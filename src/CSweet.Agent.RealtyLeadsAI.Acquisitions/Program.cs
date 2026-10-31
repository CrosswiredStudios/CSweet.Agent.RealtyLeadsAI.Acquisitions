using CSweet.Agent.SDK;
using CSweet.Agent.RealtyLeadsAI.Acquisitions;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var manifest = await AgentManifestLoader.LoadAsync("csweet-plugin.json", CancellationToken.None);
    var identity = manifest.Id == AcquisitionsManagerAgent.Id && manifest.Version == AcquisitionsManagerAgent.CurrentVersion;
    var grammar = ResearchBrief.TryParse("research-brief.v1 state=TX county=\"Travis\" limit=5", out var brief, out _) && brief is { State: "TX", County: "Travis", Limit: 5 } &&
                  PipelineCommand.TryParse("pipeline.v1 lead=00000000-0000-0000-0000-000000000001 stage=qualified", out var move, out _) && move?.Stage == PipelineStages.Qualified;
    var ok = identity && grammar;
    Console.WriteLine(ok ? "Synthetic Acquisitions Manager self-test passed." : "Synthetic self-test failed.");
    Environment.ExitCode = ok ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.AddCSweetAgent<AcquisitionsManagerAgent>();
await builder.Build().RunAsync();
