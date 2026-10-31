using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.RealtyLeadsAI.Acquisitions;

/// <summary>
/// Acquisitions Manager. Owns the real-estate acquisitions pipeline for the business: turns the owner's goals into
/// research assignments for the Lead Research Analyst, keeps one pipeline card per lead on its workboard, drafts
/// outreach for the owner to approve, and reports status each management cycle. It never contacts a lead itself.
/// </summary>
public sealed class AcquisitionsManagerAgent : CSweetManagerAgentBase
{
    public const string Id = "com.crosswiredstudios.agent.realtyleads.acquisitions";
    public const string CurrentVersion = "0.1.0";
    public const int MaximumMessageLength = 4000;
    internal const string CredentialGuidance = "Enter credentials only in native RealtyLeadsAI account settings and grant the Lead Research Analyst read access there. Never send a key in chat.";
    internal const string Help = "I run the acquisitions pipeline. Tell me where to look (for example: find 10 leads in Travis County TX with score over 70), ask for the pipeline, move a lead with `pipeline.v1 lead=<id> stage=Qualified`, or request a letter with `outreach-draft.v1 lead=<id>`. I assign research to the Lead Research Analyst and never contact leads myself.";

    private static readonly Regex CredentialWords = new("api[ -]?key|secret|password", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LongToken = new("[A-Za-z0-9_+.-]{32,}", RegexOptions.CultureInvariant);
    // A bare status question; "pipeline.v1 ..." and other versioned commands are not questions.
    private static readonly Regex StatusAsk = new(@"^\s*(pipeline|status|report|where are we|what('s| is) in the pipeline)\b(?!\.v\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IBriefExtractor _extractor;
    private readonly PipelineBoard _board = new();
    private readonly OutreachDrafter _drafter = new();

    public override string AgentId => Id;
    public override string Version => CurrentVersion;
    protected override string ManagementResponsibility => "the real-estate acquisitions pipeline: lead research assignments, pipeline tracking and outreach drafts for owner approval";

    public AcquisitionsManagerAgent() : this(new ModelBriefExtractor()) { }
    public AcquisitionsManagerAgent(IBriefExtractor extractor) => _extractor = extractor;

    // ---- Typed capability for other agents (for example the Chief of Staff) ----

    public const string PipelineSummary = "acquisitions.pipeline.summary.v1";
    public sealed record StageCount(string Stage, int Count, IReadOnlyList<string> LeadIds);
    public sealed record PipelineSummaryResult(int ActiveLeads, IReadOnlyList<StageCount> Stages, string Summary);

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(AgentCapabilityRequest request, AgentRuntimeContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Capability != PipelineSummary) return AgentWorkResult.Failure("The requested capability is not supported.");
        try
        {
            var cards = await _board.ListAsync(context, ct);
            var stages = PipelineStages.All.Select(s => new StageCount(s, cards.Count(c => c.Stage == s), cards.Where(c => c.Stage == s).Select(c => c.LeadId).Take(100).ToArray())).Where(s => s.Count > 0).ToArray();
            var summary = await _board.SummaryAsync(context, ct);
            return AgentWorkResult.Success(new PipelineSummaryResult(cards.Count(c => !PipelineStages.IsTerminal(c.Stage)), stages, summary.Length > 4096 ? summary[..4096] : summary));
        }
        catch (PlatformCapabilityException) { return AgentWorkResult.Failure("The pipeline board is unavailable; review this manager's personal work grants."); }
    }

    // ---- Events ----

    protected override async Task HandleManagerEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken ct)
    {
        if (message.EventType == CommunicationEvents.MessageReceived)
        {
            var incoming = DeserializePayload<CommunicationMessageReceivedEvent>(message.Data);
            if (incoming is null || !Guid.TryParse(incoming.ConversationId, out _) || incoming.TurnId == Guid.Empty) return;
            await RespondAsync(incoming, null, context, ct);
            return;
        }
        if (message.EventType == ManagementEvents.ReviewDue)
        {
            var due = DeserializePayload<ManagementReviewDueEvent>(message.Data);
            if (due is not null) await ReportStatusAsync(due, context, ct);
            return;
        }
        await base.HandleManagerEventAsync(message, context, ct);
    }

    public override Task HandleDelegationExecutionAsync(DelegationSnapshot delegation, CommunicationMessageReceivedEvent incoming,
        AgentEventEnvelope envelope, AgentRuntimeContext context, CancellationToken ct) => RespondAsync(incoming, delegation, context, ct);

    // ---- Chat routing: deterministic first, model last ----

    private async Task RespondAsync(CommunicationMessageReceivedEvent incoming, DelegationSnapshot? delegation, AgentRuntimeContext context, CancellationToken ct)
    {
        await using var stream = context.CreateTurnStream(incoming.ConversationId, incoming.TurnId, incoming.Attempt, "Confidential");
        var text = delegation?.Instruction ?? incoming.Context?.GetValueOrDefault("currentUserMessage") ?? incoming.Message;
        string reply;
        try { reply = await RouteAsync(text, incoming, delegation, context, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PlatformCapabilityException e) when (e.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.NotFound)
        { reply = "I don't have the access needed for that yet. Review my installation grants (delegation, personal work, artifacts and the RealtyLeadsAI read) and try again."; }
        catch (PlatformCapabilityException) { reply = "A platform service was unavailable, so nothing was changed. Please try again shortly."; }
        catch (JsonException) { reply = Help; }
        catch (InvalidOperationException) { reply = "I could not interpret that. " + Help; }
        await stream.CommitAsync(reply, ct);
    }

    internal static bool LooksLikeCredential(string text) =>
        CredentialWords.IsMatch(text) || LongToken.Matches(text).Any(m => !Guid.TryParseExact(m.Value, "D", out _));

    /// <summary>
    /// Only authenticated humans and this manager's own manager may direct the pipeline. Missing sender context is
    /// treated as unverified (information only); a delegation counts when it is a reporting-chain directive or comes
    /// from the manager's manager.
    /// </summary>
    internal static bool MayDirect(CommunicationMessageReceivedEvent incoming, AgentRuntimeContext context, DelegationSnapshot? delegation)
    {
        if (delegation is not null) return IsAuthorizedDelegation(delegation, context);
        var type = incoming.Context?.GetValueOrDefault("senderEmployeeType");
        var sender = incoming.Context?.GetValueOrDefault("senderOrganizationUserId");
        if (type is not null && type.Equals("Human", StringComparison.OrdinalIgnoreCase)) return true;
        return sender is not null && context.Identity?.ManagerEmployeeId is { } manager && sender.Equals(manager, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAuthorizedDelegation(DelegationSnapshot delegation, AgentRuntimeContext context) =>
        delegation.IsDirective || context.Identity?.ManagerEmployeeId is { } manager &&
        (delegation.CoordinatorId.ToString("D").Equals(manager, StringComparison.OrdinalIgnoreCase) ||
         delegation.OriginalRequesterId.ToString("D").Equals(manager, StringComparison.OrdinalIgnoreCase));

    private async Task<string> RouteAsync(string? text, CommunicationMessageReceivedEvent incoming, DelegationSnapshot? delegation, AgentRuntimeContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumMessageLength) return Help;
        if (LooksLikeCredential(text)) return CredentialGuidance;
        var conversation = Guid.TryParse(incoming.ConversationId, out var chat) ? chat : (Guid?)null;
        if (StatusAsk.IsMatch(text)) return await _board.SummaryAsync(context, ct);
        if (!MayDirect(incoming, context, delegation)) return "Only the business owner or my manager can direct the acquisitions pipeline. " + await _board.SummaryAsync(context, ct);

        if (ResearchBrief.TryParse(text, out var brief, out var briefError))
            return brief is null ? $"{briefError} Example: {ResearchBrief.Prefix} state=TX county=\"Travis\" minScore=70 limit=10" : await ResearchAssignments.AssignAsync(brief, incoming, delegation?.Id, context, ct);
        if (PipelineCommand.TryParse(text, out var move, out var moveError))
            return move is null ? $"{moveError} Example: {PipelineCommand.Prefix} lead=<id> stage=Qualified" : await _board.MoveAsync(move, incoming.MessageId, context, ct);
        if (OutreachCommand.TryParse(text, out var outreach, out var outreachError))
            return outreach is null ? $"{outreachError} Example: {OutreachCommand.Prefix} lead=<id>" : await _drafter.DraftAsync(outreach, incoming.MessageId, conversation, _board, context, ct);

        var extracted = await _extractor.ExtractAsync(text, incoming, context, ct);
        if (extracted is not null) return await ResearchAssignments.AssignAsync(extracted, incoming, delegation?.Id, context, ct);
        return "I couldn't turn that into a research assignment. Tell me the state and the county or city, how many leads you want, and any minimum score — or send it exactly as " +
               $"`{ResearchBrief.Prefix} state=TX county=\"Travis\" minScore=70 limit=10`. {Help}";
    }

    // ---- Delegation protocol: deterministic on both sides ----

    /// <summary>This manager assigns research in code; the shared mention-based classifier never runs a model here.</summary>
    public override Task<DelegationIntent> ClassifyDelegationIntentAsync(CommunicationMessageReceivedEvent incoming, AgentRuntimeContext context, CancellationToken token) =>
        Task.FromResult(new DelegationIntent("Other", null, null, null, null));

    /// <summary>When another manager delegates to this one, accept acquisitions work and handle it on the execution turn.</summary>
    public override Task<DelegationResponse> HandleDelegationReceivedAsync(DelegationSnapshot delegation, AgentRuntimeContext context, CancellationToken token)
    {
        if (LooksLikeCredential(delegation.Instruction ?? "")) return Task.FromResult<DelegationResponse>(new DelegationResponse.Decline(CredentialGuidance));
        if (!IsAuthorizedDelegation(delegation, context))
            return Task.FromResult<DelegationResponse>(new DelegationResponse.Decline("Only the business owner or my manager can direct the acquisitions pipeline. Ask them to route the request through me."));
        return Task.FromResult<DelegationResponse>(new DelegationResponse.Acceptance(
            "I will turn this into a research assignment for the Lead Research Analyst and track the resulting leads in the acquisitions pipeline. No outreach is sent without owner approval."));
    }

    public override Task<Guid?> ResolveDelegationWorkAsync(DelegationSnapshot delegation, string response, AgentRuntimeContext context, CancellationToken token) =>
        Task.FromResult(delegation.WorkItemId);

    /// <summary>Every execution turn commits a reply, so this fallback is unreachable; it stays deterministic regardless.</summary>
    public override async Task HandleDelegationWorkIntakeAsync(DelegationSnapshot delegation, CommunicationMessageReceivedEvent incoming, AgentRuntimeContext context, CancellationToken token)
    {
        await using var stream = context.CreateTurnStream(incoming.ConversationId, incoming.TurnId, incoming.Attempt, "Confidential");
        await stream.CommitAsync("I could not act on this request. " + Help, token);
    }

    /// <summary>
    /// As a recipient, assigning research is progress, not completion: the outcome is reported when the resulting lead
    /// brief is ingested (see <see cref="SummarizeDelegationUpdateAsync"/>). A reply that made no assignment is a final failure.
    /// </summary>
    public override Task<DelegationResponse.FinalOutcome?> ReviewDelegationOutcomeAsync(DelegationSnapshot delegation, AgentRuntimeContext context, CancellationToken token)
    {
        var content = delegation.ExecutionEvidence?.Content;
        if (content is null || content.StartsWith("Assigned to ", StringComparison.Ordinal)) return Task.FromResult<DelegationResponse.FinalOutcome?>(null);
        return Task.FromResult<DelegationResponse.FinalOutcome?>(new("The request could not be turned into a research assignment; see the manager's reply.", false));
    }

    /// <summary>
    /// As the coordinator of analyst assignments, this is where a delivered lead brief arrives. The leads are read from
    /// the artifact's data block and added to the pipeline idempotently; the owner's report names what changed.
    /// </summary>
    public override async Task<string> SummarizeDelegationUpdateAsync(DelegationSnapshot delegation, TrackingUpdate update, AgentRuntimeContext context, CancellationToken token)
    {
        // The runtime may hand us only the newest progress update; markers in any recorded update are ingested idempotently.
        var markers = delegation.Updates.Select(u => LeadBriefData.ParseMarker(u.Summary)).Append(LeadBriefData.ParseMarker(update.Summary))
            .Where(m => m.HasValue).Select(m => m!.Value).Distinct().ToArray();
        if (markers.Length == 0) return update.Summary;
        var lines = new List<string>();
        foreach (var artifactId in markers)
        {
            IReadOnlyList<InheritanceLead>? leads;
            try
            {
                var document = await context.Platform.Artifacts.GetAsync(artifactId, token);
                var revision = document.Revisions.OrderByDescending(r => r.Number).FirstOrDefault(r => r.Id == document.LatestRevisionId) ?? document.Revisions.OrderByDescending(r => r.Number).FirstOrDefault();
                leads = document.DocumentType == LeadBriefData.DocumentType ? LeadBriefData.ParseLeads(revision?.Content) : null;
            }
            catch (PlatformCapabilityException) { leads = null; }
            if (leads is null) { lines.Add($"{delegation.RecipientName} delivered a lead brief (artifact {artifactId:D}), but I could not read its data block, so no pipeline cards were added. Open the brief to review the leads."); continue; }
            var added = await _board.IngestAsync(leads, artifactId, delegation.ReportingConversationId, null, context, token);
            lines.Add($"{delegation.RecipientName} delivered a lead brief with {leads.Count} lead(s) for \"{delegation.IntendedOutcome}\". Added {added} new card(s) to the acquisitions pipeline" +
                      (added < leads.Count ? $"; {leads.Count - added} already tracked" : "") + $". Review the brief (artifact {artifactId:D}) and tell me which leads to qualify.");
            await CompleteParentAsync(delegation, artifactId, lines[^1], context, token);
        }
        return string.Join("\n\n", lines);
    }

    /// <summary>When this assignment was made on behalf of an upstream delegation, report that one complete now that leads are tracked.</summary>
    private static async Task CompleteParentAsync(DelegationSnapshot delegation, Guid artifactId, string summary, AgentRuntimeContext context, CancellationToken token)
    {
        if (delegation.ParentDelegationId is not { } parentId) return;
        try
        {
            var parent = (await context.Platform.Delegation.ReadAsync(new(parentId), token)).SingleOrDefault();
            if (parent is null || parent.Status is "Completed" or "Cancelled" or "Declined") return;
            await context.Platform.Delegation.RespondAsync(new(parent.Id, parent.Revision, "Completed", summary, $"delegation-outcome:{parent.Id:N}:{artifactId:N}"), token);
        }
        catch (PlatformCapabilityException e) when (e.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.NotFound or PlatformCapabilityErrorCode.Conflict) { }
    }

    // ---- Management cycle ----

    private async Task ReportStatusAsync(ManagementReviewDueEvent due, AgentRuntimeContext context, CancellationToken ct)
    {
        IReadOnlyList<PipelineCard> cards;
        try { cards = await _board.ListAsync(context, ct); } catch (PlatformCapabilityException) { cards = []; }
        var byStage = PipelineStages.All.ToDictionary(s => s, s => cards.Count(c => c.Stage == s));
        var active = cards.Count(c => !PipelineStages.IsTerminal(c.Stage));
        var report = new ManagementStatusReport(due.CycleId,
            active == 0 ? "The acquisitions pipeline has no active leads. Research assignments start from an owner request naming a state and area."
                        : $"Acquisitions pipeline: {active} active lead(s); {byStage[PipelineStages.Qualified]} qualified, {byStage[PipelineStages.OutreachPlanned] + byStage[PipelineStages.Contacted]} in outreach, {byStage[PipelineStages.Negotiating]} negotiating.",
            CompletedOutcomes: byStage[PipelineStages.Closed] > 0 ? [$"{byStage[PipelineStages.Closed]} lead(s) closed"] : [],
            InProgress: PipelineStages.All.Where(s => !PipelineStages.IsTerminal(s) && byStage[s] > 0).Select(s => $"{byStage[s]} in {s}").ToArray(),
            Blockers: [],
            Risks: active > 0 && byStage[PipelineStages.New] == active ? ["Every active lead is still unqualified; owner review of the lead briefs is the next step."] : [],
            ResourceNeeds: [],
            DecisionsNeeded: byStage[PipelineStages.New] > 0 ? [$"Qualify or dismiss {byStage[PipelineStages.New]} new lead(s)"] : [],
            Assumptions: ["Lead data are provider records, not verified ownership.", "No outreach is sent without owner approval."],
            Confidence: 0.9m, ReportedAt: DateTimeOffset.UtcNow);
        await context.Platform.InvokeAsync<ManagementStatusReport, JsonElement>(CapabilityNames.Management.StatusReport, report, ct);
    }
}
