# RealtyLeadsAI Acquisitions Manager

Version 0.1.0 on SDK 5.8.0. A manager agent (`manager.v1`) that owns a real-estate acquisitions
pipeline for a C-Sweet business. It works with the **RealtyLeadsAI Lead Research Analyst** and
the **RealtyLeadsAI plugin** (≥ 0.2.0). It never contacts a lead and never changes provider records.

## What it does

| Owner says | Manager does | Model? |
|---|---|---|
| "Find 10 leads in Travis County TX with score over 70" | Rewrites it as a `research-brief.v1` line (every filter must be quoted from your words), finds the Lead Research Analyst that reports to it, and **delegates with tracking** | Rewrite only |
| `research-brief.v1 state=TX county="Travis" minScore=70 limit=10` | Delegates it verbatim | No |
| *(the analyst delivers a lead brief)* | Reads the brief's data block and adds **one pipeline card per new lead** to its workboard (`acquisition-lead.v1:<leadId>`), then tells you how many were new | No |
| "pipeline" / "status" | Counts by stage with lead IDs | No |
| `pipeline.v1 lead=<id> stage=Qualified note="..."` | Moves the card; `Closed`/`Dead` archive it | No |
| `outreach-draft.v1 lead=<id> [tone=warm\|formal] [from="Name"]` | Refreshes the record through the granted read, saves a letter as a `realtyleads.outreach-draft.v1` document for you to edit and approve. **Nothing is sent.** | No |
| *(management review due)* | Posts a status report: active leads, counts by stage, decisions needed | No |

Stages: `New → Qualified → Outreach planned → Contacted → Negotiating → Closed | Dead`.

Only authenticated humans (and this manager's own manager) can direct the pipeline; other agents, and
messages without sender context, get read-only answers. Credential-shaped messages are refused before anything else runs.
When the manager acts on an upstream delegation (for example from the Chief of Staff), that delegation is reported
complete only once the resulting lead brief has been ingested, never merely because research was assigned.

## How it fits the platform

- Assignments use the SDK's delegation-with-tracking protocol. The analyst replies with a
  `[lead-brief:<artifactId>]` marker; the manager picks that up in `SummarizeDelegationUpdateAsync`,
  reads the artifact, and ingests the leads idempotently.
- Pipeline cards are personal workboard items created in the backlog, so they are owner-driven
  records rather than agent tasks; the manager does not subscribe to personal-todo availability.
- All SDK delegation hooks that would otherwise call a model are overridden deterministically.
- `acquisitions.pipeline.summary.v1` exposes the pipeline to other agents (for example the Chief of Staff).

## Grants

Provider: `realtyleads.inheritance.lookup.v1` (dependency `realtyleads`). Platform: model chat,
organization snapshot, delegation (`create/read/respond/publish/cancel`) and tracking
(`read/pending/processed`), personal work (`read/add/update/archive`), artifacts (`read/create`),
management status report, and the shared manager monitoring set (project health, incidents).
Declarations grant nothing; install review decides.

Run `dotnet test` and `dotnet run --project src/CSweet.Agent.RealtyLeadsAI.Acquisitions -- --self-test`.
