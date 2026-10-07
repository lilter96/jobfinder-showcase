# Search policy ownership

`policy.search` is the typed, versioned PostgreSQL aggregate for vacancy filters, post prefilters, HeadHunter searches/quiet hours, Habr selectors, company searches/tags/schedule and Threads search selectors/limits. Core DTOs own the persisted shape; Discovery compiles them into source request adapters and pure rules. No secrets, browser selectors/sessions, transport credentials, scrape budgets or progress cursors are stored in it.

The first read imports the effective layered Filter/Prefilter/HeadHunter/Habr/Threads Options, atomically with publication history and an outbox notification. After that, the persisted policy is the only preference owner. Old file entries remain bootstrap compatibility inputs, not runtime overrides. Missing known fields, invalid schedules/selectors and corrupt current revisions fail closed. A document deleted after publication is not silently recreated from defaults; restore the verified document/history together.

## Publish and read

`SearchPolicyStore.ReadAsync` returns a detached value and revision. `PublishAsync` validates the whole aggregate and requires that revision; policy, history and notification commit together. Conflicting updates fail without overwriting the other writer. The settings page shows and edits this authoritative policy: normal filter/search controls, plus an advanced typed JSON form for complete search profiles. Advanced text is applied to the form before the aggregate is saved. No settings/state file is written.

HeadHunter, Habr, company and Threads collectors capture a policy per pass. Vacancy scoring uses one filter snapshot for both pre- and post-model checks. Post extraction uses one prefilter snapshot for the post, enrichment eligibility and enriched content. The doctor and manual scoring use the same policy instead of old Options.

## Calendar and safety

Dynamic collectors remain alive while disabled, without creating disabled runs or making requests. Their schedule is checked at most once per minute. A policy revision recomputes the next due time from the completed pass, not from every poll; configuration polling does not become a new run. A re-enabled job retains its initial/calendar delay and missed-slot behavior. Explicit run triggers remain distinct from timer expiry. An active pass retains its original policy and finishes before subsequent schedule refresh.

HeadHunter quiet hours also gate the first/manual pass; requests still go through PoliteHttp priorities, shared-family budgets and cooldowns. Company work retains the low-priority overnight schedule. Policy publication does not release a paused site or change stored operational cursors/cooldowns. Source transport/session configuration remains separate from preferences.

## Engagement preferences

`policy.engagement` owns automatic versus approval-mode comments, the daily draft limit and author cooldown. Its typed store imports the effective Threads Options once, validates every revision, and publishes the document, immutable history and notification atomically with an expected-version check. Secrets, API mode/endpoints, browser selectors/sessions and the operational emergency pause are not preferences in this aggregate. Generic document writes refuse every `policy.*` key; only validating feature stores publish policies.

Threads captures one Engagement snapshot before starting a collection pass. A preference change during HTTP/LLM work applies to the next pass, not to part of the current one. Already approved drafts are not revoked by switching off automatic approval: the emergency comments pause is a separate control. The dashboard keeps conflicted edits and their original version until explicit reload. This policy migration does not certify the still-legacy comment publication worker; durable preparation/publication and emergency controls at dispatch remain open in the full refactor.

## Verification

Tests cover public effective import parity, detached nested collections, required/tolerant persisted fields, host/request-parameter validation, concurrent import/publication, history/outbox rollback, fail-closed corruption/deletion and startup ordering. Disposable PostgreSQL plus fake HTTP verifies that a real Habr collector uses published selectors/markers and ingests the resulting vacancy, and that Threads retains its captured publishing preferences when another writer publishes during search. Fake-clock collector tests cover disabled/re-enabled execution and schedule changes without extra poll runs. Native Blazor rendering and actual Chromium editors verify published values, retained conflict drafts and explicit reload. Compiled-call ownership tests restrict bootstrap preference reads and generic policy publication to the validating import owners.
