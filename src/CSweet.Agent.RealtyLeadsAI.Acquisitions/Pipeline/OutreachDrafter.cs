using System.Text.Json.Serialization;
using CSweet.Agent.SDK;

namespace CSweet.Agent.RealtyLeadsAI.Acquisitions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LeadLookup(string Id);

/// <summary>
/// Drafts an outreach letter as an artifact for the owner to edit and approve in the document workspace. The manager
/// never sends anything. The draft is a fixed respectful template filled from the lead card; no model, no legal claims.
/// </summary>
public sealed class OutreachDrafter
{
    public const string DocumentType = "realtyleads.outreach-draft.v1";
    public const string ProviderLookup = "realtyleads.inheritance.lookup.v1";

    public async Task<string> DraftAsync(OutreachCommand command, Guid messageId, Guid? conversationId, PipelineBoard board, AgentRuntimeContext context, CancellationToken ct)
    {
        var card = await board.FindAsync(command.LeadId, context, ct);
        if (card is null) return $"No pipeline card exists for lead {command.LeadId}, so I have nothing to draft from. Assign research first.";
        if (PipelineStages.IsTerminal(card.Stage)) return $"Lead {command.LeadId} is {card.Stage}; I won't draft outreach for a closed lead.";

        // Refresh the record through the granted read when available; the card is the fallback and is never overwritten here.
        InheritanceLead? current = null;
        try { current = await context.Platform.InvokeAsync<LeadLookup, InheritanceLead?>(ProviderLookup, new(command.LeadId), ct); }
        catch (PlatformCapabilityException) { current = null; }
        var owner = current?.OwnerName ?? Field(card.Item.Description, "Owner name");
        var address = current?.Address ?? card.Address;
        var content = Letter(command.Tone, command.From, owner, address, current?.CityName ?? Field(card.Item.Description, "Location")?.Split(',')[0]);
        try
        {
            var document = await context.Platform.Artifacts.CreateAsync(new($"Outreach draft: {address}", content, DocumentType,
                $"outreach-draft:{command.LeadId}:{messageId:N}", OriginConversationId: conversationId), ct);
            return $"Outreach draft saved as artifact {document.Id:D} for lead {command.LeadId}. Review, edit and approve it in the document workspace; I will not send anything. " +
                   $"When you have sent it yourself, move the lead with `{PipelineCommand.Prefix} lead={command.LeadId} stage=Contacted`.";
        }
        catch (PlatformCapabilityException e) when (e.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.NotFound)
        {
            return "I cannot save documents yet (artifact creation is not granted). Here is the draft text for you to copy:\n\n" + content;
        }
    }

    private static string? Field(string description, string label)
    {
        foreach (var line in (description ?? "").Split('\n'))
            if (line.StartsWith(label + ": ", StringComparison.Ordinal))
            { var value = line[(label.Length + 2)..].Trim(); return value == "Not supplied" ? null : value; }
        return null;
    }

    internal static string Letter(string tone, string? from, string? owner, string? address, string? city)
    {
        var greeting = owner is null ? "Dear property owner," : $"Dear {owner},";
        var place = address is null ? "your property" : $"the property at {address}";
        var sender = from ?? "[Your name]";
        var body = tone == "formal"
            ? $"I am writing regarding {place}. I understand that managing a property during a family transition can involve difficult decisions, and I want to be respectful of your time and circumstances.\n\n" +
              "I am a local buyer interested in purchasing homes in the area as they are, without repairs, showings or agent commissions, on a timeline that suits the seller. If you are considering selling now or at any point, I would welcome a brief conversation about whether a straightforward offer could be helpful to you.\n\n" +
              "If this is not of interest, please disregard this letter; I will not follow up unless you ask me to."
            : $"I hope this note finds you well. I'm reaching out about {place}{(city is null ? "" : $" in {city}")}.\n\n" +
              "I know that handling a property after losing a family member can be a lot to take on, and I don't want to add to it. I buy homes locally, as they are, with no repairs, showings or agent fees, and on whatever timeline works for the family. If selling is something you're weighing, now or later, I'd be glad to talk and share a simple, no-pressure offer.\n\n" +
              "If it's not the right time, please just set this aside. I won't follow up unless you'd like me to.";
        return $"# Outreach draft\n\n_Draft for your review. Edit freely; nothing is sent until you send it yourself. Recipient details come from provider records and are not verified._\n\n" +
               $"{greeting}\n\n{body}\n\nWith respect,\n{sender}\n[Your phone / email]\n";
    }
}
