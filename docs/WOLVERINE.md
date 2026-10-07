# Durable execution compatibility gate

Pinned runtime: WolverineFx.EntityFrameworkCore, WolverineFx.Postgresql and WolverineFx.RuntimeCompilation **6.41.0**.
The installed package metadata declares MIT and native net10.0 assets. Restore resolves the existing EF Core 10.0.12 / Npgsql 10.0.3 stack without warnings.

## Verified seams

`WolverineIntegrationTests` uses the actual JobFinderDbContext, snake-case conventions, existing migrations and its factory against disposable PostgreSQL. No production database or external recipient is used.

- A producer with no business handlers commits state and a PostgreSQL command, exits, and a later server handles the command.
- Disposing an uncommitted context leaves neither its state nor its command.
- A state constraint failure rolls back a distinct command; the test deliberately does not hide that command behind business deduplication.
- A failure injected after the database commit but before outgoing-message flush leaves recoverable work.
- Distinct envelopes with the same business operation produce one stage result. The result counts executions, not just rows.
- Native source-generated JSON preserves defaults for an old message; explicit message identities remain independent of CLR names.

All ten tests passed with zero skips on 2026-09-30. Four use a separate executable: orderly producer exit, OS termination before commit, OS termination after commit before flush, and termination after the handler commits its result/next command but before acknowledging its input. A later server recovers committed work without duplicating the business stage. Every normal test host runs with AutoCreate.None and without transport AutoProvision; only the disposable fixture provisioner creates storage.

This is compatibility evidence, not a claim that every production queue has been migrated. The first adopted workflow is described below. The producer requires previously provisioned storage: disabling its recovery agent does not make a cold, unprovisioned database valid.

## Integration rules

### Producer recovery coordination

Producer-only runtimes retain Balanced mode and disabled local durability agents. They may become the native cluster leader,
so their DI composition also registers the existing native `MessageStoreCollection` as an `IAgentFamily`. This lets a producer
leader discover and assign database recovery work to eligible consumers without advertising the producer as eligible or adding
business handlers/listeners. Do not enable local recovery on a producer: the pinned runtime's missing-listener fallback can
route another node's inbox into its durable local queue. Do not use Solo or Serverless for a publisher sharing this cluster.
Serverless requires Inline endpoints, which the PostgreSQL transport does not support.

This uses public native family/DI APIs and the same store collection instance, not a replacement leader-election or recovery
algorithm. The pinned [controller composition](https://github.com/JasperFx/wolverine/blob/d00a3ed2a6326c62a6f8d67c13d4b9288e4f81b5/src/Wolverine/Runtime/Agents/NodeAgentController.cs)
and [capability admission](https://github.com/JasperFx/wolverine/blob/d00a3ed2a6326c62a6f8d67c13d4b9288e4f81b5/src/Wolverine/Runtime/Agents/NodeAgentController.StartLocalProcessing.cs)
are part of this compatibility seam. Package upgrades must re-run actual process-termination recovery while a producer remains
leader, as well as atomic EF publication, cold-storage refusal and bootstrap acceptance.

### Bootstrap publication and Candidate changes

An unstarted Wolverine runtime cannot publish. Server initialization acquires database ownership and migrates the schema first,
then starts a bounded, producer-only infrastructure host using the same DbContext factory and centralized message composition.
This transient host has no consumers, business handlers, recovery agent, Telegram client or bot loop and does not provision storage.
It is not a second application server. `StartupRecovery` runs next, before consumers: interrupted delivery source/attempt state,
conservative Unknown notice/root and native command commit together; root-admission failure rolls all of them back and fails startup.
Recovery never resends an employer effect, and repeats do not add another notice. Interrupted job-run repair follows delivery recovery.
Manual comment reconciliation similarly commits the checked result, source revision, Reconciled attempt and native notification
together. Notification admission failure preserves the original unknown state; reconciliation never publishes or certifies transport.
All four effective policies are previewed and validated without writes before any policy
import; legacy-file import also runs before main consumers start. After the main messaging runtime starts, the transient publisher
is stopped and ordinary publications use main-runtime scopes. Background application loops start afterward. Startup failure must
stop the transient publisher and release ownership; exclusive `--migrate` starts/stops it inside its own ownership lifetime without
starting the main host or recovering Sending work. Ordinary producers still require current schema and an explicitly started publishing runtime.

Candidate profile/facts publication and generated facts use `NotificationDeliveryRequests.SaveAndFlushAsync`: current document,
immutable candidate revision, optional import preimage, notification owner and native command commit together. Missing Telegram
configuration leaves owned AwaitingTarget work rather than rejecting a valid Candidate change. The joined facts overload requires
the caller's enrolled DbContext outbox and uses `SaveJoinedAsync`; it stages the notification owner/command without committing the
interview caller's transaction. Failed admission rolls back even the source changes saved to allocate notification IDs. File import
sets a legacy file aside only after successful publication; it remains available if native admission fails.

`PgPolicyStore` uses the same native admission boundary for effective-value import and compare-and-swap publication. All four typed
policy facades share its registered instance instead of constructing independent writer/cache objects. Document, history, consumed
legacy overrides, notification root and command commit together; failed admission preserves the former version and overrides.
Pure previews still perform no publication. A CLI or test publisher must start its native runtime before invoking an importing read.

This does not complete notification cutover. Ordinary recovery/job/card boundaries, bounded non-sending
legacy admission, digest due-slot atomicity, retention and live owner-chat acceptance remain tracked in `docs/REFACTORING.md`.

### Telegram/Threads post processing

`Modules/Discovery/PostProcessingWorkflow` owns `ProcessPost` v1 on the native Discovery queue. `PostProcessingRequests.StoreAsync`
commits the new source, frozen search/LLM recipe, owner marker, native command and Telegram `PostsSeen`/high-water cursor together.
`RawPostWorker` only admits bounded batches of legacy New rows. Missing/mismatched owner roots are quarantined with one notice,
not silently regenerated. The reparser and `--post-process <raw-id> [--refresh]` are producer/wait facades; they require an owned
consumer with current schema. Cancelling the CLI waiter does not discard its saved request or start another Telegram client.

Reading saves source links, crawled pages, grounded contacts and any structured hh detail before extraction. Extraction saves the
validated task output before Telegram person verification. Stage/outbox publication is atomic. Unavailable extraction schedules
its existing stage without consuming the failure budget or recrawling; verification failure never redrafts saved extraction.
An uncommitted provider effect can repeat after a crash: this is not exactly-once inference. Explicit refresh supersedes an active
generation under the source lock. Frozen source/revision and stage/envelope claims fence publication and retirement, including
old unsupported envelopes and source edits while handling an outage. Recipe changes cancel rather than silently reinterpreting input.

Final ingest joins vacancy deduplication, typed company contacts, source status/link, Telegram vacancy counters and assessment
capture to the request result and next command. hh posts attach their captured structured source without paid extraction. A partial
refresh retains prior contact facts; changed stored contacts rewind only verification before a destructive update. Per-parse HTML
parser instances isolate concurrent page reads. The shared source transport budgets and Telegram contact cache remain in force.

`DurablePostProcessing` refuses downgrade while request roots, source markers/revisions or native pending/scheduled/inbox/outbox/
dead-letter envelopes remain, including orphaned commands. Stop all consumers/producers and inspect retained history before schema
maintenance. Synthetic recovery/adapter tests do not certify current live Telegram, hh or Threads availability.

### Vacancy assessment

Source ingest/merge/refresh and manual rescore commit the business change, a nullable `AssessmentCaptureId` and a versioned
`CaptureVacancyAssessment` command in the same EF outbox transaction. `VacancyWorker` only imports legacy New rows without an
intent or active request. The existing lightweight `preparation` listener owns snapshot capture, not a second paid executor.
The `scoring` native queue owns
`ScoreVacancy`, `PrepareVacancyLetter` and `FinishVacancyAssessment`, with stable v1 identities and two handlers at a time.
No inline scoring/letter fallback remains in that worker. Capture token plus assessment revision fence repeated envelopes,
manual rescore, dismissal and unresolved delivery. Snapshot input, assessment root, token clearance and Score command commit
together under the source/candidate locks and a serialized two-request admission gate. Ingest does not read candidate inputs or
call the LLM; capture reads the current source. It is an atomic work intent, not a premature frozen candidate/source snapshot.
Missing reviewed profiles defer two minutes without business attempts; full admission/snapshot races defer five seconds.
Failures retire after four attempts with one atomic alert; a failed assessment commit preserves its original capture intent.

`vacancy_assessment_requests` records detached vacancy/source/company and reviewed profile/fact versions, search/automation
policy snapshots, sanitized LLM routes, task recipes and a canonical checksum of the entire input. Score and letter each
checkpoint their result with the next native command. Availability failures schedule the exact stage without business attempts;
a saved score is not regenerated when lettering resumes. Transient checkpoint I/O/database errors retry the captured result
in memory without consuming business attempts. A process death before the paid response is checkpointed can still repeat it.
Lettering now publishes a child Hiring request and schedules a short continuation instead of holding an assessment handler
while the child drafts/reviews. Its saved dependency survives midnight, retries and restarts without recapturing a new child.
The parent operation also fixes child business identity, so replay between child commit and dependency checkpoint reuses the
first frozen date. Those two owner commits are not one cross-owner SQL transaction; that handoff audit remains release work.

Claim and publication recheck candidate versions, source inputs, dismissal/manual edits and pending/uncertain deliveries under
short row locks. Manual rescore supersedes even a same-New generation and refuses Pending/DryRun/Sending/OutcomeUnknown
delivery. Finalization rechecks current hard filters and restrictive automation controls without upgrading an earlier approval
to automatic sending. Approval-card counting/insertion and daily automatic reservations share a PostgreSQL transaction lock.
Weak or mismatched letter reviews become a visible approval warning, never unattended delivery; the shared sender still
enforces its independent exact-text, candidate and PDF gates. Corrupt snapshots are quarantined with one atomic notice and
dead-lettered, freeing admission capacity. A bounded, deferred capture failure does not monopolize the first queue positions.

The guarded assessment downgrade refuses stored requests or nonzero rescore generations. Stop all producers/consumers
before any rollback; do not restore the legacy inline worker over persisted native operations. Capture downgrade also refuses
live tokens and pending/scheduled/dead-letter capture commands, including envelopes whose token has already been retired.
Native subprocess kill tests for assessment/capture and all remaining facade migrations are not established by this slice.

`--backfill-eval` now publishes annotation-only input v2 into the same assessment queue/table. It captures the original
Scored/Applied evaluation, score/decision/letter-proof state and candidate/source versions before paid work, with a Score-only
recipe. The native Score checkpoint goes directly to Finish; no letter, approval card, application reservation or delivery
trigger is admitted. Final publication changes only keywords/language/legitimacy reasons/market note and legitimacy, advancing
the assessment revision, and joins its informational notice/root/command to that transaction. Historical score/decision/status,
sent text, letter/artifact proofs, Notified, ScoredAt and business error counters remain unchanged. Scored and already Applied
rows remain supported; ordinary Rescore is not an equivalent operation.

Admission, paid claim/checkpoint and publication compare the same source/candidate/evaluation witness and refuse pending or
unknown employer delivery. Changed captures cancel rather than rebase. Availability pauses retain the same native stage;
a later Finish reuses saved Score without another paid call. Cancelling the CLI wait does not cancel the durable request.
Annotation failure retires only its request, never the historical vacancy or its counters. Version-1 JSON omits the optional
annotation field and keeps its existing recipe/command identities. Version 2 still uses the explicitly named v1 stage commands,
but a v1-only binary cannot read that input: stop all producers/consumers and retain/export the roots before any binary rollback.
The guarded schema downgrade continues refusing retained assessment history. Scoped synthetic acceptance is not proof of
live provider/process-kill behavior, which remains part of the full release gate.

### Read-only score preview

`--score <hh URL>` reads the public page once and admits a detached snapshot through `VacancyAssessments.RequestPreviewAsync`.
Input v3 uses the existing assessment table/workflow/scoring queue, not another paid engine. `PreviewKey` freezes the Score-only
recipe, candidate versions, search policy and salary adjustment. Concurrent identical previews share one root; independent
previews use VacancyId=0 outside the positive-source active-generation index. Profile/facts are captured before admission;
missing facts use read-only defaults and never call Candidate bootstrap publication. Score/hash plus Finish command commit together.
Finish validates the paid checkpoint and completes only the diagnostic root, without vacancy/decision/application/card publication
or delivery/worker triggers. Ordinary v1 and annotation-only v2 retain their JSON/recipe and source-publication semantics.

Diagnostics still score after a hard-filter refusal and support unreviewed profiles. Results retain the originally captured
candidate inputs across later edits; they do not authorize employer sends or overwrite current candidate/source history.
Letter preview separately requests/waits through the existing native maximum Application executor using the frozen profile,
facts and operation. Reviewed profile and saved facts remain prerequisites; missing prerequisites show the score and explicit
letter refusal, never unchecked text or implicit document creation. Changed candidate content refuses that letter rather than rebasing.

`--score-preview <guid>` resumes/reads a saved preview without another hh read or paid Score. Same-input warm requests reuse the
score and native letter; changed relevant inputs/recipes create a new Score key. Cancelling the producer wait leaves native work.
Unavailable providers resume the same stage. Three exhausted score-save attempts retain the paid result in a failed root with
its joined alert instead of automatic paid rework. Damaged/future inputs and hash-mismatched scores quarantine once. Death before
the paid checkpoint may still repeat an interrupted provider call; no exactly-once provider guarantee is claimed.

Migration `20261004010312_DurableVacancyPreview` refuses retained preview/key/hash/input or immediate/scheduled/incoming/outgoing/
dead-letter preview commands, including future identities. Do not force downgrade by deleting evidence. Scoped native restart,
fault injection, typed provider and actual CLI-method acceptance use disposable data and synthetic transports. They do not prove
real process-kill, real-provider quality/performance, production UI/browser acceptance or the whole release.

### Letter preparation

`LetterWriter.WriteAsync` / `ResponseAsync` capture/request/wait through `Modules/Hiring`; no inline drafting fallback remains.
The request freezes the brief, source/conversation witness, candidate versions, rendered date, sanitized operation policy and
prompt/schema/route/quality recipe. Two native lanes per round independently checkpoint Drafting → Reviewing → Completed.
Review unavailability reschedules only review of the saved draft, without another paid writer call or quality round.

Draft and review have separate bounded PostgreSQL listeners; arbitration uses a lightweight control listener. A perfect clean
10 finishes immediately; a clean 9 starts one immutable native 20-second grace deadline, or finishes once both lanes complete.
Four exhausted rounds return the best warned, unsendable result. Original-envelope claims fence duplicate paid work, and late
losers/timers cannot replace the winner. Process-local cancellation only aborts losing HTTP work; it never executes saved work.

Warm reuse requires the actual context recipe, frozen rendered date, source witness and candidate/conversation versions;
explicit regeneration supersedes the active generation. Detached `--score` previews have no vacancy FK but retain candidate
fences. Transient checkpoint failures reuse the captured response in memory. Death before its checkpoint can still repeat a
charged call; exactly-once LLM execution is not promised. Application reasoning and all truth/layout/checklist gates remain.

The pinned `DurableLetterPreparation` migration adds the two native queues and request/lane tables without runtime DDL.
Downgrade refuses any saved request or queued lane envelope. Native storage comparison, actual native publisher/consumer,
outage/restart, duplicate/late commands, grace/perfect races, four-round ranking, source/candidate drift, checkpoint faults,
waiter cancellation and composed assessment-child handoff are tested against disposable PostgreSQL. Real-provider latency/
cost benchmarks and actual CLI/process-kill cases for this new slice remain release work.

### Exact-text employer review

`LetterWriter.ReviewMessageAsync` / `ReviewResponseAsync` are now producer/wait facades over the same Hiring owner. A separate
v2 input mode freezes the exact text, source/conversation/candidate witnesses, review-only task recipe and sanitized operation.
It creates one Reviewing lane on the existing review listener; it never emits Draft commands, rewrites the approved text,
runs four quality rounds or waits for the generation grace timer. Weak verdicts are returned unchanged and fail the send gate.
Legacy v1 generation snapshots remain readable; inconsistent modes are quarantined instead of treated as generation requests.

Warm reuse binds exact text/context/recipe and authoritative candidate versions. A stored review dependency is checked against
the expected brief/text and its persisted score/problems/hash proof before being returned. First-message review still uses
maximum Application reasoning and the existing local formatting/truth/independent-checklist gates. Outages defer only review
with no business attempt; transient checkpoint faults reuse the paid response. Cancellation of a waiter does not cancel saved
work. Death before a paid response commits can still cause another charged call.

The disposable exact-review process probe kills the actual consumer before the reviewer effect, after the effect but before
checkpoint, and after the committed review but before acknowledgement. Recovery preserves the exact text and calls no writer.
Only the middle boundary repeats the synthetic paid-effect counter: provider execution and PostgreSQL checkpoint are not one
transaction. A committed verdict survives orphan-envelope recovery without another reviewer call. The probe refuses ordinary
5432/5433 databases and never loads private deployment configuration or an employer transport.

Employer preparation returns explicit ReviewPending, ResumePending or Ready outcomes, not an unverified ApprovedDispatch.
The native parent commits the review request ID and scheduled continuation, releases its claim, then later checkpoints the
completed review with the resume dependency or ready packet. It never waits for paid review inside a delivery handler. Child
publication and parent dependency are separate replay-safe commits: replay reuses the same exact-text recipe/root rather than
paying again. The selected review request ID is retained even when the parent replays after child completion. Guarded downgrade
refuses stored v2 review requests or employer dependencies; it does not erase them to make rollback appear successful.

### Shared rules

1. Use separate message/transport schemas, versioned message identities and narrow DTOs. Never serialize tracked EF entities into commands.
2. Begin an explicit SQL transaction before taking a transaction-scoped advisory lock, then enroll the exact factory-created context in IDbContextOutbox before publishing. Enroll does not begin a transaction: a lock taken without one is released at the end of its statement and cannot protect the following read/merge/save. One context owns state and commands.
3. `SaveChangesAndFlushMessagesAsync` commits an active transaction itself. Do not commit it again, and do not reuse the scoped outbox for another independent operation.
4. Thin handlers using factory-created contexts declare `NonTransactional` and explicitly own their transaction/outbox. This is not permission to publish through an unenrolled bus.
5. Producer-only hosts do not discover business handlers, listen to application queues or run the durability recovery agent. The server retains sole ownership of MTProto, bot polling and employer delivery.
6. Explicitly provision the pinned schema before enabling listeners. Never use AutoPurgeOnStartup or destructive resource setup in deployment. The compatibility tests use disposable AutoProvision/CreateOrUpdate only.
7. Durable retries cannot resolve an uncertain external send. DeliveryGateway/DeliveryLedger remain the authority; OutcomeUnknown is not a retryable Pending row.

## Pinned storage and retry checks

The checked-in EF migration `PinnedWolverineStorage` embeds the SQL generated by the installed 6.41.0 native Weasel/Wolverine schema model. It provisions dedicated `jobfinder_messages` / `jobfinder_queues` schemas and the original seven immutable `DurableQueues` names; `DurableLetterPreparation` adds the two pinned Hiring letter queues. No AutoProvision, AutoPurge or runtime DDL is required by normal producers/consumers. Native schema comparison detects drift without repairing it. Down only retires wholly empty storage; any persisted rows refuse rollback before either schema is dropped. Stop producers/consumers and export/reconcile pending/uncertain messages and metadata before an intentional rollback.

`dotnet run --project tests/JobFinder.MessagingProbe -- --storage-script` exports the native model without starting a host or connecting to a database. Its inert loopback port-1 connection string is only a schema construction input. Script ordering is not the compatibility criterion; CI compares the migrated database against native schema objects.

Storage integration tests cover native full-schema parity, producer exit/later consumer with AutoCreate.None, drift detection without repair, immutable/non-destructive storage roster, retry fairness, cancellation before a scheduled retry and recovery of a scheduled retry after consumer restart. Rollback takes exclusive table locks before checking emptiness, refuses persisted data with a non-retryable PostgreSQL exception and retains a concurrent producer's newly committed message. The existing contact-migration test also exercises empty-storage rollback/reapply. A healthy operation progresses while a failing one waits outside the active queue. These are test business commands, not migrated production preparation stages.

Register source-generated JSON contexts in one `UseSystemTextJsonForSerialization` configuration action. Repeated calls replace the action rather than cumulatively registering each context; the mixed-command regression caught this seam and now uses one complete message context.

## First adopted workflow: manual HeadHunter links

The server registers real Discovery handlers through `AddJobFinderMessaging(consumer: true)`. Ownership, database migrations, policy validation and legacy-file import start before Wolverine; host services start sequentially. Its native `discovery` listener is sequential and uses the already migrated PostgreSQL transport. RuntimeCompilation is now required by actual handler generation, not only the compatibility probe.

Bot, dashboard and `--add-vacancy` share the `HhLinkQueue` facade and `HhLinkRequests` owner. A transaction merges active requests by vacancy ID, retains every concurrently added contact and saves `ReadHhLink` through the enrolled EF outbox. The partial unique index prevents two active requests for one vacancy. Neither UI nor CLI reads the external vacancy in this path.

```text
hh_link_requests: Reading
  -> user-requested account read, or anonymous High-priority PoliteHttp read
  -> durable vacancy/source ingest
  -> [Linking state + LinkHhContacts command] in one transaction
  -> idempotent company-contact linking
  -> [Completed state + notification] in one transaction
```

Network work stays outside SQL transactions. A restart in Linking never repeats Reading. If ingest commits but the next-stage transaction fails, the retry finds the existing source instead of fetching its HTTP page again. Contacts added while linking receive a further durable continuation before completion. No employer send is part of this workflow.

`SourceBlockedException` stores RetryAt and a native scheduled command atomically without consuming business retries. Other work can proceed while it waits; account-read rules, anonymous pacing, site-family pauses and High-priority behavior remain in the existing adapters. Transient errors use native scheduled retries; four business failures retire the request with one atomic alert. Cancellation prevents subsequent stage/completion publication but cannot retract a harmless read or contact upsert already in flight. Ownership is checked before effects and again at stage commit. Empty command identities and unknown payload versions go directly to native dead-letter storage, not a pointless retry loop; legacy payloads without Version retain version 1.

Resubmitting a paused link when a saved account session and account-read allowance are available atomically clears the request's defer time and publishes an immediate Read command. The gateway checks access again at execution; anonymous-only resubmission never bypasses the site's pause. An older scheduled envelope later sees the completed/current stage and is harmless.

`HhLinksJob` no longer fetches pages or links contacts. It is only a compatibility producer importing the old `hh.links.pending` string once: request merge, commands and removal of the old setting share a transaction. Import failure preserves the entire old value and leaves no partial work. New producers never write that setting. `/run hh-links` imports legacy work; it does not bypass the native scheduled source pause.

The actual `--add-vacancy`, `--tailor`, `--application-check`, `--hh-chat-send` and `--hh-add-letter` CLI commands start only the producer messaging runtime, have no application listener, business handler or recovery agent, and stop it before exit. They work while the real server advisory lock is held. They require the pinned storage and current application migrations to be provisioned first; they do not acquire server ownership or perform migrations. Resume/message commands wait for the existing server to finish the saved request; interrupting a CLI wait does not cancel the business request. Letter/draft producers (`--letter`, `--letters-refresh`, `--recruiter-draft`, `--reply-redraft`, `--reply-card`, `--score`) now also start that producer-only runtime; their actual subprocess acceptance cases remain release work. Remaining unrelated CLI commands are not migrated by this slice.

The `DurableHhLinkRequests` migration refuses rollback if the request table contains any data, taking an exclusive lock before checking. Stop producers/consumers, export/reconcile request history and native queued work before deliberate rollback; do not delete rows to make a live downgrade pass.

`DurableHhLinkIntegrationTests` and `HhLinkCliIntegrationTests` exercise real handlers/listeners, actual CLI subprocesses, real HTTP parsing/ingest/contact linking over intercepted HTTP and disposable PostgreSQL. They cover concurrent merges, producer exit, duplicate commands, cancellation races, new contacts during linking, restart between stages, source-pause fairness, state/command rollback, legacy import rollback, invalid-policy and second-server startup gates, lost ownership, dead-letter compatibility and non-destructive migration rollback.

## Employer conversation delivery

Recruiter conversations, inbound replies/follow-ups, Telegram/email outreach and hh chat/cover-letter CLI share `EmployerMessages`, `EmployerContentGate`, `DeliveryGateway` and the same PostgreSQL delivery ledger as initial applications. LinkedIn's manual-sent button remains explicitly a record of the user's external action, not an automated send. Saved Messages test sends are not employer delivery.

The producer captures exact text/recipient/channel, source identity/revision, first/fresh/attachment semantics, conversation/vacancy fingerprint and authoritative profile/fact versions. Request plus versioned Prepare command commit atomically. Replies/outreach are bound to their source draft; direct recruiter packets conservatively deduplicate identical text/target/fresh/attachment choices. Reused or stale source drafts never silently send a different unseen revision.

Native Prepare/Dispatch handlers use `hiring_deliver`; resume preparation retains its separate listener. A pending resume is a durable dependency and scheduled continuation: its paid message review is checkpointed, and waiting occupies no message handler or SQL transaction. Distinct preparation envelopes cannot concurrently repeat paid work. Ready plus Dispatch share a transaction, and restart restores the saved review and exact resume ID/hash rather than selecting a newer PDF.

The gateway rechecks source/candidate versions and automatic-reply emergency controls, freezes the source revision and stores a Submitting attempt before any external effect. Sending cannot be dismissed or overridden. A process-local in-flight marker distinguishes a still-running send from an interrupted receipt within the one owned server; it is not an executor, retry timer or durable lease. Missing receipt commit reaches native retry/recovery and becomes OutcomeUnknown, never another submission. SQL cannot provide exactly-once SMTP/Telegram/browser effects.

Receipt, source status, funnel/history, exact chosen text/attachment reference and notice share a commit. A first/closed-attempt message creates its own application; an open conversation retains its existing history. Restart marks interrupted attempts and reply/outreach sources unknown before listeners start. Known-not-sent outcomes leave an explicit manual result; unknown outcomes block all sends for that vacancy until reconciliation.

To record a result only after checking the actual chat/mail/site, use `--message-reconcile <operation-guid> --sent|--not-sent --evidence "what was checked"`. This invokes the ledger owner, records Reconciled and updates source/history/notice atomically without sending anything. A manual sent assertion does not certify the original submitted PDF; a newly recorded application does not inherit its attachment ID. Notices include the operation GUID. Stop producers/consumers before an intentional downgrade; populated message history or resume dependencies refuse destructive rollback.

HeadHunter chat confirmation requires the complete normalized text in a new own bubble, or newly added cover-letter text in the response bubble, not an old bubble matching the first 40 characters. An unconfirmed post-click outcome is unknown. `--hh-add-letter` deliberately requires a synced existing chat and never falls back to a page navigation that can create a new application.

`EmployerMessageIntegrationTests` exercise the actual native runtime, EF outbox, disposable PostgreSQL, source/facade owners and fake content/recipient seams: duplicates, producer exit, Ready restart, interrupted Sending recovery, source/candidate changes, emergency switches, receipt rollback, quality/attachment rejection, three reply channels, outreach, hh letter, resume-wait fairness and explicit concurrent reconciliation. `HhChatConfirmationTests` check the confirmation predicate. These are synthetic correctness tests, not live-provider quality, actual message process-kill/CLI-subprocess/browser delivery coverage or real-provider latency/cost benchmarks; those release requirements remain open.

## Actual resumable resume preparation

`ResumeTailor` is now a cache/input-snapshot/request/attachment facade, not a second in-process generation loop. Bot, dashboard, application attachment preparation and resume CLI use it. `ResumeWorker` only selects eligible vacancies and publishes; PostgreSQL request state and recent terminal history replace its process-local failure blacklist. It limits automatic runnable requests to two, and completion wakes the selector rather than imposing another two-minute idle wait. Manual requests can also join the same durable workflow.

`resume_preparation_requests` owns Drafting, Printing, Reviewing and Finalizing checkpoints and terminal history. Candidate profile, source PDF text, facts, stories, a narrow vacancy projection, canonical recipe and the complete secret-free LLM policy snapshot are frozen before publication. Restart restores the same operation UUID, policy revision and effective routes while attaching current deployment credentials/transport settings. Current task/prompt/recipe incompatibility cancels the old request rather than silently mixing generations.

Each completed stage and its next versioned command commit through the EF outbox in one transaction. Final artifact, result reference and notification also share a commit; accounting sums the operation's paid successful and failed calls. A native transport envelope UUID claims an expensive stage under the vacancy advisory lock. Distinct duplicate commands cannot concurrently run the claimed stage; recovery with the original envelope can resume it. This is a native-inbox recovery identity, not an expiring handwritten queue lease. No caller runs an alternate local executor.

The `hiring_prepare` native listener accepts two concurrent handlers and two messages per fetch. Its idle poll is explicitly one second, not the native five-second default that added a delay at each handoff. The default and per-queue override are documented in [Wolverine PostgreSQL polling](https://wolverinefx.net/guide/durability/postgresql#polling). Scheduled LLM outages release the claim and persist the exact deferred stage; unrelated work proceeds, and an outage at review does not rerun the writer. Transient failures use native retry; four business failures retire with an atomic alert. Cancellation or changed inputs prevent subsequent publication, but cannot undo a paid call already in flight.

PDF checkpoints retain SHA-256, extracted page text and layout evidence. Missing/replaced files are reprinted from the saved document and reviewed again without a writer call. This also applies when a completed, previously ready artifact is damaged and its inputs still match: the saved operation resumes at Printing, preserves old artifact rows, creates a new result and accounts the additional review. `--force` instead requests fresh generation. Writer metadata comes from the chosen best round, not the last rejected round.

Readiness still requires a complete, non-duplicate checklist scored at least nine, no truth/keyword/layout issues, at most two pages and matching file bytes. Old unchecked base resumes are never a fallback. Maximum Resume reasoning is retained through the persisted policy compiler. The new business-data migration refuses populated-history rollback with an exclusive table lock, like the Discovery migration.

Verification uses disposable PostgreSQL, real native listeners, actual CLI subprocesses, intercepted synthetic LLM HTTP and the real Chromium/PdfPig printer/read-back/attachment pipeline. Synthetic PDF visual inspection supplements text/layout checks; mocked checklist scores prove gate execution, not real-provider writing quality.

Nine real subprocess-kill cases cover Printing, Reviewing and Finalizing before the effect, after the effect but before its checkpoint, and after the actual SQL commit but before outbox flush/acknowledgement. Restart recovers the original native envelope claim, drains pending incoming/outgoing storage and creates one result/notification. Completed earlier stages never rerun; an interrupted effect without a committed result can rerun and be charged twice. This does not claim exactly-once HTTP effects. Only disposable test hosts accelerate node/orphan recovery timers, using the supported ConfigureWolverine override rather than registering a second runtime. Comparable real-provider cold/warm/retry benchmarks and remaining workflow migrations are still final verification work.

These Discovery/Hiring/preparation slices are code adoption, not production deployment or completion of the full migration. Interview preparation, remaining queue/manual producers, notification transport handoffs and common send/module boundaries remain pending. The durable-execution checklist stays open until those workflows and final end-to-end/release gates are verified.

### Initial-application executor and atomic producer handoff

`ApplicationDeliveryRequests` captures an existing Pending application's exact operation/revision/text/recipient, vacancy/company/source witness,
reviewed candidate versions, automation controls and sanitized LLM operation. The root and its Prepare command commit atomically. Assigning
the root is executor metadata, not a new approved application revision. Distinct requests for the same application/revision reuse one root.

Approval, assessment, pending-letter editing/regeneration and dry-run release now capture the native root and Prepare command in their
source transaction. `QueueAsync(context, outbox, trackedApplication)` joins that transaction without committing; the caller owns final
business-state/outbox commit. Capture or root-save failure rolls back the source change too. Reading the stored uncommitted source avoids
inventing precision that PostgreSQL has already normalized. An edit advances the business revision and captures a new root; old work is
fenced and cannot send the replaced text. `ApplyWorker` imports/reconciles unowned Pending rows only, with no content engine or recipient
transport. Native-held dry-run release captures the released revision and current policy in the same transaction.

The importer reads only a bounded source projection. Empty legacy letters or absent non-hh recipients retire as NeedsManual with one
notice under the vacancy lock and matching revision/owner marker. It rechecks the current source before rejection: a concurrent actual
edit or cancellation wins instead of being overwritten. Retired rows count as progress, so the next healthy row beyond the 25-row
batch limit can be admitted. A missing reviewed candidate leaves a well-formed source Pending; it is not an application rejection or
permission to send without review. Content/PDF work and recipients remain exclusively native.

Native Prepare checkpoints exact-review/resume dependency IDs and schedules continuation without occupying a handler while a child works.
Ready checkpoints the selected proof and attachment ID/hash with Dispatch. Source/candidate changes are checked again under short locks,
and the shared gateway still re-reads attachment bytes. The persisted native owner marker prevents the legacy gateway from claiming that
same revision; it is not an authorization to bypass the shared review/PDF/recipient gates.

The ledger claims Sending plus its attempt atomically, serializes application pacing across vacancies, and rechecks restrictive current
automation controls and daily reservations. Exhausted daily capacity defers to local midnight rather than polling every five seconds.
An in-progress application also fences another initial send after the cursor expires; receipt completion renews spacing from that receipt.
Current or captured dry-run prevents SMTP/Telegram sending too. An invalid policy/cursor fails closed. Unknown effects and receipt-commit
failures require reconciliation; restart marks interrupted Sending roots, applications and attempts unknown and never resends them.

Unknown notices include the native request GUID. `--application-reconcile <guid> --sent|--not-sent --evidence "checked result"`
records one manually checked outcome under the vacancy lock. The exact original request marker, operation, attempt and approval
revision must still match; changed witnesses refuse reconciliation. Evidence is required and bounded to 2000 characters. The existing
manual-sent button delegates to this same operation in its source transaction. Concurrent decisions commit one result/notice only.
The attempt becomes Reconciled, not Confirmed; original proof/hash remain forensic records, while the application does not certify a
tailored PDF as delivered. A not-sent outcome leaves NeedsManual and cancels the root, never automatically schedules another send.
Reconciliation does not deserialize a damaged preparation input, invoke a provider or repeat a recipient effect.

Unsupported saved roots are fenced under the vacancy lock, retired as Failed and leave the matching Pending application NeedsManual.
One user notice commits with quarantine before the typed unsupported exception goes to the native dead-letter queue. A repeated invalid
command cannot create another notice. An unsupported command version is also dead-lettered, never interpreted as permission to send.

A confirmed known-not-sent hh receipt commits its pause and Retry command with the application revision/receipt. Retry validates that
exact completed root/known-not-sent attempt before capturing a new revision. Contacts returned by hh hand off through the same receipt
outbox. Duplicate contact commands upsert the existing contact; observation counters are not an exactly-once claim.

The guarded downgrade locks business/envelope tables and refuses any roots, owner markers, attempts, pending/scheduled/inbox/outbox or
dead-letter initial-application commands, including commands whose business row has already been retired. Stop every producer/consumer
before attempting rollback; do not disable this refusal or drop persisted work.

This source cutover is verified on isolated PostgreSQL/Wolverine with fake content/recipients and network-refusing clients; it is not a
production migration, deploy or proof of an actual employer effect. Actual subprocess termination tests stop after Ready commit,
Sending claim, simulated recipient effect and receipt commit before acknowledgement: Ready resumes once; interrupted Sending becomes
unknown even when the test observer knows that the fake effect had not happened; a committed receipt never repeats. The actual
reconciliation CLI runs against a disposable database and does not send. Real headless Chromium/Blazor tests cover queued-letter
replacement and the checked-sent reconciliation button against a native consumer. Pending text is editable and re-reviewed; Sending
and OutcomeUnknown retain a read-only letter. These are synthetic browser effects, not live hh or Telegram delivery. Broader browser
approval/send acceptance and real-provider quality/latency evidence remain full-release work. Do not restore a second legacy executor
to recover failed native work, or bypass the guarded downgrade.

### Exclusive migration ownership

Server initialization checks ownership before context creation, schema migration and recovery, and checks it again after migration
before proceeding. Its cancellation token includes host shutdown, so ownership-monitor shutdown does not leave startup using an
uncancelled token. The `--migrate` CLI holds the same monitored, unpooled advisory lock through schema migration and legacy-file import;
it refuses a live server instead of modifying that server's schema. Stop the server before this maintenance command. The CLI does not
start Telegram, bot or native consumer loops and does not recover live Sending rows. Its lock is closed on success or failure; normal
producer/read-only CLI commands remain able to use a running server. Destructive downgrade still needs the existing guarded rollback
and stopped producers/consumers; the ownership check does not weaken native rollback refusal.

### Captured letter editor revisions

Dashboard and bot edits carry the original `LetterEditSnapshot` to the shared Hiring action. Source letter revision and application
IDs/revisions/statuses/owner markers are compared under the vacancy lock before any source write. Regeneration checks the captured
view before paid preparation and again before publication. Sent/Sending/OutcomeUnknown sources remain read-only. A failed dashboard
save keeps both the typed text and original snapshot; repeated saves cannot silently rebase. Explicit reload adopts the current copy
and discards the retained draft. A bot reply carries the snapshot captured when its edit prompt was opened.

`Vacancy.LetterRevision` is an EF concurrency token advanced by edit/regeneration, changed approval publication and assessment letter
publication, and is included in frozen source witnesses. Identical text or unchanged timestamps cannot hide an intervening edit.
Legacy source snapshots default to revision zero. Migration `LetterEditRevision` refuses downgrade while nonzero source revisions
remain, rather than erasing that fence. EF can complete newer independent down-migrations before an older native-history guard refuses
rollback; inspect applied migrations and reapply the current schema before restarting. A failed downgrade is not a promise that every
newer schema migration remained applied. Native request/attempt/envelope rollback guards remain in effect.

### Remaining native Engagement cutover

The versioned `policy.engagement` owner and collector/editor acceptance are implemented. Paid comment preparation now has one
native executor, `Modules/Engagement/CommentPreparationWorkflow`; `CommentPlanner` only requests and waits. Drafting uses the
`engagement` queue, while pure style checking uses `preparation`, so a saved draft does not wait behind another paid call. Frozen
inputs contain the candidate, author/post, prompt examples, operation policy and artifact recipe, never deployment credentials.
Draft output and Check command commit together; the first check and rewrite command also commit together. Duplicate commands are
fenced by stage, round and envelope claim. Explicit regeneration cancels the active generation and bypasses completed-result reuse.
Cancellation stops the waiting caller, not the durable request. A provider outage schedules the same phase without consuming the
failure budget; an unavailable rewrite may retain the first usable set with its original audience/reason. The surviving set must
still pass question/style checks, and completed results are read back through the style gate.

`--comment-test` and `--comment-refresh` are producer-only commands requiring a running consumer and current migrations. They do
not start a second server, MTProto client or inline LLM engine. Migration `DurableCommentPreparation` refuses downgrade while any
request or pending/scheduled/inbox/outbox/dead-letter comment command remains, including orphaned envelopes. Stop all producers
and consumers before rollback. Actual subprocess-kill acceptance covers both phases before/after a synthetic effect and after
checkpoint commit: only an uncommitted drafting effect may repeat; committed drafts resume checking without another paid draft.
Synthetic HTTP acceptance exercises the real task/transport/usage sink and frozen maximum-effort route across a concurrent policy
change. This is not real-provider latency/quality evidence or proof of publication to Threads.

Final source draft/card publication now belongs to `CommentSourceWorkflow`. The collector only captures an admitted request with
candidate/policy/post witnesses, expiry and a daily/author reservation; it never waits for paid planning. Source handlers checkpoint
their child and its native command in one transaction. A terminal child publishes dependent source commands with its own result,
so normal waiting parents neither retain a handler nor continually poll the paid queue. Source publication checks current candidate,
pause, expiry and restrictive cap/cooldown/auto-approval changes again. A current preference cannot upgrade captured manual approval.

Explicit refresh carries the full displayed source witness, normalizing timestamps to PostgreSQL microseconds. It retires only the
still-Pending matching source. Retirement/revision, replacement draft, new card and old-card keyboard edit intents commit together.
If an original card finishes sending while retirement is in flight, its receipt takes the same source lock and enqueues retirement;
the original-card receipt and keyboard intent then commit together. The dispatcher uses the real SDK over a named HttpClientFactory
client, keeps existing socket recycling settings, treats an already-modified keyboard as success and never turns an edit failure into
a new text card. These owner-notification receipts do not imply exactly-once Telegram delivery.

Bot/UI comment decisions use `CommentDecisions` with the captured revision/status. A stale edit cannot rebase; the dashboard retains
its text until explicit reload and bot edit replies retain the prompt version. Failed comments are not reauthorized by ordinary edits.

Pending-comment expiry belongs to `CommentExpiry`, not a bulk scheduler update. Its captured source witness includes both owner
markers and the full source hash; the shared Engagement lock serializes expiry with approval, replacement and card receipts.
The deadline is the earlier of the validated completed source's frozen expiry and the current typed search policy's post-age limit.
Missing or damaged source checkpoints and active delivery roots fail closed. The bounded rotating `engagement.expiry.scan` cursor
advances atomically with source revision, notices and native commands, including scans of fresh or ineligible rows; it never marks
a skipped source as successfully expired. Original pending card admission stays immutable, so a late primary receipt can retire its
keyboard under the same source lock. Existing cards produce parent-bound edit intents; a message ID without a confirmed original
account/chat/effect witness fails owned without an SDK effect. No recipient publication or paid preparation runs in housekeeping.
Housekeeping delegates cleanup to `Maintenance/OperationalRetention`. Its conservative retention policy keeps all native/business
history, notifications/effect attempts, controls/cursors, edit bindings/tombstones, source checkpoints, artifacts and LLM
billing/provenance indefinitely, including damaged or future protocols. There is no destructive native-history purge or archival.
Only clean unowned Skipped/NotVacancy posts and unreferenced terminal non-digest job diagnostics older than 30 days are removed,
in bounded row-locked batches under a shared maintenance advisory lock and server ownership. Posts with errors, a native owner
marker, processing revision, linked vacancy or any processing root survive even when another marker is damaged or missing.
Locked source rows are skipped, not waited on or deleted; sweep failure rolls back the batch. Notification-linked and all digest
run receipts remain. Running jobs are never declared interrupted merely because they are six hours old: that transition belongs
to `StartupRecovery` after acquiring new server ownership. Storage compaction or business-history expiry requires a separate
explicit policy and dependency audit; this cleanup does not establish unrelated live acceptance or production release gates.

Ordinary import/failure workers join their notices through `Notifier.SaveAndFlushAsync`; they do not save new unowned cards.
Legacy initial-application import excludes unresolved roots/attempts before bounded selection and rechecks under the vacancy
lock before retirement. This is a conservative fence, not ledger reconciliation. Telegram seed proposals and reply redraft
cards also use native admission. Outreach's generated-ID save remains inside the card publication transaction, so a failed root
admission rolls the source back too. Remaining source-editor/candidate witnesses and legacy dispatcher admission are separate
cutover gates; these joins do not certify the whole notification migration or live recipient delivery.

Migration `DurableCommentSources` refuses downgrade while source roots, revisions/source markers, keyboard edits or orphaned source
envelopes remain. Stop all producers/consumers first. Subprocess termination at child handoff and final publication before/after
commit preserves atomic source/card state and does not repeat the committed paid child.

Native comment delivery uses `PrepareCommentDelivery` on `preparation` and `DispatchCommentDelivery` on `engagement`.
Source automatic approval and manual picks checkpoint their root and command in the same source transaction. The root freezes
the exact post/author/text/revision, candidate and policy witnesses and selected API/browser route without credentials.
`CommentPublishWorker` imports only unowned legacy Approved rows; it never prepares or publishes inline. Legacy attempts without
a reliable receipt become forensic OutcomeUnknown roots/attempts, including malformed content, and are never automatically sent.
Valid approvals missing a reviewed candidate remain prerequisite-paused instead of being rejected.

API preparation verifies the frozen post ID, author, permalink and content, then creates an explicitly unpublished text container
and resumes its status by the saved container/account identity. A numeric browser shortcode cannot authorize a different API post.
Container creation is not publication. The explicit publication POST returns the thread ID; after that POST starts, any unreliable
receipt is OutcomeUnknown, including HTTP errors. Browser delivery identifies the current account and exact target, requires
unique inline controls in that target's scope, and accepts only a new own full-text reply with its own permalink under that target.
Existing own replies require reconciliation; snippets, unrelated cards and other authors are not proof. No publication path falls
back to another transport. API/search credential and browser cookie state never belong in operation snapshots.

Before the effect, source/attempt/packet witnesses, ownership, candidate, post expiry, current restrictive cap and emergency pause are checked.
The daily cap includes legacy Posted history as well as native submitting/confirmed/uncertain attempts, without counting a native
receipt and its Posted source twice. A legacy row with no publication timestamp falls back to its decision/creation timestamp.
An unsafe API endpoint is not copied into the route snapshot, including credentials in user info, query or fragment.
The Sending source, immutable attempt and durable spacing cursor commit before publication. Sending work blocks another comment
even after its cursor expires; receipts renew spacing. Prerequisite and known-not-sent retries schedule native commands instead
of sleeping in handlers. Restart or handler replay after a committed send claim but without its receipt becomes OutcomeUnknown,
even if a synthetic process dies before the actual effect. This deliberately favors manual checking over duplicate publication.

Queued cancellation uses the displayed Approved revision and can only cancel Preparing/Ready work. Sending and unknown sources
are read-only. `--comment-reconcile <guid> --sent|--not-sent --evidence "checked outcome"` verifies the original root/source/attempt
witnesses and records Reconciled, not Confirmed. It neither publishes nor retries nor fabricates a transport receipt/publication
timestamp. Changed or damaged witnesses refuse reconciliation instead of being overwritten. The dashboard exposes this command
on an unknown source; bot/UI queue cancellation carries the exact approval revision.

`DurableCommentDelivery` refuses downgrade while roots, attempts, source markers or native pending/scheduled/inbox/outbox/dead-letter
commands remain, including orphaned commands. Stop all producers/consumers before rollback. Adapter, actual browser/CLI and
process-kill acceptance are separate from live Meta-page verification and final production cutover, which remain release gates.

Meta's [official sample implementation](https://github.com/fbsamples/threads_api/blob/main/src/index.js) distinguishes an unpublished
container from the published thread ID. Container status is read separately; explicit publication calls `threads_publish`.
With `auto_publish_text` enabled, creation itself publishes instead. A local timeout or lost response after a publication request
therefore cannot authorize browser fallback or another send. This is the safety inference for our adapter, not a claim that Meta
provides an exactly-once guarantee. The native owner conservatively records uncertainty rather than retrying a possible effect.

Local native consumers now own comment dispatch; there is no direct legacy ReplyAsync or recipient-capable importer. The existing
production container has not been upgraded. Native PostgreSQL/HTTP/Chromium/CLI recovery acceptance does not certify the real
signed-in Meta page, its current account navigation/composer DOM or public read-back. These live adapter checks, remaining queues
and module controls, comparable real-provider benchmarks and the complete release audit remain required before deploy/push.

Primary references: [EF outbox source at the pinned package commit](https://github.com/JasperFx/wolverine/blob/d00a3ed2a6326c62a6f8d67c13d4b9288e4f81b5/src/Persistence/Wolverine.EntityFrameworkCore/DbContextOutbox.cs), [PostgreSQL transport](https://wolverinefx.net/guide/durability/postgresql.html), [EF factory transaction refusal policy](https://github.com/JasperFx/wolverine/blob/d00a3ed2a6326c62a6f8d67c13d4b9288e4f81b5/src/Persistence/Wolverine.EntityFrameworkCore/Codegen/DbContextFactoryRefusalPolicy.cs).

### Native interview preparation and entry-point cutover

`PrepareInterview` is registered on the native Preparation queue only in the owned consumer. Its stages capture frozen inputs,
checkpoint the plan, prepare/cache answers, print an operation-owned PDF and publish the source pack plus notice. A producer
publishes and waits; neither `InterviewPrep.BuildPackAsync`, `InterviewWorker` nor the CLI contains a second pack engine.
`InterviewWorker` admits at most 20 legacy candidates per pass, including one-shot retirement/repair progress, and never waits for
paid work. `--interview <id> [--force]` requires the current migrations and a running consumer. Ctrl+C stops the wait, not the saved
request. Warm native output passes the same checkpoint/source/PDF read-back; legacy or damaged output is not certified by the CLI.

`InterviewTransitions` joins manual scheduling, incoming invitations and hh history to `InterviewSources` in the caller's outbox
transaction. Paid reply analysis and browser/chat reads stay outside that short transaction. Incoming analysis checks the exact
vacancy text supplied to the task and the captured open-interview witnesses before publishing its inbound event/stage/root.
Debrief remains an interactive paid call, not a durable inference checkpoint: its displayed source/header is checked before the
call and again when closure, note, next stage and next preparation command commit together.

Dashboard editors keep typed time/kind/duration/positions and their old witnesses on conflict. Explicit reload discards them;
switching detail modes does not silently rebase positions. Position witnesses are read together under one source/candidate lock,
not in a transaction per question. Interview bot prompts persist source/header witnesses and transcripts; exact conversation
compare-exchange prevents late completion from clearing a newer prompt. Reminder notices and flags are committed after a locked
due/source recheck. Automatic stale closure uses the same source commands and retires any active preparation root.

Acceptance uses isolated PostgreSQL, the actual native producer/consumer registration, intercepted provider HTTP, real Blazor
pages and Chromium screenshots. Fourteen subprocess-kill cases now cover Planning, Answering and Printing before/after a
synthetic effect and before/after checkpoint commit, plus final publication before/after commit. Recovery reuses committed
stages and publishes one source pack/notice. An effect whose checkpoint never committed may repeat; this is not exactly-once
provider inference. The probe refuses non-disposable databases and external HTTP.

Two intercepted-provider cases execute the real typed tasks, transport, usage sink, selective answer cache and Chromium PDF
renderer. A paid incomplete answer pauses Answering without discarding successful answers; retry reuses those answers and
the saved plan. Maximum-effort captured routes survive a policy change; operation cost includes failed paid responses, and
warm read-back performs no paid call. The resulting three-page PDFs are read back and all six rendered pages are visually
inspected. An unknown salary position must remain visible with its question/options rather than disappearing from the PDF.

These fixtures establish registration, accounting, recovery and rendering behavior, not real-provider content quality or
latency. Comparable real-provider measurements, notification per-effect receipts, remaining module/entry controls and final
production audit/deployment remain open. No production migration, restart, real provider/recipient effect, push or deployment
is performed by this acceptance checkpoint. See `docs/REFACTORING.md` for verification logs and remaining release gates.

## Native notification delivery

`DeliverNotification` (`jobfinder.notifications.delivery.v1`) runs on the existing sequential PostgreSQL notifications queue.
Each owned notice has a frozen account/chat/source input and a separately hashed effect plan. Text, document and keyboard
edits have individual attempts and receipts: confirmed multipart text is not repeated when only the document is rate limited.
The dedicated Telegram SDK delivery client disables implicit retries so every network effect belongs to one persisted attempt.
Missing files pause before any effect; replaced attachment bytes fail closed. HTML fallback applies only to an explicitly
rejected effect, never to an uncertain operation or the already confirmed multipart prefix.

`NotificationDeliveryRequests.QueueAddedAsync` captures every new tracked notice before saving generated IDs and joins their
owners/commands to the caller's enrolled transaction. It does not commit the caller's business transaction. Native late card
retirement uses this same admission and retains the original account/chat/message identity, even after configuration changes.
Disabled delivery pauses the saved target-bound plan without claiming an effect. A changed configured target cannot rebind it.

New version-2 admission can commit without a configured Telegram target. Its immutable admission JSON/hash remains separate
from the eventual target-bound input. `BindNotificationTarget` (`jobfinder.notifications.target.v1`) retains AwaitingTarget work
and schedules a continuation while configuration is unavailable; the first valid target binds once, atomically with the next
delivery command. Existing version-1 roots retain their original protocol. Intermediate source saves use `SaveJoinedAsync`,
which captures Added notices before generated IDs are persisted without committing. Terminal saves use `SaveAndFlushAsync`
to commit the business change, notification owner and native commands together. Neither helper permits an unenrolled context.
Native Discovery/Hiring/Preparation/Engagement workflow and source-decision boundaries, Candidate publication and typed policy
stores use these helpers. Standalone Hiring receipt/rejection and checked application/message/comment reconciliation also
publish through the native transaction; Reconciled never certifies the original payload or repeats delivery. Interview reminder
and debrief flags join their notices under the captured-source lock, so failed admission cannot consume the reminder.
Follow-up proposal flags, draft events and exact event-ID cards commit through `FollowUpSources` under the snapshot captured
before native letter preparation. NoResponse closure shares the source/notice transaction and the existing stage owner.
An admission failure leaves the source eligible and reuses a matching completed letter request without redrafting/reviewing;
changed source witnesses or unresolved employer delivery refuse the write. The timer does not save these source changes itself.
hh response history, external-application linking/duplicate cancellation, funnel/interview handoff and stage/summary/unread
notices publish through `HhNegotiationSources` in one enrolled transaction. Network reading remains outside that transaction.
Observation time is captured before the read; older captures and older site revisions do not overwrite newer history.
Unread-announcement cursors live under `hh.chat.unread.*` in `settings`, commit with their notice and rearm only after a read
observation. Unresolved initial/employer delivery refuses application mutation and live-chat admission; reading site history
does not reconcile delivery or certify the original letter/PDF.
Weekly statistics publish through `WeeklyReportSources` before optional paid hh advice. Frozen frame, exact native notice/root,
captured JobInvocation receipt/marker, planned-slot cursor and Prepare command commit together. Manual runs never claim slots;
replayed slots reuse their native receipt without another notice or paid call. The scheduler's terminal conditional write cannot
replace this source-owned Ok after a post-commit error. Recovery/noise retention preserve native markers and root evidence.
`WeeklyReportWorkflow` owns Prepare → Publish on Preparation; `WeeklyReportSteps` restores captured profile/resume/frame/date and
operation/recipe with maximum Resume reasoning. Outages resume the same request while statistics remain available. Result/hash
and next command checkpoint together; advice, full report, owner proof, completion and actual recommendation notice publish
atomically. No resume/demand completes statistics at admission with no paid command and does not erase older compatible advice.
Changed candidate/resume/newer report retains late results without publication. Known save exhaustion quarantines without paid
replay, not an exactly-once guarantee before checkpoint. Generic settings refuse report/advice/owner/cursor keys; native proofs
and original manual/start/slot receipt witnesses fail closed. `20261004060138_DurableWeeklyReports` refuses downgrade with roots,
receipt markers, publication evidence or orphaned current/future commands. Analytics is request/read-only at this boundary.
Real-provider, OS process-kill, interactive Analytics/owner-chat and final quality/performance/current-regression gates remain open.

Rejection analysis runs through `RejectionReviewWorkflow` on the Preparation queue, with Analyze → Publish checkpoints.
The facade admits and returns immediately. Capture freezes candidate/version, resume text, response/vacancy/event cohort,
computed facts, rendered date, operation/recipe and previous result/date. Duplicate unchanged controls reuse active roots or
same-day completed output; new evidence/force creates a fenced generation. `RejectionReviewSteps` is the sole paid adapter.
Outages resume the original request; result/hash and next command commit together. `RejectionReviewSources` joins result,
timestamp, owner proof, completion and native notice; publication retries never redraw the saved analysis. Changed witnesses
retain late output without publishing over the new source. Result-save exhaustion quarantines known output without an automatic
paid replay; death before checkpoint is not an exactly-once provider guarantee. Legacy unowned output remains readable, generic
settings refuse owned keys and damaged native result/date proofs fail closed. `20261004053531_DurableRejectionReview` refuses
downgrade with roots/markers or orphaned current/future commands. Real-provider, process-kill, Analytics browser/owner-chat and
final performance/regression acceptance remain open. Weekly-report/advice has its separate native boundary described above.

HR playbooks run through `HrPlaybookWorkflow` on the Preparation queue, with Build → Publish checkpoints.
The service/job are producer-only. Admission captures candidate versions/preimages, bank, evidence, funnel, date and
operation/recipe; desired-root markers fence late completion. Six parallel question tasks reuse individual GeneratedAnswers
checkpoints after an outage. Whole-result checkpoint and next command commit together; publication joins Latest/owner/request
flag, notice/root and completion. A known paid cache-write failure retains the available raw partial result and quarantines
without automatic paid replay; interrupted concurrent calls are not guaranteed exactly-once or fully recoverable.
HR position edits carry the displayed question and candidate witness. Candidate revision and new request join one transaction;
conflicts preserve the draft until explicit reload. Generic settings writes refuse these owned keys. Producers require current
migrations and a running native consumer. `20261004045533_DurableHrPlaybookPreparation` refuses downgrade with retained roots,
markers or orphaned current/future envelopes. Consumer-recreation and intercepted-provider tests do not establish live provider,
OS process-kill, interactive HR browser or owner-chat acceptance; retain these and final performance/regression release gates.

Career time/debrief/offer questions publish through `CareerPromptSources`, not unconditional ConversationStore writes.
An idle-slot insert and native notification/root admission share the source transaction; a competing or damaged dialogue
refuses replacement. Story starts also preserve other owners. Time/debrief callbacks bind the displayed interview source,
preparation root and company/title header; legacy tokens fail closed. Saved raw answers retain that original witness.
InterviewSources/InterviewTransitions consume the exact prompt in the source completion transaction, including unchanged-time
confirmation. A failed publication restores the prompt; a newer dialogue refuses late time/debrief completion. Late debrief
of Done interviews remains available. The callback adapter emits no empty duplicate notice after successful source admission.
Debrief inference itself remains interactive outside SQL: these controls do not establish a durable provider checkpoint,
exactly-once paid calls or live owner-chat delivery. Retain live/process and whole-plan release gates; rollback to the old
unconditional dialogue router is unsafe while captured prompts or unresolved native preparation/control roots remain.

Inbox admission publishes through `EmailInboxSources`. `EmailInboxJob` captures the account cursor, reads IMAP outside SQL and
publishes the batch. A single transaction retains cursor/preimage/receipt and joins incoming native commands. Same-batch replay
does not append a receipt or root; a different stale capture refuses. Missing/mismatched receipt, damaged state and unsupported
version fail closed instead of treating corruption as UID zero. Generic settings writes refuse the owned namespace and legacy key.
The identity includes endpoint/login/INBOX plus UIDVALIDITY and UID; password rotation does not change the account hash. First use
keeps the configured recent window. Known UIDVALIDITY change discards the old UID range and re-queries the folder in batches of
at most 100, preserving already admitted RFC Message-ID roots. Recovered mail requires manual reply, never new automatic
authorization from resynchronization. A missing RFC Message-ID is bound to account/epoch/UID; reread checks that same epoch.
MailKit opens read-only and a changing UIDVALIDITY during reading refuses publication. This follows
[MailKit's UIDVALIDITY contract](https://mimekit.net/docs/html/P_MailKit_IMailFolder_UidValidity.htm) and
[IMAP identity rules](https://www.rfc-editor.org/info/rfc9051/).
hh mail hints are durable operational state: a process-local trigger only wakes the existing scheduler. Successful hh source
publication joins acknowledgement of captures at least as new as the hint. Night/no-session/pause/error does not clear it; an
acknowledgement rollback retains it. Legacy `email.lastUid` remains an immutable import preimage, not an unsafe identity bridge.
Do not downgrade to the old cursor: exclusive reconciliation of the mailbox, native replies and cursor evidence is required.
Actual IMAP/TLS/owner-mailbox/process acceptance remains a release gate; synthetic batch and real MimeKit parsing tests are not
live mailbox proof. No production schema migration is needed for this slice's owned settings namespace.

Telegram history publishes through `TelegramChatHistorySources`; `RecruiterChatService` captures before MTProto reading and has
no business writes. The vacancy/application/contact witness includes captured controls and peer binding. Shared source locks fence
publication against new inbox admission and contact edits. History events, unique exact text matches, source revision/timestamps
and native notice/root/command commit together. Replay deduplicates channel IDs globally, without reparenting another application's
events. Pending/uncertain delivery, active incoming roots and changed sources refuse publication. Explicit history admission may
create an External application, never a native delivery receipt or verified PDF. Attachment-only text, common prefixes and ambiguous
matches cannot claim a saved event. No paid analysis/reply runs; real Telegram/process acceptance remains a release gate.

Historical-chat import publishes through `HhChatHistorySources`, with browser reads outside SQL. Captured application/chat/event
and persisted progress witnesses fence history-only events, the legacy marker and native notice in one transaction. No draft,
classification, funnel change or reply is produced. Incoming raw admission takes the same application row lock; an active incoming
root refuses history publication. Failure counts survive owner recreation; only two distinct failed captures retire the import.
`HhResponseBackfillSources` persists source-fenced negative caching: seven days for confirmed 404/410, one hour for format failures.
Actual response changes invalidate suppression; a normal sync timestamp does not. Damaged state fails closed. These owners and
transaction-race tests do not certify real browser reading or process-kill recovery; those release checks remain open.
Digest publication joins its reporting-window/calendar-slot cursor, exact notice/root/command and source Ok receipt. The
scheduler persists manual mode and slot before invoking `DigestPublications`; warm replay cannot reinterpret that witness.
Manual runs do not claim a planned slot, and catch-up reads the native cursor rather than manual/legacy diagnostic Ok rows.
Cursor/native-notice corruption fails closed; deleting old JobRuns does not lose the current slot witness. A terminal scheduler
write only replaces Running, preserving source-owned Ok after an outbox-flush error. Migration `DigestRunWitness` keeps old
rows' mode unknown and refuses downgrade while a native digest publication cursor exists; do not clear evidence to force rollback.
Other scheduled jobs and remaining card producers still need cutover. Alert cooldown and admission now share one
locked transaction, so concurrent alerts cannot consume a cooldown without retaining its notice.

Keyboard edits require the original notification ID and its exact confirmed primary-effect receipt. The original bot/chat
is recovered from immutable witnesses, not the current owner configuration; missing or corrupted proof creates a failed owned
request without guessing a destination or emitting a new message. A changed configured account still prevents dispatch.

Sending is committed before the SDK call. Same-envelope recovery turns an interrupted claim into `OutcomeUnknown` without
resending. Damaged or missing references quarantine the root without overwriting foreign attempts or replacement source owners.
An ambiguous result must never become ordinary Pending work. `--notification-reconcile <guid> --sent|--not-sent --evidence "..."`
records checked historical evidence only: no send, continuation, invented message identity or PDF-delivery certification.
Intact immutable witnesses can be reconciled after source drift; a changed current source remains untouched. Damaged witnesses
refuse reconciliation. The guarded migration refuses populated roots, attempts, owner markers and orphaned native envelopes.

`NotificationDispatcher` no longer sends/edits through Telegram. It only requests bounded `LegacyNotificationImports` batches,
independently of bot configuration but with server ownership and a started native publisher. Unowned Pending/Failed/Sending/
OutcomeUnknown rows become owned OutcomeUnknown, never known-unsent work. Original raw PostgreSQL row/hash, immutable v2
admission, source status/marker and `QuarantineLegacyNotification` commit together. This command only validates; no target
binding, document read or effect occurs. Opaque capture retains damaged keyboard JSON without starving later rows. Native
roots with missing source markers are excluded before bounded selection, not silently repaired or reimported.

`--notification-reconcile` records checked legacy aggregate evidence as a typed assertion/hash under the original source
witness. Reconciled creates no attempt, effect/PDF certification or continuation in either direction. Source drift and damaged
witnesses refuse the write. Original status, attempts, error/message ID and attachment metadata stay in the raw admission.
Terminal unowned history and every native/unresolved record remain retained. The guarded migration refuses original admissions,
reconciliation evidence and orphaned current/future quarantine commands, even if the current input is damaged.

This removes dual notification executors in code, not the full release obligation. Remaining producer/control audit, dedicated
retention, real-process kill, live owner-chat/browser/PDF acceptance and real-provider measurements remain required. Intercepted
SDK/subprocess tests do not establish live Telegram delivery acceptance.

### Interactive owner-content publication

`BotService` and `CareerBot` publish message/document results through `OwnerBotMessages`, including dialogue responses, help,
approval-card refreshes and background reports. Only `NotificationDeliveryTransport` calls the Telegram message/document SDK.
The producer freezes the originating bot and private owner chat through `PublishCapturedAsync`; it does not defer destination
selection to later deployment configuration. The existing version-2 admission and version-1 effect plan remain unchanged.
Publication completion means notice/root/command admission, not confirmed Telegram delivery or an employer/PDF receipt.

Owner output preserves its complete text and requested attachment. A report exceeding the document caption limit becomes
separate text/document effects rather than truncating the caption. A missing attachment pauses before either effect instead of
silently becoming text-only. Explicit parse rejection may use the existing per-effect plain-text fallback; unknown document
delivery retains the confirmed text receipt and never triggers an inline fallback/error message or blind retry. Changed bot/owner
configuration cannot redirect an admitted output. Bot handlers do not answer unowned authors or non-private owner chats.

Business source owners continue to publish their own transaction-joined notices; these interactive outputs do not replace them
or assert that a separate source commit and output admission were atomic. Callback acknowledgements and typing indicators remain
transient control SDK operations, not notification/employer receipts.

### Interactive keyboard controls

Main/career outcome and clear-keyboard controls also use `OwnerBotMessages`. `PublishKeyboardAsync` finds exactly one original
native text/document receipt for the captured bot/chat/message, with matching source and effect hashes; reconciled or ambiguous
primary records do not certify an editable target. Legacy/unproven targets are left unchanged, without a fallback SDK call.
The new edit captures that parent notification/entity and joins its notice/root/command in the existing native transaction.
No schema or notification input/plan version changes are introduced.

The producer serializes under the notification advisory lock. Row completion merges the latest valid saved desired rows, so a
callback carrying older markup cannot restore other rows. Identical active/completed intents reuse the same root. Before a new
effect claim and again immediately before SDK admission, a higher parent-edit notice supersedes delayed older work. An earlier
sending/unknown or unresolved attempt blocks the next edit until checked ledger reconciliation, even after pacing expires.
Known original version-1 edit roots without parent metadata retain their previous protocol; new controls require exact lineage.
This is a visual-control publication, not certification that its separately owned source action or employer/PDF delivery succeeded.
Process-kill/live owner-chat acceptance and the whole-plan release remain separate gates.

### Typed edit-question publication

Letter, comment, reply and outreach edit questions use `BotEdits.BeginQuestionAsync`: the typed frozen source witness and
intent document commit with the native question notice/root/command. No bot-handler SDK question or inline-send fallback remains
for these four controls. The native transport sends literal text with ForceReply and no HTML parsing or HTML fallback.

Only these questions use notification input version 3 and plan version 2 with one `EditPrompt` effect. Ordinary version-1/2
inputs and version-1 plans keep their previous shapes/hashes; no schema migration is needed. Preparation and effect admission
check the persisted intent, captured account/chat, supported witness and unbound/uncompleted state before the SDK call.
The confirmed SDK receipt and exact message binding commit in the same transaction. A failed receipt commit rolls both back
and retains unknown delivery, never automatically resending the question. A trusted owner reply quoting the saved intent marker
may recover the binding and retain user text even before receipt commit; that recovery does not certify the notification receipt.

These controls do not analyze, publish or send employer content. Existing source gates still decide what the retained user text
may change. Intercepted SDK and
transaction-fault tests are not proof of actual process-kill recovery or live owner-chat delivery.

## Incoming reply analysis and redraft

`ReplyService.HandleInboundAsync` is an admission-only producer. It commits immutable raw input/hash and `ProcessReply` before
returning to Telegram/email/hh collectors, without a paid model call or a wait on the delivery handler. `ReplyProcessingWorkflow`
owns `reply_processing_requests` and the independent PostgreSQL `hiring_replies` queue (two concurrent handlers). For one
application, incoming capture waits for earlier active incoming roots; other applications can continue independently.

Capture freezes candidate revisions, full vacancy/conversation and scheduled interview witnesses, stories, time/zone,
automation and LLM operation/recipe. Fast analysis, each of up to two deep improvements, and publication are separate durable
stages. A model outage schedules the same stage; it never commits a synthetic unclassified success. Publication joins event,
funnel/interview changes, automatic employer-message preparation, native card/notice and terminal root in one transaction.
The card explicitly says an automatic reply is queued and delivery unconfirmed; only the existing delivery ledger can prove
an external outcome. There is no direct transport, inline analysis fallback or unchecked resume fallback in the producer.

Redraft captures through `ReplyDraftSources`; its final source revision/card/native notice join that transaction. `--reply-redraft`
waits for the server, while `--preview` publishes no source or card and can reuse matching completed preparation. Ctrl+C stops
only the wait. Changed witnesses cancel publication and retain the raw input. `--reply-process <guid>` reads status;
`--reply-process <guid> --retry` explicitly creates a linked analysis generation only for Cancelled/Failed Incoming roots with
valid raw input and no already-committed external message. This is not a resend or delivery reconciliation, and cannot certify
historical letters/PDFs. Completed/redraft/preview roots cannot use that retry command.

`--email-reprocess <event-id>` is a corrected-source producer, not ordinary redrafting and not deletion followed by Incoming.
It captures the original editable event/conversation/candidate/interviews before fetching the mailbox, requires the same email
Message-ID, sender and original instant, and freezes corrected text/subject in a distinct Reprocess root. Input v2 retains the
immutable original event JSON/hash; existing input v1 JSON and command v1 remain compatible. The existing Fast/Deep/Publishing
executor updates that same inbound event ID and revision, retaining all previous events and native roots. Classification,
funnel/interview changes, the normal restrictive automatic-reply handoff, card and completion still share the final transaction.
No earlier mistaken transition is deleted or used to override the funnel's transition rules. A paid/review outage leaves the
original event untouched and resumes its saved stage. Active, sent or uncertain delivery and changed witnesses fail closed.
Repeated matching preparation reuses a pending/completed correction; after a failed/cancelled correction an explicit repeated
command can admit a linked fresh analysis with freshly checked inputs, retaining the failed root and never resending a message.
Ctrl+C stops waiting, not the saved request. Older executors cannot process the new Reprocess mode; retained roots are not a
safe downgrade boundary. Synthetic PostgreSQL tests do not establish live mailbox/provider/recipient acceptance.

Migration provisions the pinned native queue and refuses downgrade while roots or orphaned queue/incoming/outgoing/dead-letter
envelopes remain. Twelve actual OS process-kill cases cover capture before/after checkpoint, fast/deep analysis before/after an
independently committed fake provider effect and checkpoint, and final source/card publication before/after commit. Recovery
reuses every saved analysis checkpoint and preserves raw/candidate input; only a result lost before checkpoint repeats its
fake paid call. Exactly one inbound event and joined native card remain. This is process-kill proof with synthetic analysis,
not real-provider quality/cost or live owner-chat/browser acceptance. Remaining operational producers, dedicated retention,
real-provider measurements and the whole release audit remain open.

## Bot control admission and execution

`BotService` polling does not run commands inline or acknowledge an update before persistence. `BotControls` commits eligible
raw SDK update/owner/account capture and native Process command with the account cursor, under a PostgreSQL advisory lock.
Unowned updates only advance that cursor; the bot no longer sends unsolicited `/start` replies to other users. Empty polling
batches do not rewrite progress. Stale poll captures cannot overwrite a newer offset, and damaged cursors fail closed.

The separate sequential `bot_controls` queue keeps controls out of paid-preparation and notification queues. Its executor
checks the captured account, owner and exact SDK update identity before claiming, then calls the existing source handlers with
the dedicated no-implicit-retry control client. Unavailable configuration schedules a retry without claiming or dispatching.
Claim plus envelope identity commits before execution. A handler error, startup interruption or claim replay becomes
OutcomeUnknown with a native notice, never a replay. Later controls for that account remain admitted but wait behind uncertain
or quarantined work. `--bot-update-reconcile <guid> --evidence "checked outcome"` records the operator's assertion and unblocks
later controls without repeating the original, rewriting input or certifying SDK/employer/PDF delivery. Handler completion is
not a receipt from Telegram and does not replace any feature's source witness or employer delivery ledger.

Migration `DurableBotControls` provisions the pinned queue and control roots. Downgrade refuses retained roots, account cursors,
queue/scheduled messages and incoming/outgoing/dead-letter controls, including future identities. Dedicated retention and
live owner-chat acceptance remain open. Eight actual OS process-kill cases now cover before/after admission commit, before/after
claim commit, before/after an independently committed fake effect and before/after completion commit. They verify cursor/root/
command atomicity, conservative unknown recovery and no repeated effect. The probe refuses production ports and external HTTP;
this process-kill proof does not certify delivery to real Telegram or employer/PDF receipts.

## Automatic outreach scans

Company-contact observations and retirement are owned by `Modules/Hiring/ContactSources`, not the registry facade or UI.
Every observation increments a revision; SQL post observations join the source transaction, and manual publication plus native
notice commit together. Retirement checks the exact displayed `ContactEditSnapshot` under a contact row lock, sets `RetiredAt`
and advances the revision with one native notice. Neither the contact nor its outreach history is deleted. A sighting cannot
silently restore that identity. Registry selection, outreach capture/publication, direct recruiter preparation and employer
source matching exclude retirement; inbound matching/history still retain the address. This does not alter the independent
initial-application routing from a vacancy's typed contacts.

Employer admission captures the contact under `FOR SHARE` through root/command commit; retirement uses `FOR UPDATE` and checks
all related employer history, including opaque future inputs linked only by outreach ID. Sent/sending/unknown or unsupported
outreach status refuses retirement. Thus an admitted operation preserves its history; retirement winning the race prevents new
admission, without guessing an external outcome or consuming paid work. Both dashboard contact surfaces retain their displayed
witness and typed draft after conflict until explicit reload. `RetiredCompanyContacts` preserves legacy IDs/data, defaults old
revisions to zero and refuses downgrade after revision/retirement changes. Browser/live recipient acceptance remains separate
from the isolated SQL and actual-component render proofs.

Automatic outreach selection uses `OutreachScans` for PostgreSQL reservations and retry progress, not a process-local tried set.
Reservations serialize under the scan advisory lock, select eligible distinct vacancies by score before the configured bound,
and expire after 30 minutes if preparation exits. Unchanged failures back off from 30 minutes to six hours; candidate/source,
contact, confirmed stories, policy or recipe changes make a fresh attempt eligible immediately. Completion compare-exchanges
the captured token/hash; late failures and duplicate completions cannot overwrite or extend newer progress. Damaged rows remain
intact while the scan continues. Generic settings writes cannot reset this namespace. This operational receipt neither publishes
a draft nor authorizes delivery; `OutreachSources` and the shared employer gateway remain authoritative.

## LinkedIn people reading

Offer analysis is a separate native Hiring use case; see the offer preparation section below. It does not borrow LinkedIn search budgets or automate a negotiation send.

`LinkedInPeopleReader` is a request/wait facade. Browser search remains a read-only adapter outside SQL; immediately afterward,
`LinkedInPeopleRequests` admits the captured card/parsed observations, company, frozen LLM operation and canonical recipe plus
`ReadLinkedInPeople` in one transaction. Paid reading belongs only to `LinkedInPeopleSteps` / `LinkedInPeopleWorkflow`, using the
existing native `hiring_replies` queue. Matching page/recipe requests are admitted once; pending compatible work and completed
compatible results from the last day are reused before opening the browser. Saved-file CLI reading shares that executor.

The root claims one envelope before paid work, persists reading/hash at completion and verifies the exact saved input/result
before returning. A transient result-save fault retries the in-memory result, not another model call. A model outage schedules
the same saved page for two minutes later (30 minutes for unauthorized); it never silently substitutes a rule result. Only an
empty-card page uses the explicitly captured parsed/rule path. Cancelling a producer wait leaves work admitted. Damaged input,
result or recipe fails closed; unsupported roots retain the page with a native alert. Migration `DurableLinkedInPeople` refuses
downgrade with retained roots or orphaned queued/scheduled/incoming/outgoing/dead-letter commands, including future identities.

`LinkedInSearchQuota` serializes reservations of the existing `linkedin.searches.<Minsk date>` counter in PostgreSQL. All browser
search callers share this owner, including CLI. Disabled/exhausted budgets refuse a read; corrupt budgets retain evidence and
refuse generic reset/removal. The slot is spent before browsing and never implicitly refunded after failure. A saved-page model
read spends no browser budget. No LinkedIn message/connection effect is automated. Isolated native-consumer, provider interception,
owner recreation and SQL rollback tests are not real-process kill, live LinkedIn browser acceptance or real-provider measurements;
those checks and the rest of the full release remain open.

## Interactive story preparation

`StorySources` owns question/answer dialogue mutations, explicit confirmation and skip/stop. Each question or answer transition
joins its original captured candidate/dialogue witness and native notice in one source transaction. `StoryBank` is a read/request
facade, not another paid executor. The original profile/fact versions, resume text/hash and four-question roster remain frozen
through the dialogue; an unreviewed profile is valid for interactive fact gathering, not employer delivery authorization.

The fourth raw answer, waiting dialogue, hashed `StoryPreparationRequest`, `DraftStory` and queued notice commit atomically.
`StoryPreparationWorkflow` uses the existing Preparation queue for Draft → Publish. `StoryPreparationSteps` is the only
`ComposeStoryAsync` caller and restores the captured Coach operation/recipe. Draft JSON/hash and `PublishStory` checkpoint together;
publication checks the same source/candidate witnesses and commits ready dialogue, complete STAR card and completed root together.
An unavailable provider resumes the saved stage. A publication failure or restart reuses its paid draft. Damaged/future checkpoints
quarantine once. Three exhausted result-save retries retain the draft in a failed root/alert/dead letter, never automatic paid rework.
Death before a durable paid checkpoint can still repeat an interrupted model call; this is not exactly-once provider execution.

Draft completion never inserts confirmed candidate evidence. The displayed confirmation checks the exact completed root/result
and dialogue, then atomically commits Story/history, native notice and compare-clear. `Story.PreparationRequestId` retains provenance.
Redo preserves the previous draft while waiting but refuses its old confirmation. Skip/stop use exact versioned controls, cancel
only their pending root and preserve raw/result evidence; a late draft cannot overwrite a subsequent achievement or unrelated dialogue.
Legacy unmarked drafts remain readable and explicitly confirmable. Cancelling a producer wait does not cancel durable work.

Migration `20261004002414_DurableStoryPreparation` refuses downgrade with roots, confirmed source/history markers, captured story
dialogues or orphaned immediate/scheduled/incoming/outgoing/dead-letter commands, including future protocol identities. Never delete
evidence to force rollback. Disposable PostgreSQL/native-consumer restart and intercepted provider proofs do not establish actual
process-kill, live owner-chat/provider, comparable performance or whole-plan release acceptance. Production remains unchanged.

## Learning progress source publication

`LearningProgressSources` owns marks and answer approvals, not paid rehearsal execution. Legacy progress JSON and newline
approvals remain readable without bootstrap writes. First publication retains their exact raw preimage, adds v1 metadata bound
to normalized hashes and appends immutable scope/item revision history. Unsupported/damaged storage or missing/mismatched
history fails closed rather than clearing knowledge or approval evidence.

Standalone controls enroll one EF/outbox transaction for settings, history and native notice/root/command. Joined publication
requires the parent's transaction and never commits it; the parent remains responsible for enrollment and terminal flush.
Scope advisory locking and locked fresh rows merge different items. Same-item revision fences include ABA/tombstones; full reset
checks the captured whole scope and clears knowledge only. It does not clear content-fingerprint answer approvals.

Dashboard editors retain the displayed witness and desired mark/approval/reset through conflicts/admission errors. Reload is
explicit and applying the retained change is a separate action. Failed learning-card publication does not advance the deck.
Rehearsal captures the item before paid review and refuses to overwrite a concurrent manual mark. Native Mock publication now joins
that mark/history to the session/dialogue transaction, as described below; learning controls do not own paid rehearsal execution.

`OwnedLearningProgress` refuses downgrade with retained history or ownership markers, including unsupported future markers.
Disposable PostgreSQL/intercepted-provider/SSR tests are not real-browser interaction or live owner-chat release acceptance.

## Mock interview preparation and source publication

`MockInterviews` is a capture/admission facade. `MockSources` owns session revision/preimage history, exact global-dialogue changes,
prepared rehearsal start, raw answer admission, captured stop controls and source/card publication. Question requests capture the
original profile/fact versions, confirmed stories, full vacancy, kind and roster header before paid work. Prepared HR/interview
rehearsal retains its exact question/ideal/item roster rather than generating substitute questions. Empty preparations refuse.

`MockPreparationWorkflow` runs Preparing → Publishing on the existing preparation queue with one adapter, `MockPreparationSteps`.
Questions/review/summary each capture the Coach operation/recipe. Answer admission writes raw text, waiting marker, revision/history
and command together. Paid output/hash and publish command checkpoint together; publication uses that saved output. Last-review
publication atomically saves its feedback, learning update, exact session/dialogue witness and a child summary request/command;
it never waits in a handler. Summary uses the saved scored transcript and completes source/history/dialogue/card/root together.

Changed candidate/source/dialogue or incompatible recipe refuses a late result and preserves evidence. A concurrent manual learning
mark wins; review publication retains that mark and explains the conflict in its saved feedback. Source/learning/history/card/root
rollback together. Provider outages retain inputs and reschedule only the failed stage. Final publication failure does not redraft
or re-review. Result persistence retries the in-memory result three times; exhaustion retains it in a quarantined failed root.
As elsewhere, process death before the result checkpoint can repeat a provider call; this is not an exactly-once API guarantee.

`/stop` uses the captured dialogue; buttons bind session ID plus revision. Unversioned/old controls fail closed. Cancellation or
replacement cancels only the matching pending root and preserves raw/partially reviewed answers. An early stop summarizes only
scored turns; it never invents a score for an unreviewed saved answer. A new non-mock dialogue is not silently overwritten.

`DurableMockPreparation` preserves legacy session rows with revision zero and refuses downgrade with retained roots, source/history/
dialogue markers or orphaned immediate/scheduled/incoming/outgoing/dead-letter commands, including future protocol identities.
Native listener restart, intercepted HTTP, source/outbox fault and compiled boundary tests use disposable infrastructure; actual
process-kill, live owner-chat/provider, comparable quality/performance and final current regression remain release gates.

## Scrape request admission and operational state

`ScrapeStateStore` implements the production `IScrapeRequestLedger`. It reads compatible pause/budget strings and owns the
revisioned `scrape.state.<family>`/`scrape.owner.<family>` projection. All reads and transitions serialize under one PostgreSQL
family lock. Missing ownership, damaged native/legacy values or mismatched materialization fail closed before HTTP. Generic
settings writes and old whole-snapshot Save refuse. No per-host row is created for an ordinary unbudgeted successful crawl.

`PoliteHttp` checks fresh pauses and reserves each direct/proxy attempt before the wire. Local standard windows/pacing and Polly
remain; saved windows are the final cross-producer budget boundary. Background has a separately persisted share, while High
bypasses budget counts but never an active pause. A denied reservation commits nothing. A bounded quota wait/cancellation is
outside the HTTP timeout and uses the originally requested deadline; proxy fallback must reserve its own attempt too.

Block streak/pause/projection and its native notice join one source transaction; repeated active-block observations reuse it.
Success carries the original block revision and cannot clear a newer pause, including after its expiry. Compatible expired legacy
pause removal is now atomic with streak/budget publication and a recovery notice. Announced exceptions mean notice admission,
not confirmed external notification delivery; job adapters retain fresh-block semantics without admitting a duplicate alert.

`OwnedScrapeLedger` is a rollback guard for the operational protocol, with no new table: legacy-only down/up is compatible, but
retained native state/ownership refuses downgrade. Do not delete native markers to force a downgrade. There is no new paid or
external-delivery executor. A crash after reservation can count a request that never reached HTTP, deliberately conservative;
source-response persistence is not an exactly-once HTTP transaction or cross-process guarantee for local random pacing. Scoped
PostgreSQL/native notice/actual HTTP-handler/fault/time/boundary tests are recorded in `docs/REFACTORING.md`. Real sites, restart/
process-kill and cold/warm/retry provider/performance, plus the remaining audit and full release, remain open.

## Captured automation controls and hh captcha blocks

`ApplicationBlocks` is the only writer of the compatible `apply.blockedUntil` setting and its separate persistent revision marker.
`application_block_revisions` retains previous/raw values, actor, publication time and the original native known-not-sent attempt.
Reads take the shared transaction lock and validate the marker/latest history/materialized value and timestamp; malformed or missing
native evidence fails closed. Legacy unowned values are read without mutation and retained as the first change's preimage.

`DeliveryLedger` joins automatic block history/materialization and notice to its existing native known-not-sent receipt/retry outbox.
It never shortens a longer block, and a replay of the same attempt cannot publish a second revision/notice. Sending, unknown and
reconciled attempts cannot establish this automatic block. There is no new delivery executor, SDK call or resend authorization.

Manual release compares the exact original `ApplicationBlockSnapshot` under that lock, including revision, value and timestamp.
Captured absence cannot erase a later block; retained release tombstones fence same-value ABA. `ApplicationAutomationControls`
uses the validating joined automation publisher in the same enrolled context: policy/history/notice and block/history/notice commit
together. Pausing does not consume a release witness. `/resume` captures both sources at admission; hh login captures the block
before opening the browser, never recaptures a new one just to remove it after login.

Overview/Facts/Settings controls retain their displayed witnesses and desired drafts after conflict or unconfirmed admission until
explicit reload. A dashboard timer cannot rebase a conflicted action. Generic block Set/Remove refuses. `OwnedApplicationBlocks`
allows compatible unowned legacy down/up but refuses retained native history or revision tombstones. Scoped disposable PostgreSQL,
native notice admission, actual Razor handler/SSR, intercepted admission-fault and compiled-call checks are recorded in
`docs/REFACTORING.md`; they do not establish live browser/owner-chat/process-kill acceptance or complete the remaining operational audit.

## Captured Telegram channel decision publication

`Modules/Discovery/ChannelDecisions` is the user-decision and proposal-card writer. Row commands retain the displayed
`ChannelDecisionSnapshot` (identity, membership status, added instant and DecisionRevision); bulk UI approval retains only
the displayed Proposed cohort. A bot callback identifies a persisted `telegram_channel_approval_batches` row, not a live
query. Its protocol/hash, linked native notice/root and callback are checked before applying the saved snapshots.
Old zero-ID/text-only cards fail closed. All members must still match; one conflict changes none. Applied-cohort replay checks
the retained history but does not approve anything again, including a member subsequently rejected or a newly proposed channel.

The shared advisory lock serializes native decisions/proposals. Decision history, source revision/status, cohort AppliedAt and
notice/root/command commit together. Proposal/card creation enrolls the source context before intermediate generated-ID saves;
each card has its own explicit notice identity. Cards contain at most twenty channels, retain every visible identity within the
text limit even with escaped reasons, and never expose approval when truncation would hide a member. Conflicted row/batch drafts
and witnesses remain visible through filter/reload and missing rows until explicit discard/reload.

Status and DecisionRevision are EF concurrency tokens. Runtime metadata/counters do not advance the user revision. Legacy sync
checks the original approval before resolving/joining and drops stale status publications, including folder/history conflicts.
This is **not** a durable membership executor: resolve, join, mute/archive/folder and history still lack native claim/attempt/receipt
checkpoints. A later rejection can race an already-started remote join; a successful remote effect followed by SQL failure is not
certified or automatically reconciled here. Complete membership ownership/process/live acceptance remains release work.

Migration `CapturedTelegramChannelDecisions` preserves unowned revision-zero channels and refuses downgrade with retained
cohort/history/source revisions. Do not delete these witnesses to force rollback. Disposable PostgreSQL, actual Razor-handler/SSR,
native admission-fault and compiled-call acceptance are recorded in `docs/REFACTORING.md`; they do not certify live Telegram or
production cutover.

## Offer preparation source and executor

`OfferAdvisor` admits and returns immediately. `OfferPreparationRequests` freezes profile/fact versions, open application/vacancy
witnesses, optional original dialogue, market samples/text, comparison offers, conversion basis and the maximum Resume operation/recipe.
Waiting dialogue with raw owner text/request marker, request hash, `AnalyzeOffer` and queued notice commit together. Same-dialogue
commands reuse one root before recapturing newer data. Manual submissions are separate intents and never overwrite another dialogue.

`OfferPreparationWorkflow` runs Analyzing on `hiring_prepare`, then Publishing on `preparation`. The sole paid adapter is
`OfferPreparationSteps`. Saved analysis/hash and `PublishOffer` share the checkpoint transaction. Publication uses the same source
owner and enrolled outbox: offer/history, matched funnel stage, exact waiting-dialogue completion, full negotiation card and final
root receipt commit together. A final-stage retry/restart never reruns analysis. Missing/changed/damaged source or recipes refuse,
not rebase. Provider outages keep the same inputs and reschedule; generic stage failures retire after four attempts.

`/stop` captures the persisted waiting dialogue and marker, cancelling only that exact root/state with its notice. Late paid results
cannot publish after cancellation; a newer conversation is untouched. Cancelling a producer wait does not cancel the durable request.
Result-save faults retry their in-memory result three times; exhausted persistence retains the result in a quarantined failed root
with a native alert and dead letter, never an automatic paid replay. A process killed before a paid checkpoint can repeat its call;
saved checkpoint recovery is not an exactly-once provider guarantee. Negotiation text is copy-only; no employer transport is called.

Migration `DurableOfferPreparation` refuses downgrade with retained roots/source/dialogue evidence or immediate/scheduled/incoming/
outgoing/dead-letter offer commands, including future protocol identities. Do not delete evidence to force rollback. Disposable native
restart/fault/provider-interception tests pass; actual process-kill, live provider/owner-chat and comparable performance release tests
remain open. No production migration or deployment is certified by these scoped checks.

## Native Telegram membership and Ubuntu operator recovery — 2026-10-05

The earlier captured-channel section records its pre-membership boundary. Current execution uses a single discovery queue
consumer, `ProcessChannelMembership` v1, and `DurableChannelMembership` schema. Root/stage/attempt pointers, account/peer,
input/chat/history hashes, receipts, timestamps and envelopes are validated before effects and final publication. Resolve and
ReadHistory checkpoint reusable results. Joining/Muting/Archiving/Filing claim immutable attempts before the wire; next stage
and receipt share the transaction. Joined source, deduplicated posts, post roots/commands and final notice commit together.
A successful RPC receipt survives a later source conflict; it cannot authorize overwriting a newer user decision.

`INVITE_REQUEST_SENT` retains InviteRequested and stops, without a Confirmed receipt. Negative/null SDK receipts cannot certify
success. FLOOD_WAIT retains known rejection and shared saved delay. Malformed flood/pacing values fail closed; never reset them
to enable work. Ownership and original account are checked at the actual SDK boundary, including after folder reads.
`WaitAsync(ct)` cancels observation, not an issued SDK call. Sending at crash, lost response or unpersistable receipt becomes
OutcomeUnknown; no automated continuation or replay is allowed. Saved successful checkpoints resume without repeating them.

Operator procedure: stop new approvals for the affected channel; read the original root, exact attempt/account/peer/stage,
input/chat hashes and source marker/revision. Independently inspect that account's state for that exact operation. If evidence
is sufficient, invoke the producer CLI against the same current schema:

```bash
dotnet src/JobFinder.App/bin/Debug/net10.0/JobFinder.App.dll \
  --channel-membership-reconcile REQUEST_GUID --attempt ATTEMPT_GUID \
  --applied --evidence "checked account/peer/stage and observation details"
# Use --not-applied instead only when the original operation was verified not applied.
```

The command records Reconciled, not Confirmed/Joined, and makes no MTProto call or restart. A fresh explicit approval is a new
captured operation; it is not a retry of unknown work. Changed source/attempt or damaged proof refuses reconciliation.
Preserve all evidence when verification is impossible. Late receipts cannot resume unknown roots. Do not mark invitation
requests as membership, infer a failed join from missing local SQL, or treat the chosen CLI flag itself as independent proof.

Rollback: the migration preserves legacy channels and restricts root/attempt deletion. Down refuses any roots, attempts,
orphan source markers, shared native delay keys and current/future membership envelopes in immediate/scheduled/inbox/outbox/
dead-letter storage. Exclusive production migration requires the sole server stopped and its ownership lock. Earlier Down
steps may already have committed before a later guard refuses: inspect the applied chain and restore forward, never erase
retained evidence to force rollback. Disposable negative migration fixtures restore latest schema before read-back.

Production remains gated by current full acceptance, real provider/browser/owner-only SDK/PDF/quality/performance evidence,
backup and runtime access. The precise requirement matrix, reproducible commands and evidence limits are in
`docs/UBUNTU-ACCEPTANCE.md`. No production migration/deploy is implied by this source update.
