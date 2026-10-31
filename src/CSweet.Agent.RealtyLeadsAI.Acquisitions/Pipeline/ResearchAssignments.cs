using System.Globalization;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.RealtyLeadsAI.Acquisitions;

/// <summary>Turns a plain-language owner request into a research brief. Seam for tests; the default uses the approved model.</summary>
public interface IBriefExtractor
{
    /// <summary>Returns a validated, grounded brief, or null when the request is not a clear research ask.</summary>
    Task<ResearchBrief?> ExtractAsync(string text, CommunicationMessageReceivedEvent incoming, AgentRuntimeContext context, CancellationToken ct);
}

/// <summary>
/// The model's only job is to rewrite the owner's sentence as a <c>research-brief.v1</c> line or the word CLARIFY.
/// The line is then parsed by the deterministic grammar and every filter must be quoted from the owner's message,
/// so the model can neither invent criteria nor smuggle instructions. Small models handle this well.
/// </summary>
public sealed class ModelBriefExtractor : IBriefExtractor
{
    private const string SystemPrompt = """
        You convert a business owner's request into exactly one line for a lead research analyst, or the single word CLARIFY.
        Line format: research-brief.v1 state=XX [county="Name"] [city="Name"] [status="Value"] [minScore=N] [limit=N]
        Rules: state is the two-letter US state abbreviation implied or stated. Copy county, city and status values verbatim from the
        request. minScore only if the request states a score threshold. limit is the number of leads requested (default 10, maximum 100).
        If the request is not asking to find, research or search for property leads, or has no identifiable state, answer CLARIFY.
        Never add filters that were not requested. Output only the line or CLARIFY, nothing else.
        """;

    public async Task<ResearchBrief?> ExtractAsync(string text, CommunicationMessageReceivedEvent incoming, AgentRuntimeContext context, CancellationToken ct)
    {
        if (incoming.ProviderProfileId == Guid.Empty) throw new InvalidOperationException("No approved model is configured.");
        using var client = context.CreateChatClient(new(incoming.ProviderProfileId, null, new(Guid.Parse(incoming.ConversationId), incoming.TurnId, "acquisitions-brief")));
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, text)],
            new ChatOptions { MaxOutputTokens = 200, Temperature = 0, Reasoning = new ReasoningOptions { Output = ReasoningOutput.None } }, ct);
        var line = response.Text.Trim().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(ResearchBrief.Prefix, StringComparison.Ordinal));
        if (line is null || line.Length > 1024) return null;
        return BriefGrounding.Ground(line, text);
    }
}

/// <summary>Accepts a proposed brief only when every textual filter appears in the owner's own words.</summary>
public static class BriefGrounding
{
    public static ResearchBrief? Ground(string proposedLine, string source)
    {
        if (!ResearchBrief.TryParse(proposedLine, out var brief, out _) || brief is null) return null;
        bool Quoted(string? value) => value is null || Regex.IsMatch(source, @"(?<![\p{L}\p{N}])" + Regex.Escape(value) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);
        // State may be inferred from a city or state name the owner typed; everything else must be literal.
        if (!Quoted(brief.County) || !Quoted(brief.City) || !Quoted(brief.Status)) return null;
        if (brief.MinScore is { } score && !Quoted(score.ToString(CultureInfo.InvariantCulture))) return null;
        if (!Regex.IsMatch(source, @"\d") && brief.Limit != ResearchBrief.DefaultLimit) brief = brief with { Limit = ResearchBrief.DefaultLimit };
        return brief with { Purpose = null };
    }
}

/// <summary>Finds the Lead Research Analyst this manager can assign to, from the authenticated organization snapshot.</summary>
public static class AnalystLocator
{
    public const string RoleName = "Lead Research Analyst";

    public static async Task<OrganizationPerson?> FindAsync(AgentRuntimeContext context, CancellationToken ct)
    {
        var snapshot = await context.Platform.ReadOrganizationSnapshotAsync(ct);
        var roles = snapshot.Roles.Where(r => r.Name.Contains("Research Analyst", StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToHashSet();
        var agents = snapshot.People.Where(p => p.IsActive && p.EmployeeType == "Agent" &&
            (p.RoleId is { } role && roles.Contains(role) || p.DisplayName.Contains("Research Analyst", StringComparison.OrdinalIgnoreCase))).ToArray();
        var self = Guid.TryParse(context.Identity?.EmployeeId, out var me) ? me : Guid.Empty;
        return agents.FirstOrDefault(a => a.ReportsToId == self) ?? (agents.Length == 1 ? agents[0] : null);
    }
}

/// <summary>Assigns a brief to the analyst with tracking. The instruction is the canonical brief line, nothing else.</summary>
public static class ResearchAssignments
{
    public static async Task<string> AssignAsync(ResearchBrief brief, CommunicationMessageReceivedEvent incoming, AgentRuntimeContext context, CancellationToken ct)
    {
        if (incoming.MessageId == Guid.Empty || !Guid.TryParse(incoming.ConversationId, out var chat))
            return "I can only assign research from a saved chat message. Please send the request again.";
        var analyst = await AnalystLocator.FindAsync(context, ct);
        if (analyst is null)
            return $"No {AnalystLocator.RoleName} reports to me yet, so I cannot assign this research. Hire or install the RealtyLeadsAI Lead Research Analyst and grant it the RealtyLeadsAI connection, then ask again.";
        var instruction = brief.ToInstruction();
        var outcome = $"A lead brief for {brief.Scope()} with up to {brief.Limit} subscribed inheritance leads" +
                      (brief.MinScore is { } s ? $" scoring at least {s.ToString(CultureInfo.InvariantCulture)}" : "") + ".";
        await context.Platform.Delegation.DelegateWithTrackingAsync(new(analyst.Id, instruction, outcome, chat, incoming.MessageId,
            $"delegation-source:{incoming.MessageId:N}", WorkContext: incoming.WorkContext is { } wc ? new(wc.WorkstreamId, wc.TeamId, wc.WorkItemId) : null), ct);
        return $"Assigned to {analyst.DisplayName}: `{instruction}`. When the lead brief comes back I will add each new lead to the pipeline and let you know.";
    }
}
