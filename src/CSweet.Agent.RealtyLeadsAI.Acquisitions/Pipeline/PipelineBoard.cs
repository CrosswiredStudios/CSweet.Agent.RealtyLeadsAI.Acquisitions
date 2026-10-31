using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.RealtyLeadsAI.Acquisitions;

/// <summary>One lead's pipeline card as the manager understands it.</summary>
public sealed record PipelineCard(PersonalTodoItem Item, string LeadId, string Stage)
{
    public string Address => Regex.Match(Item.Title, @"^\[[^\]]+\]\s*(?<rest>.*)$").Groups["rest"].Value is { Length: > 0 } rest ? rest : Item.Title;
}

/// <summary>
/// The acquisitions pipeline lives as cards on the manager's personal workboard, one per lead, correlated by lead ID
/// so repeated briefs never duplicate a lead. Cards start in the backlog: they are owner-driven records, not agent tasks.
/// </summary>
public sealed class PipelineBoard
{
    public const string CorrelationPrefix = "acquisition-lead.v1:";
    private static readonly Regex TitleStage = new(@"^\[(?<stage>[^\]]+)\]", RegexOptions.CultureInvariant);
    private const int MaximumTextLength = 200;

    public static string Correlation(string leadId) => CorrelationPrefix + leadId.ToLowerInvariant();
    public static string Title(string stage, InheritanceLead lead) =>
        $"[{stage}] {Clip(lead.Address ?? "Address not supplied")} ({Clip(lead.CityName ?? "city?")}, {Clip(lead.StateAbbreviation ?? "state?")})";
    private static string Clip(string value) => new(value.Where(c => !char.IsControl(c)).Take(MaximumTextLength).ToArray());
    private static string RetitleStage(string title, string stage) => TitleStage.IsMatch(title) ? TitleStage.Replace(title, $"[{stage}]", 1) : $"[{stage}] {title}";

    /// <summary>All current lead cards owned by this manager, newest first.</summary>
    public async Task<IReadOnlyList<PipelineCard>> ListAsync(AgentRuntimeContext context, CancellationToken ct, bool includeArchived = false)
    {
        var directory = await context.Platform.PersonalTodo.ListAsync(ct);
        var self = context.Identity?.EmployeeId;
        return directory.Boards.SelectMany(b => b.Items)
            .Where(i => (includeArchived || i.ArchivedAt is null) && (self is null || i.OwnerOrganizationUserId.ToString("D") == self) &&
                        i.CorrelationId?.StartsWith(CorrelationPrefix, StringComparison.Ordinal) == true)
            .Select(i => new PipelineCard(i, i.CorrelationId![CorrelationPrefix.Length..], TitleStage.Match(i.Title).Groups["stage"].Value is { Length: > 0 } s ? s : PipelineStages.New))
            .OrderByDescending(c => c.Item.UpdatedAt).ToArray();
    }

    public async Task<PipelineCard?> FindAsync(string leadId, AgentRuntimeContext context, CancellationToken ct) =>
        (await ListAsync(context, ct)).FirstOrDefault(c => c.LeadId.Equals(leadId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds cards for leads not yet on the board. Returns how many were added; existing cards are untouched.</summary>
    public async Task<int> IngestAsync(IReadOnlyList<InheritanceLead> leads, Guid briefArtifactId, Guid? sourceConversation, Guid? sourceMessage,
        AgentRuntimeContext context, CancellationToken ct)
    {
        // Archived (closed or dead) leads stay known, so a repeated brief never resurrects them as new.
        var existing = (await ListAsync(context, ct, includeArchived: true)).Select(c => c.LeadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var lead in leads.DistinctBy(l => l.InheritanceLeadId, StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Contains(lead.InheritanceLeadId)) continue;
            var description = Describe(lead, PipelineStages.New, $"Added from lead brief {briefArtifactId:D}.", null);
            await context.Platform.PersonalTodo.AddAsync(new(Title(PipelineStages.New, lead), description, WorkPriorities.Medium, null,
                $"acquisition-lead-add:{lead.InheritanceLeadId.ToLowerInvariant()}", null,
                sourceMessage is null ? null : sourceConversation, sourceMessage is null ? null : sourceMessage, Correlation(lead.InheritanceLeadId))
            { StartInBacklog = true }, ct);
            added++;
        }
        return added;
    }

    /// <summary>Moves a card to a stage; terminal stages archive it. Idempotent for the same message.</summary>
    public async Task<string> MoveAsync(PipelineCommand command, Guid messageId, AgentRuntimeContext context, CancellationToken ct)
    {
        var card = await FindAsync(command.LeadId, context, ct);
        if (card is null) return $"No pipeline card exists for lead {command.LeadId}. Assign research first, or check the ID on the card.";
        if (card.Stage == command.Stage && command.Note is null) return $"Lead {command.LeadId} is already in {command.Stage}.";
        var history = $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC: {card.Stage} → {command.Stage}" + (command.Note is null ? "" : $" — {Clip(command.Note)}");
        var description = AppendHistory(card.Item.Description, command.Stage, history);
        var updated = await context.Platform.PersonalTodo.UpdateAsync(new(card.Item.Id, RetitleStage(card.Item.Title, command.Stage), description,
            card.Item.Priority, card.Item.DueDate, card.Item.Revision, $"acquisition-lead-move:{command.LeadId}:{messageId:N}"), ct);
        if (PipelineStages.IsTerminal(command.Stage))
        {
            await context.Platform.PersonalTodo.ArchiveAsync(new(updated.Id, updated.Revision, $"acquisition-lead-archive:{command.LeadId}:{messageId:N}"), ct);
            return $"Lead {command.LeadId} moved to {command.Stage} and archived from the active pipeline.";
        }
        return $"Lead {command.LeadId} moved from {card.Stage} to {command.Stage}.";
    }

    /// <summary>Counts by stage, plain text, no model.</summary>
    public async Task<string> SummaryAsync(AgentRuntimeContext context, CancellationToken ct)
    {
        var cards = await ListAsync(context, ct);
        if (cards.Count == 0) return "The acquisitions pipeline is empty. Ask me to research an area (for example: find 10 leads in Travis County TX with score over 70) and I will assign it to the Lead Research Analyst.";
        var b = new StringBuilder($"Acquisitions pipeline: {cards.Count} active lead(s).\n\n");
        foreach (var stage in PipelineStages.All.Where(s => !PipelineStages.IsTerminal(s)))
        {
            var inStage = cards.Where(c => c.Stage == stage).ToArray();
            if (inStage.Length == 0) continue;
            b.Append($"**{stage}** ({inStage.Length})\n");
            foreach (var c in inStage.Take(10)) b.Append($"- {c.Address} — lead {c.LeadId}\n");
            if (inStage.Length > 10) b.Append($"- …and {inStage.Length - 10} more\n");
            b.Append('\n');
        }
        b.Append($"Move a lead with `{PipelineCommand.Prefix} lead=<id> stage=<{string.Join('|', PipelineStages.All)}>` or ask for an outreach draft with `{OutreachCommand.Prefix} lead=<id>`.");
        return b.ToString();
    }

    public static string Describe(InheritanceLead l, string stage, string origin, string? history)
    {
        static string V(string? s) => string.IsNullOrWhiteSpace(s) ? "Not supplied" : Clip(s);
        var score = l.Score is { } s ? s.ToString(CultureInfo.InvariantCulture) : "Not supplied";
        return $"Stage: {stage}\nLead ID: {l.InheritanceLeadId}\nAddress: {V(l.Address)}\nOwner name: {V(l.OwnerName)}\nProvider status: {V(l.Status)}\nScore (provider-supplied): {score}\n" +
               $"Deceased: {V(l.DeceasedName)} ({V(l.DeceasedId)})\nProperty ID: {V(l.PropertyId)}\nLocation: {V(l.CityName)}, {V(l.CountyName)}, {V(l.StateAbbreviation)}\n" +
               $"{origin}\nProvider records are not verified ownership or legal conclusions.\n\nHistory:\n{history ?? "- Created"}";
    }

    private static string AppendHistory(string description, string stage, string line)
    {
        var updated = Regex.Replace(description ?? "", @"^Stage: .*$", $"Stage: {stage}", RegexOptions.Multiline);
        return (updated.Contains("\nHistory:", StringComparison.Ordinal) ? updated : updated + "\n\nHistory:\n- Created") + "\n- " + line;
    }
}
