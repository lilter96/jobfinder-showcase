# Ubuntu acceptance — 2026-10-05

Repository `lilter96/jobfinder`, branch `main`; starting checkpoint `270f9b4f8d59d1a81fc9add790086a0768e711ba`.
This is implementation and isolated acceptance, **not a production release or full-plan completion**.
Historical Windows evidence remains in REFACTORING; it is not a current Linux certificate.

## Scope and requirement evidence

| Requirement | Current implementation / evidence owner | Remaining release gate |
|---|---|---|
| Server ownership before recovery/effects | SingleInstanceGuard/ServerOwnership; membership validates the original account/source/claim again at the actual SDK boundary, including after folder reads. | Real deployed sole-server/runtime and account/session acceptance. |
| Exact durable Telegram membership | Requests/Workflow/Gateway/Ledger; bound ordered receipts, separate InviteRequested, captured reusable resolve/history, atomic channel/posts/roots/commands publication. PostgreSQL proof/fault/race/cancellation/process tests. | Actual owner-controlled MTProto protocol/account outcomes; no real Telegram receipt is certified by the fake gateway. |
| Damaged/uncertain retained state | Old-marker/newer-root checks; importer quarantines missing/orphan markers; consumer quarantines damaged owned input; unknown wire outcomes never repeat. Explicit exact-request/attempt reconciliation only. | Operator evidence for any real unresolved operation; no automatic reconciliation. |
| Migration and rollback | `20261005103050_DurableChannelMembership`, current snapshot, restrictive FKs, unique channel/revision, guarded Down including orphan markers/delay keys/native current/future envelopes. | Production chain, restorable database/artifact backup and exclusive migration. |
| Telegram collection/runtime writers | ChannelCollectionSources owns captured folder/access failure and contact-backfill completion; ChannelCollectionSources fences history/stream admission against the original account/source; PostProcessingRequests owns admitted post counters/high-water progress and distinct child operation IDs. TelegramService cannot save channel state. | Read-only collection is still a host reader, not an exactly-once durable MTProto reader. Actual reconnect/catch-up/account/UI behavior. |
| Remaining source writes | ApplicationImportSources owns conservative legacy retirement and locked revision-bound dry-run release; CompanyMetadataSources owns joined ingestion and captured nightly metadata/absence. VacancyAssessmentCapture owns version-bound import failure retirement; InterviewReminderSources owns captured reminder/debrief publication. Compiled boundaries and PostgreSQL cancellation/stale/unknown tests. | Real collector/dry-run control interaction and end-to-end deployed behavior. |
| Concurrent business intents | Offer request admission rechecks the retained exact key if another producer committed between lookup and source capture; original dialogue/input stays authoritative. | Real provider/owner dialogue acceptance. |
| Candidate/Discovery/Hiring/Preparation/Engagement | Existing native owners/workflows/typed policies/LLM contracts remain; changed guards preserve explicit joined HR/offer owner calls. | Full checklist audit is not closed by the new membership slice. Interactive/operational/live acceptance of all workflows remains open. |
| Quality and artifacts | Maximum Application/Resume reasoning and >=9 gates preserved. Synthetic adapter/browser/PDF tests exercise hashes, read-back, cache and layout. | Real sendable messages and truthful candidate PDFs with human/layout/font inspection; synthetic provider scores are not independent quality proof. |
| Performance | Saved checkpoints/cache/native scheduling preserved; no reasoning or quality gate weakened. | Comparable real baseline and cold/warm/retry measurements: sample count, input/recipe/route fingerprints, p50/p95, calls/cost/rounds/cache reuse/failures. No production speedup claim. |
| Publication | Verified source/test/document batches are intended for main, with no force push. | Production has not been migrated or deployed; a pushed commit alone is not a release. |

## Final current verification

Current whole-solution build: **0 warnings, 0 errors**, 2.48 seconds (`build-final.log`); EF reports no pending model changes
(`model-final.log`). Current whole suite: **3236 passed / 3236 total, 0 failed, 0 skipped**, 27m 54s 950ms (`full-5.log`).
The source/test manifest covers all 84 changed source/test files and was checked unchanged from the final build through acceptance.
Checksums, exact commands, individual scoped runs and artifact identities are in [UBUNTU-ACCEPTANCE-EVIDENCE.json](UBUNTU-ACCEPTANCE-EVIDENCE.json).
Scoped results overlap the full suite and are not added to its total. `git diff --check` also passes.

Retained failing evidence: `full-2.log` completed on earlier binaries with 3219 total, 3211 passed, 8 failed, 0 skipped. Those
failures exposed one real concurrent offer admission defect and stale/poisoned test fixtures; current full acceptance covers the
corrections. `full-3.log` and `full-4.log` were interrupted after newly moved owner dependencies and a changed collector signature
exposed outdated fixtures; they are not certificates. A preceding OrdinaryNotification run passed 205/206, with one incorrect
fresh-notice target expectation; its corrected targeted case passes and the final complete suite covers all 206 cases.
No assertion, quality threshold or production downgrade fence was weakened to obtain the final pass.

Sanitized artifacts are at `the retained private acceptance artifacts`, with a sibling ZIP archive. They include the actual
synthetic Chromium PDF/HTML/raster, extracted text, font/info/warning reports, six current browser screenshots, source manifest
and evidence ledger. Raw scratch logs remain under `/tmp/jobfinder-handoff`; no credentials or personal candidate artifacts
are committed. Screenshots show preserved editing/conflict state, reconciliation and read-only unknown outcomes on isolated local
Razor/Blazor hosts. Four membership OS-kill boundaries and actual CLI reconciliation are part of the passing PostgreSQL suite.

## Reproduction and evidence limits

Ubuntu 24.04, .NET SDK 10.0.401 installed at `<dotnet-sdk-path>`, Docker Engine 29.5.3.
Tests use disposable PostgreSQL 17-alpine and the exact restored Playwright Chromium revision 1243.
Local full acceptance uses conservative xUnit class parallelism, at most four threads, through the ignored output-only
`tests/JobFinder.Tests/bin/Debug/net10.0/xunit.runner.json`:
`{"longRunningTestSeconds":60,"diagnosticMessages":true,"maxParallelThreads":4,"parallelAlgorithm":"conservative"}`.
This bounds simultaneous disposable hosts on this machine; it changes no application policy or test assertion.
Existing unrelated application/database/monitoring containers were not changed. No production compose startup was performed.
Logs and synthetic artifacts live under `/tmp/jobfinder-handoff`; personal PDFs, sessions and credentials are not committed.

```bash
export PATH=<dotnet-sdk-path>:$PATH
export DOTNET_ROOT=<dotnet-sdk-path>
dotnet tool restore
dotnet restore JobFinder.slnx
dotnet build JobFinder.slnx --no-restore
dotnet ef migrations has-pending-model-changes -p src/JobFinder.Data -s src/JobFinder.Data
dotnet test --project tests/JobFinder.Tests --no-build -- --filter-class '*ChannelMembershipTests'
dotnet test --project tests/JobFinder.Tests --no-build -- --filter-class '*ModuleOwnershipTests'
JOBFINDER_TEST_RESUME_ARTIFACTS=/tmp/jobfinder-handoff/artifacts dotnet test --solution JobFinder.slnx --no-build
```

The membership fixture exercises native producers and real consumer subprocesses with a synthetic gateway, four OS-kill
boundaries and separately retained effect counters. It does not log in to Telegram. Source/ledger fault tests use genuine
PostgreSQL transactions. CLI acceptance runs the actual App in a separate producer-only process against disposable storage.
Browser/PDF tests use actual Chromium with intercepted synthetic provider/site traffic; no real employer is contacted.

Regression uncovered stale native-owner bot expectations, Windows-only paths, incomplete fixture dependencies, old notice
counts and migration-fixture poisoning. EF commits each earlier Down separately before a later guard can refuse; the test
helper restores latest schema before source read-back and keeps the original failure assertion. No production rollback guard
was weakened. Fixture cleanup is scoped for membership/mock messages and preserves unrelated queue families.

## Synthetic artifact and reuse acceptance

`DurableResumeIntegrationTests.Real_adapters_print_read_back_review_and_attach_a_cached_ATS_PDF_through_the_shared_facade`
uses the actual resume adapters and Chromium renderer against intercepted synthetic provider responses. The standalone current
case passes (1 test, no skips). It checks PDF hash/read-back, attached artifact identity, complete visible content and layout,
cache reuse, maximum `xhigh` reasoning, captured operation provenance and exact call accounting.

| Synthetic scenario | Provider-fixture assertions | Meaning / limit |
|---|---|---|
| Cold print/read-back/review, then warm reuse | 2 total intercepted calls; fixture cost USD 0.0002; subsequent cache/attachment retrieval adds no call. | Demonstrates functional reuse; not measured real-provider latency or pricing. |
| Replaced PDF recovery | 3 total intercepted calls; one content writer; same saved content/model, new validated artifact; fixture cost USD 0.0003. | Reprint/review reuses the saved content checkpoint; never blindly attaches a replaced PDF. |
| Producer-only tailor CLI and warm CLI reuse | Original isolated consumer retains ownership; no Telegram session; original request/artifact reused. | Actual process admission/retrieval; no real provider or employer call. |

The saved synthetic PDF is one A4 page. `pdfinfo`, `pdffonts`, Poppler text extraction and a rasterized-page visual inspection
confirm embedded subset Inter fonts with Unicode maps, readable text, a joined role/company heading and both experience bullets,
without visible overlap or clipping. Poppler reports `Syntax Warning: Bad bounding box in Type 3 glyph`; the warning is retained,
not waived as a production font/layout certificate. Chromium emits Type 3 Inter subsets here. This one short synthetic English
resume does not certify real Cyrillic/multipage candidate PDFs, factual truth or independent quality >=9. The fixture supplies
its 10/10 review score; it is not a quality verdict on the user's resume. A comparable real baseline and p50/p95 have not been
measured because the original inputs/provider/runtime are unavailable.

## Private/live prerequisites and exact open gates

Availability was inspected by name only; no values are copied into this report. There is no provisioned JobFinder production
runtime, private development configuration, candidate resume or provider/account configuration in this checkout/environment.
Required deployment options/storage are:

- `Llm:ApiKey`, reviewed `Llm:Endpoint` and actual usable `Llm:Proxy` route. The Windows localhost proxy is not assumed on Ubuntu.
- `Telegram:ApiId`, `Telegram:ApiHash`, authenticated `telegram_sessions`; first-login `Telegram:Phone`/2FA password if needed.
- `Bot:Token`, `Bot:OwnerUserId`, a self/owner-only diagnostic destination.
- Reviewed `candidate.profile`, confirmed `candidate.facts`, original resume under `Paths:Profile`; no fictional facts may enter the real account.
- Authenticated `browser.hh`, `browser.linkedin`, `browser.threads` documents as required by the exercised workflows. Review `HeadHunter:ApplyHost`/`HeadHunter:ResumeTitle` for the real resume selection; Threads may instead use its authorized `Threads:AccessToken` route.
- Mailbox/self-only delivery acceptance needs `Email:Enabled`, `Email:Host`/`Email:Port`, `Email:ImapHost`/`Email:ImapPort`, `Email:Username`/`Email:Password` and the reviewed `Email:FromAddress`/`Email:FromName`/`Email:BccSelf`. These are option names from code, not provisioned values.
- Real `Database:ConnectionString`, deployment access/configuration and `.env` (including reviewed Linux proxy/backup settings).
- Restorable database and artifact backup on a safe destination (`JOBFINDER_BACKUP_DIR`), with enough deployment disk space.

Open: actual provider/message/PDF quality >=9, real account/protocol/owner-only delivery and browser/mailbox behavior,
comparable baseline/performance, full workflow operational acceptance, production backup/migration/deploy/health/recovery.
Do not send tests to employers or publish test comments to real Threads accounts. No keys, sessions or candidate facts were invented.

## Migration, recovery and rollout runbook

Membership reconciliation and downgrade limits are in WOLVERINE's “Native Telegram membership and Ubuntu operator recovery”.
Keep Sending/OutcomeUnknown receipts and all source markers/history. Never clear them, resets pauses or downgrade the schema
to make a retry possible. Reconciled records checked evidence and requires a separate later approval; it cannot certify Joined.

After live gates actually pass, the deployment operator must identify the real runtime/volume, verify the migration chain and
queue inventory, back up database plus artifacts and test restoration, stop the sole server, and run ownership-protected
`--migrate` on the current deployed assembly. Start one verified server, check schema/ownership/recovery/health/UI, and perform
controlled owner-only end-to-end acceptance. Keep sent and uncertain histories intact. Prefer a reviewed forward fix; a guarded
Down is not a release rollback plan. These steps were **not performed** by this Ubuntu update.
