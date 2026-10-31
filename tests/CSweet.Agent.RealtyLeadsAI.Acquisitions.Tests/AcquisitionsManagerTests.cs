using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.RealtyLeadsAI.Acquisitions.Tests;

public sealed class AcquisitionsManagerTests
{
    private static readonly Guid Owner = Guid.NewGuid(), Manager = Guid.NewGuid(), Analyst = Guid.NewGuid(), Chat = Guid.NewGuid();
    private static readonly Guid LeadA = Guid.Parse("00000000-0000-0000-0000-00000000000a"), LeadB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static AgentIdentity Identity() => new(Manager.ToString("D"), "Acquisitions Manager", null, null, null, [], null, Owner.ToString("D"), "Owner");
    private static CommunicationMessageReceivedEvent Message(string text, string senderType = "Human") =>
        new(Guid.NewGuid(), Chat.ToString(), Owner.ToString(), text, new Dictionary<string, string> { ["senderEmployeeType"] = senderType, ["senderOrganizationUserId"] = Owner.ToString("D") }, Guid.NewGuid(), 0, Guid.NewGuid());
    private static OrganizationSnapshotResponse Snapshot(bool withAnalyst = true) => new(Guid.NewGuid(), "Active",
        withAnalyst ? [new(Analyst, "Lead Research Analyst", "Agent", Guid.Parse("00000000-0000-0000-0000-0000000000f1"), Manager, Guid.NewGuid(), true),
                       new(Guid.NewGuid(), "Other Researcher", "Agent", Guid.Parse("00000000-0000-0000-0000-0000000000f1"), Guid.NewGuid(), Guid.NewGuid(), true)] : [],
        [new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"), "Lead Research Analyst", "Researches leads", "[]")], [], [], [], DateTimeOffset.UtcNow);
    private static PersonalTodoItem Card(Guid lead, string stage, string address = "1 Fixture St") => new(Guid.NewGuid(), Guid.NewGuid(), Manager, Manager, "Acquisitions Manager",
        $"[{stage}] {address} (Austin, TX)", PipelineBoard.Describe(new(lead.ToString("D"), null, address, "Fixture owner", "Verified", 88, null, "Fixture person", "Austin", "Travis", "TX"), stage, "Added from lead brief.", null),
        PersonalTodoStatuses.Backlog, WorkPriorities.Medium, 1, 3, null, null, null, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { CorrelationId = PipelineBoard.Correlation(lead.ToString("D")) };
    private static AgentTestRuntime Runtime(params PersonalTodoItem[] cards) => new AgentTestRuntime()
        .RegisterCapability<object, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(new PersonalTodoDirectory([new(Guid.NewGuid(), Manager, "Acquisitions Manager", null, null, 1, cards)])))
        .RegisterCapability<object, OrganizationSnapshotResponse>(PlatformCapabilities.OrganizationSnapshotRead, (_, _) => Task.FromResult(Snapshot()))
        .RegisterCapability<object, object>(DelegationCapabilities.Create, (_, _) => Task.FromResult<object>(new { }))
        .RegisterCapability<ReadDelegationRequest, IReadOnlyList<DelegationSnapshot>>(DelegationCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<DelegationSnapshot>>([]));
    private static AcquisitionsManagerAgent AgentWithoutModel() => new(new NoModel());
    private sealed class NoModel : IBriefExtractor
    { public Task<ResearchBrief?> ExtractAsync(string t, CommunicationMessageReceivedEvent i, AgentRuntimeContext c, CancellationToken ct) => throw new Xunit.Sdk.XunitException("The model must not run for structured commands."); }
    private sealed class FixedModel(string line) : IBriefExtractor
    { public Task<ResearchBrief?> ExtractAsync(string t, CommunicationMessageReceivedEvent i, AgentRuntimeContext c, CancellationToken ct) => Task.FromResult(BriefGrounding.Ground(line, t)); }
    private static async Task<string> Reply(AgentTestRuntime runtime, AcquisitionsManagerAgent agent, CommunicationMessageReceivedEvent message)
    {
        await runtime.DeliverEventAsync(agent, CommunicationEvents.MessageReceived, message, identity: Identity());
        var final = Assert.Single(runtime.Progress, p => p.TryGetProperty("isFinal", out var f) && f.GetBoolean());
        return final.GetProperty("delta").GetString()!;
    }

    [Fact]
    public async Task BriefLineIsAssignedToTheAnalystVerbatimWithTracking()
    {
        DelegateWithTrackingRequest? created = null;
        var runtime = Runtime().RegisterCapability<DelegateWithTrackingRequest, DelegationSnapshot>(DelegationCapabilities.Create, (r, _) =>
        { created = r; return Task.FromResult(new DelegationSnapshot(Guid.NewGuid(), 1, Owner, Manager, Analyst, "M", "A", r.Instruction, r.IntendedOutcome, "AwaitingAcknowledgement", true, Chat, Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, [], [])); });
        var message = Message("research-brief.v1 state=TX county=\"Travis\" minScore=70 limit=10");
        var reply = await Reply(runtime, AgentWithoutModel(), message);
        Assert.Equal(Analyst, created!.RecipientOrganizationUserId);
        Assert.Equal("research-brief.v1 state=TX county=\"Travis\" minScore=70 limit=10", created.Instruction);
        Assert.Equal(message.MessageId, created.SourceMessageId); Assert.Equal($"delegation-source:{message.MessageId:N}", created.IdempotencyKey);
        Assert.Contains("Travis County, TX", created.IntendedOutcome); Assert.Contains("Assigned to Lead Research Analyst", reply);
    }

    [Fact]
    public async Task PlainLanguageGoesThroughTheExtractorAndIsGrounded()
    {
        DelegateWithTrackingRequest? created = null;
        var runtime = Runtime().RegisterCapability<DelegateWithTrackingRequest, DelegationSnapshot>(DelegationCapabilities.Create, (r, _) =>
        { created = r; return Task.FromResult(new DelegationSnapshot(Guid.NewGuid(), 1, Owner, Manager, Analyst, "M", "A", r.Instruction, r.IntendedOutcome, "AwaitingAcknowledgement", true, Chat, Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, [], [])); });
        var grounded = new FixedModel("research-brief.v1 state=TX county=\"Travis\" minScore=70 limit=10");
        var reply = await Reply(runtime, new AcquisitionsManagerAgent(grounded), Message("Find 10 leads in Travis county, Texas with a score over 70"));
        Assert.Equal("research-brief.v1 state=TX county=\"Travis\" minScore=70 limit=10", created!.Instruction); Assert.Contains("Assigned to", reply);

        created = null;
        var invented = new FixedModel("research-brief.v1 state=TX county=\"Williamson\" limit=10");
        var clarification = await Reply(Runtime().RegisterCapability<DelegateWithTrackingRequest, DelegationSnapshot>(DelegationCapabilities.Create, (r, _) => { created = r; throw new Xunit.Sdk.XunitException("no assignment"); }),
            new AcquisitionsManagerAgent(invented), Message("Find leads around Austin please"));
        Assert.Null(created); Assert.Contains("couldn't turn that into a research assignment", clarification);
    }

    [Fact]
    public void GroundingKeepsOnlyQuotedFiltersAndSafeLimits()
    {
        Assert.Null(BriefGrounding.Ground("research-brief.v1 state=TX city=\"Austin\" minScore=80", "find leads in Austin TX"));
        var b = BriefGrounding.Ground("research-brief.v1 state=TX city=\"Austin\" limit=25", "find leads in Austin TX");
        Assert.NotNull(b); Assert.Equal(ResearchBrief.DefaultLimit, b!.Limit);
        Assert.Equal(25, BriefGrounding.Ground("research-brief.v1 state=TX city=\"Austin\" limit=25", "find 25 leads in Austin TX")!.Limit);
        Assert.Null(BriefGrounding.Ground("CLARIFY", "hello"));
    }

    [Fact]
    public async Task WithoutAnAnalystNothingIsAssigned()
    {
        var runtime = new AgentTestRuntime()
            .RegisterCapability<object, OrganizationSnapshotResponse>(PlatformCapabilities.OrganizationSnapshotRead, (_, _) => Task.FromResult(Snapshot(withAnalyst: false)))
            .RegisterCapability<object, object>(DelegationCapabilities.Create, (_, _) => throw new Xunit.Sdk.XunitException("no assignment"))
            .RegisterCapability<ReadDelegationRequest, IReadOnlyList<DelegationSnapshot>>(DelegationCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<DelegationSnapshot>>([]));
        var reply = await Reply(runtime, AgentWithoutModel(), Message("research-brief.v1 state=TX"));
        Assert.Contains("No Lead Research Analyst reports to me", reply);
    }

    [Fact]
    public async Task DeliveredBriefAddsOnlyNewLeadsToThePipeline()
    {
        var artifactId = Guid.NewGuid(); var added = new List<AddPersonalTodoItemRequest>();
        var content = "# Lead brief\n\n## Data (realtyleads.lead-brief.v1)\n\n```json\n" + JsonSerializer.Serialize(new { criteria = new { state = "TX" }, leads = new[] {
            new { inheritanceLeadId = LeadA.ToString("D"), propertyId = (string?)null, address = "1 Fixture St", ownerName = "Fixture owner", status = "Verified", score = 88.0, deceasedId = (string?)null, deceasedName = "Fixture person", cityName = "Austin", countyName = "Travis", stateAbbreviation = "TX" },
            new { inheritanceLeadId = LeadB.ToString("D"), propertyId = (string?)null, address = "2 Fixture Ave", ownerName = (string?)null, status = "Verified", score = 71.0, deceasedId = (string?)null, deceasedName = (string?)null, cityName = "Austin", countyName = "Travis", stateAbbreviation = "TX" } } }) + "\n```\n";
        var runtime = Runtime(Card(LeadA, PipelineStages.Qualified))
            .RegisterCapability<object, ArtifactDocument>(PlatformCapabilities.ArtifactRead, (_, _) => Task.FromResult(new ArtifactDocument(artifactId, "Lead brief", LeadBriefData.DocumentType, "Draft", Guid.Parse("00000000-0000-0000-0000-0000000000e1"), null, null, null, null,
                [new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"), 1, null, content, "sha", "Draft", DateTimeOffset.UtcNow, null, null)])))
            .RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) => { added.Add(r); return Task.FromResult(Card(LeadB, PipelineStages.New)); });
        var delegation = new DelegationSnapshot(Guid.NewGuid(), 2, Owner, Manager, Analyst, "Acquisitions Manager", "Lead Research Analyst", "research-brief.v1 state=TX", "Travis County leads", "Active", true, Chat, Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, [], []);
        var update = new TrackingUpdate(Guid.NewGuid(), Guid.NewGuid(), delegation.Id, 1, "Progress", $"Execution response: Lead brief saved as an artifact with all reviewed fields [lead-brief:{artifactId:D}]\n\n| table |", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var summary = await AgentWithoutModel().SummarizeDelegationUpdateAsync(delegation, update, runtime.CreateContext(identity: Identity()), CancellationToken.None);

        var card = Assert.Single(added);
        Assert.Equal(PipelineBoard.Correlation(LeadB.ToString("D")), card.CorrelationId); Assert.StartsWith("[New] 2 Fixture Ave", card.Title);
        Assert.True(card.StartInBacklog); Assert.Equal($"acquisition-lead-add:{LeadB:D}", card.IdempotencyKey); Assert.Null(card.SourceMessageId); Assert.Null(card.SourceConversationId);
        Assert.Contains("Owner name: Not supplied", card.Description!); Assert.Contains("Score (provider-supplied): 71", card.Description!);
        Assert.Contains("2 lead(s)", summary); Assert.Contains("Added 1 new card(s)", summary); Assert.Contains("1 were already tracked", summary);

        Assert.Equal("ordinary progress", await AgentWithoutModel().SummarizeDelegationUpdateAsync(delegation, update with { Summary = "ordinary progress" }, runtime.CreateContext(identity: Identity()), CancellationToken.None));
    }

    [Fact]
    public async Task PipelineCommandsMoveAndArchiveCards()
    {
        var updates = new List<UpdatePersonalTodoItemRequest>(); var archives = new List<ArchivePersonalTodoItemRequest>();
        var runtime = Runtime(Card(LeadA, PipelineStages.New))
            .RegisterCapability<UpdatePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Update, (r, _) => { updates.Add(r); return Task.FromResult(Card(LeadA, PipelineStages.Qualified) with { Revision = 4 }); })
            .RegisterCapability<ArchivePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Archive, (r, _) => { archives.Add(r); return Task.FromResult(Card(LeadA, PipelineStages.Dead)); });
        var reply = await Reply(runtime, AgentWithoutModel(), Message($"pipeline.v1 lead={LeadA:D} stage=qualified note=\"Owner confirmed probate\""));
        var move = Assert.Single(updates);
        Assert.StartsWith("[Qualified] 1 Fixture St", move.Title); Assert.Contains("New → Qualified — Owner confirmed probate", move.Description); Assert.Contains("Stage: Qualified", move.Description);
        Assert.Equal(3, move.ExpectedRevision); Assert.Contains("moved from New to Qualified", reply); Assert.Empty(archives);

        var dead = await Reply(Runtime(Card(LeadA, PipelineStages.New))
            .RegisterCapability<UpdatePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Update, (r, _) => Task.FromResult(Card(LeadA, PipelineStages.Dead) with { Revision = 4 }))
            .RegisterCapability<ArchivePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Archive, (r, _) => { archives.Add(r); return Task.FromResult(Card(LeadA, PipelineStages.Dead)); }),
            AgentWithoutModel(), Message($"pipeline.v1 lead={LeadA:D} stage=Dead"));
        Assert.Equal(4, Assert.Single(archives).ExpectedRevision); Assert.Contains("archived", dead);

        Assert.Contains("No pipeline card exists", await Reply(Runtime(), AgentWithoutModel(), Message($"pipeline.v1 lead={LeadB:D} stage=Qualified")));
        Assert.Contains("stage must be one of", await Reply(Runtime(), AgentWithoutModel(), Message($"pipeline.v1 lead={LeadA:D} stage=Won")));
    }

    [Fact]
    public async Task OutreachDraftIsSavedForApprovalAndNeverSent()
    {
        CreateArtifactDocument? artifact = null; var lookups = 0;
        var runtime = Runtime(Card(LeadA, PipelineStages.Qualified, "12 Oak Ln"))
            .RegisterCapability<LeadLookup, InheritanceLead>(OutreachDrafter.ProviderLookup, (_, _) => { lookups++; return Task.FromResult(new InheritanceLead(LeadA.ToString("D"), null, "12 Oak Lane", "Pat Example", "Verified", 90, null, null, "Austin", "Travis", "TX")); })
            .RegisterCapability<CreateArtifactDocument, ArtifactDocument>(PlatformCapabilities.ArtifactCreate, (a, _) => { artifact = a; return Task.FromResult(new ArtifactDocument(Guid.NewGuid(), a.Title, a.DocumentType, "Draft", null, null, null, null, null, [])); });
        var message = Message($"outreach-draft.v1 lead={LeadA:D} tone=formal from=\"Matt\"");
        var reply = await Reply(runtime, AgentWithoutModel(), message);
        Assert.Equal(1, lookups); Assert.Equal(OutreachDrafter.DocumentType, artifact!.DocumentType); Assert.Equal($"outreach-draft:{LeadA:D}:{message.MessageId:N}", artifact.IdempotencyKey);
        Assert.Contains("Dear Pat Example,", artifact.Content); Assert.Contains("12 Oak Lane", artifact.Content); Assert.Contains("With respect,\nMatt", artifact.Content);
        Assert.DoesNotContain("I'm reaching out", artifact.Content); Assert.Contains("I will not send anything", reply);

        var closed = await Reply(Runtime(Card(LeadA, PipelineStages.Closed)), AgentWithoutModel(), Message($"outreach-draft.v1 lead={LeadA:D}"));
        Assert.Contains("won't draft outreach for a closed lead", closed);
    }

    [Fact]
    public async Task StatusQuestionsAreAnsweredFromTheBoardWithoutAModel()
    {
        var reply = await Reply(Runtime(Card(LeadA, PipelineStages.New), Card(LeadB, PipelineStages.Contacted, "2 Fixture Ave")), AgentWithoutModel(), Message("Pipeline?"));
        Assert.Contains("2 active lead(s)", reply); Assert.Contains("**New** (1)", reply); Assert.Contains("**Contacted** (1)", reply); Assert.Contains(LeadB.ToString("D"), reply);
        Assert.Contains("pipeline is empty", await Reply(Runtime(), AgentWithoutModel(), Message("status")));
    }

    [Fact]
    public async Task OtherAgentsCannotDirectThePipeline()
    {
        var runtime = Runtime().RegisterCapability<DelegateWithTrackingRequest, DelegationSnapshot>(DelegationCapabilities.Create, (_, _) => throw new Xunit.Sdk.XunitException("no assignment"));
        var agentMessage = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Chat.ToString(), "bot", "research-brief.v1 state=TX",
            new Dictionary<string, string> { ["senderEmployeeType"] = "Agent", ["senderOrganizationUserId"] = Guid.NewGuid().ToString("D") }, Guid.NewGuid(), 0, Guid.NewGuid());
        var reply = await Reply(runtime, AgentWithoutModel(), agentMessage);
        Assert.StartsWith("Only the business owner or my manager", reply);
    }

    [Fact]
    public async Task CredentialShapedInputIsRefusedBeforeAnything()
    {
        var reply = await Reply(new AgentTestRuntime().RegisterCapability<object, object>(DelegationCapabilities.Create, (_, _) => Task.FromResult<object>(new { }))
            .RegisterCapability<ReadDelegationRequest, IReadOnlyList<DelegationSnapshot>>(DelegationCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<DelegationSnapshot>>([])),
            AgentWithoutModel(), Message("here is my api key synthetic-key-000000000000000000000000000 go find leads in TX"));
        Assert.Equal(AcquisitionsManagerAgent.CredentialGuidance, reply); Assert.DoesNotContain("synthetic-key", reply);
    }

    [Fact]
    public async Task ManagementReviewReportsPipelineCounts()
    {
        ManagementStatusReport? report = null;
        var runtime = Runtime(Card(LeadA, PipelineStages.New), Card(LeadB, PipelineStages.Negotiating, "2 Fixture Ave"))
            .RegisterCapability<ManagementStatusReport, JsonElement>(CapabilityNames.Management.StatusReport, (r, _) => { report = r; return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true })); });
        var cycle = Guid.NewGuid();
        await runtime.DeliverEventAsync(AgentWithoutModel(), ManagementEvents.ReviewDue, new ManagementReviewDueEvent(cycle, "weekly", DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "UTC"), identity: Identity());
        Assert.Equal(cycle, report!.CycleId); Assert.Contains("2 active lead(s)", report.Summary); Assert.Contains("1 in New", report.InProgress); Assert.Contains("1 in Negotiating", report.InProgress);
        Assert.Single(report.DecisionsNeeded); Assert.Empty(report.Risks);
    }

    [Fact]
    public async Task DelegationProtocolIsDeterministic()
    {
        var agent = AgentWithoutModel(); var context = Runtime().CreateContext(identity: Identity());
        Assert.Equal("Other", (await agent.ClassifyDelegationIntentAsync(Message("ask the analyst to search TX"), context, CancellationToken.None)).Kind);
        var d = new DelegationSnapshot(Guid.NewGuid(), 1, Owner, Owner, Manager, "Chief of Staff", "Acquisitions Manager", "Find leads in Travis County TX", "Leads", "AwaitingAcknowledgement", true, Chat, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, [], []);
        Assert.IsType<DelegationResponse.Acceptance>(await agent.HandleDelegationReceivedAsync(d, context, CancellationToken.None));
        Assert.IsType<DelegationResponse.Decline>(await agent.HandleDelegationReceivedAsync(d with { Instruction = "use password hunter2" }, context, CancellationToken.None));
        Assert.Equal(d.WorkItemId, await agent.ResolveDelegationWorkAsync(d, "x", context, CancellationToken.None));
        Assert.True((await agent.ReviewDelegationOutcomeAsync(d with { ExecutionEvidence = new(Guid.NewGuid(), Guid.NewGuid(), "Committed", "Assigned to Lead Research Analyst: `research-brief.v1 state=TX`") }, context, CancellationToken.None))!.Succeeded);
        Assert.Null(await agent.ReviewDelegationOutcomeAsync(d, context, CancellationToken.None));
    }

    [Fact]
    public async Task PipelineSummaryCapabilityReturnsCountsByStage()
    {
        var result = await Runtime(Card(LeadA, PipelineStages.New), Card(LeadB, PipelineStages.New, "2 Fixture Ave")).ExecuteCapabilityAsync(AgentWithoutModel(), AcquisitionsManagerAgent.PipelineSummary, new { });
        Assert.True(result.Succeeded);
        var value = result.Value!.Value;
        Assert.Equal(2, value.GetProperty("activeLeads").GetInt32());
        var stage = Assert.Single(value.GetProperty("stages").EnumerateArray());
        Assert.Equal("New", stage.GetProperty("stage").GetString()); Assert.Equal(2, stage.GetProperty("leadIds").GetArrayLength());
    }

    [Fact]
    public async Task ManifestMatchesAgentAndDeclaresWhatTheCodeUses()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "csweet-plugin.json"))) root = root.Parent;
        var manifest = await AgentManifestLoader.LoadAsync(Path.Combine(root!.FullName, "csweet-plugin.json"), CancellationToken.None);
        Assert.Equal(AcquisitionsManagerAgent.Id, manifest.Id); Assert.Equal(AcquisitionsManagerAgent.CurrentVersion, manifest.Version);
        Assert.Equal("manager", manifest.RolePolicy!.BaseType); Assert.Equal("manager.v1", manifest.RolePolicy.Profile);
        Assert.Contains(AcquisitionsManagerAgent.PipelineSummary, manifest.Capabilities);
        foreach (var required in new[] { OutreachDrafter.ProviderLookup, PlatformCapabilities.OrganizationSnapshotRead, DelegationCapabilities.Create, PersonalTodoCapabilities.Read, PersonalTodoCapabilities.Add,
            PersonalTodoCapabilities.Update, PersonalTodoCapabilities.Archive, PlatformCapabilities.ArtifactRead, PlatformCapabilities.ArtifactCreate, CapabilityNames.Management.StatusReport })
            Assert.Contains(required, manifest.RequestedCapabilities);
        foreach (var e in new[] { CommunicationEvents.MessageReceived, DelegationCapabilities.Received, DelegationCapabilities.Changed, ManagementEvents.ReviewDue })
            Assert.Contains(e, manifest.RequestedSubscriptions);
    }
}
