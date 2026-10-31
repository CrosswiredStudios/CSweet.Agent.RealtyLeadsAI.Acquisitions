# RealtyLeadsAI Acquisitions Manager repository instructions

This repository contains one standalone C-Sweet protocol-v2 manager agent. Its purpose is:

> Acquisitions Manager: turns owner goals into research assignments for the Lead Research Analyst, tracks each lead through a pipeline on its workboard, drafts outreach for owner approval, and reports status each management cycle.

## Invariants

- Keep `com.crosswiredstudios.agent.realtyleads.acquisitions` and version `0.1.0` synchronized between agent code,
  `csweet-plugin.json`, tests, and releases. Bump the version first, then add `releases/<version>.md`.
- The root manifest is the reviewed authority request. Keep `provides`, `requires`, events, configuration, credentials,
  web access, and UI contributions synchronized with implementation and tests. Every declaration must be used and tested.
- Request the minimum authority needed. Manifest declarations never grant access.
- Use typed callbacks and `AgentRuntimeContext.Platform`. Do not implement MCP/JSON-RPC, access runtime/workload/lease
  tokens, connect directly to databases or Docker, or handle provider credentials.
- The manager never sends outreach, contacts leads, or mutates provider records. Outreach is a document for owner approval.
- The only model use is rewriting an owner's sentence as a `research-brief.v1` line; the result is parsed by the
  deterministic grammar and every filter must be quoted from the owner's words. Provider data never enters a prompt.
- Delegation classification, acceptance, work binding, outcome review and update summaries are deterministic overrides;
  do not reintroduce the SDK's model-backed defaults.
- The `research-brief.v1` grammar and the lead-brief data block format must stay byte-compatible with
  `CSweet.Agent.RealtyLeadsAI` (the analyst). Change both repos together.
- Pipeline cards are correlated by `acquisition-lead.v1:<leadId>`; every mutation carries a stable idempotency key.

## Verification

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.RealtyLeadsAI.Acquisitions -- --self-test
```
