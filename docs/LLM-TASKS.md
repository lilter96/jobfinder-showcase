# LLM task ownership

`LlmTaskCatalog` owns all 22 tasks, including free-text outreach and extraction. Each task declares its input/binding builder, prompt, allowed purposes, strict native wire contract, semantic validator, processing version and synthetic probe. The existing `JobFinderLlm` facade maps wire results into tolerant domain records; it no longer builds task-specific prompt bindings or schemas.

## Request compilation

`LlmPolicyCompiler` compiles every model in a route, including fallbacks. Unknown purposes fail and missing routes do not borrow Score models. Application and Resume require maximum reasoning on every model. Legacy ExtraBody is an import adapter accepting only typed reasoning/provider fields; protected fields, unsupported extensions, duplicate typed/legacy owners and disabled strict parameters fail before HTTP. The caller's configuration objects are not modified.

Endpoint, API key and proxy remain deployment concerns. Runtime model registry, model capabilities, route steps, quality profiles, attempt limit and daily budget are owned by the typed `policy.llm` PostgreSQL aggregate. The first import captures effective layered Options, not just public defaults, with history/outbox in one transaction. After import, changing bootstrap preferences does not override the persisted policy. Publication validates the entire aggregate and expected revision atomically.

Policy initialization runs after exclusive server ownership and database migration, before collectors/workers. Invalid persisted policies stop initialization; corrupt or missing required fields never cause a fallback to bootstrap settings. Native deployment validation deliberately does not keep validating obsolete imported preferences as a second policy owner.

## Contracts and probes

Canonical schemas come from source-generated wire DTO metadata. Local schema evaluation precedes deserialization and the task's semantic validator. Provider settings never disable this local validation.

`ProviderSchemaProjection` owns the versioned OpenRouter projection (`canonical-v1`): strict JSON schema, JSON-object mode with the complete schema in the prompt, or prompt-only mode. Projection objects are detached from the canonical schema; even a provider ignoring the schema cannot bypass local required/null/enum/extra-field validation. The identity projection retains the existing cache version; an intentional projection change must bump its version.

The required prompt roster is derived from the catalog. `--test-llm [purpose]` now probes each applicable task/model pair using synthetic inputs, its actual prompt/bindings and its own schema/validator. In particular, extraction-shaped JSON cannot pass an application probe. Probes are real paid calls when run against configured providers; an unrestricted probe can make substantially more calls than the former extraction-only smoke test. Tests exercise the same path using fake HTTP, without private inputs or employer messages.

## Cache recipes

`GenerationRecipe` captures task/purpose, canonical contract hash, prompt hash, processing rules, compiled route hash and the current identity provider projection version into a Core `ArtifactRecipe`. Named static input hashes and quality/postprocessing versions join the same canonical identity; property order does not matter, task execution order does. Raw candidate text and deployment secrets are not persisted in recipes.

Letter reviews, tailored resumes, interview plans/answers and HR answers use this recipe instead of individually assembling schema/prompt/route versions. Actual model/cost remain result metadata, not inputs to the cache key. Existing used-fact dependencies are still validated separately by `GeneratedAnswers`: an unrelated fact change does not invalidate every answer. Only the routes actually used by a letter affect its review identity.

Letters, resumes, interview packs and HR builds capture one validated LLM operation snapshot; draft/review/retry calls and cache recipes use that same snapshot even if another process publishes a policy meanwhile. A root client cannot compute route provenance from stale deployment defaults. New operations capture the new revision.

Candidate inputs also come from detached authoritative PostgreSQL snapshots, not shared editable cache instances. An in-flight operation retains its inputs across later writes; unsaved profile/fact edits cannot poison generation. Corrupted current candidate documents fail closed at this boundary. Ordinary legacy readers retain their tolerant cache behavior. DocumentCache freezes saved values before caching them so callers cannot change a committed document through an object alias.

Letter reviews, resume reports and `generated_answers.recipe` (nullable jsonb) persist the recipe with the operation's policy revision. The global revision is audit metadata, not a blanket cache invalidator: a change to an unrelated effective route does not invalidate every artifact. Actual model/cost remain the existing result/row metadata. Legacy rows without a recipe still deserialize.

## Call accounting and operation linkage

Every typed task captures a call recipe from its actual prompt/bindings, user-input hashes, contract, processing rules, effective route and pinned policy revision. A captured operation gets a UUID shared by its drafts/reviews/retries and saved artifact recipes; each logical request gets a separate call UUID and sequential attempted-model numbers. Parked models do not produce fictitious paid attempts. The requested model and provider-returned model are recorded separately.

`llm_calls` stores the operation/call/attempt identifiers and a nullable `recipe` jsonb column. The recipe is frozen before HTTP and detached for each attempt. It must match the captured operation, policy, effective route and contract. A unique call/attempt index prevents duplicate accounting, and an operation/time index supports artifact-level latency/cost analysis. Existing rows without provenance remain readable and contribute to daily spend.

Route probes use the same recording path, including failed paid responses, while retaining their explicit single-model/no-rotation diagnostic behavior. Caller cancellation is recorded without parking the model; when no response arrives, provider usage remains unknown and the recorded zero is a lower bound, not proof of a free call. Accounting ignores caller cancellation and an accounting failure cannot retry an already successful completion. UUIDs belong in traces/rows, not metric labels.

Operation UUID and global policy revision are audit metadata excluded from cache identity. A warm cache hit retains the stored artifact's original provenance; a new invocation must not invalidate equivalent text merely because it has another UUID. Recipes store hashes, never raw candidate text, credentials or request bodies.

Search selectors and filters are owned by the separate typed `policy.search` aggregate; see `docs/SEARCH-POLICY.md`. Durable resumable stages, remaining module/send boundaries and the final comparable performance/release verification are still open. These foundations do not complete the full migration.
