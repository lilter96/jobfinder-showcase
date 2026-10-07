# Candidate document ownership

`Modules/Candidate/ICandidatePublisher` is the write boundary for `candidate.profile` and `candidate.facts`. Existing profile/facts facades retain cached ordinary reads and detached authoritative snapshots; persisted JSON readers stay tolerant, and `app_documents.content` remains `json`.

Publication requires the expected version captured with the inputs. A transaction-scoped document lock serializes owners, while an EF version concurrency token also catches an older writer that does not take that lock. Current content/version, immutable `candidate_revisions`, optional `candidate.profile.previous` and the notification commit together. Failed or stale publication changes none of them. Notifications contain document/version only, not candidate answers or contact data.

Dashboard profile editing, profile confirmation and the facts editor retain the displayed version. Conflicts leave unsaved text in the form and offer explicit reload; they never silently adopt a fresh version to overwrite a concurrent edit. Single-fact commands reread/reapply an answer only when that fact itself is unchanged, retaining independent confirmed answers and existing in-flight snapshots. A concurrent change to the same answer is a conflict, not a last-retry-wins overwrite.

Resume import captures the old profile/version before its LLM call. Only a successful publication preserves salary/preferences/restrictions, resets Reviewed and backs up the exact preimage. A change made during extraction returns CLI exit code 3 without altering profile/history/backup. CLI output is explicitly UTF-8, including redirected Windows output.

Automatic facts drafting locks profile then facts, reads the current profile inside that transaction, creates one unconfirmed draft and never replaces a saved answer. With no profile it writes nothing. A previously published document that was deleted is not recreated from defaults; restore/reconcile it. The first edit of an existing legacy document records its real prior version and exact content, not a fabricated revision one.

Generic PostgreSQL document Save/Add/Delete refuse the active candidate keys. Legacy file import uses the same Candidate publisher for these keys, retains add-only behavior and leaves unreadable or deleted-history inputs in place. Direct SQL remains an operator escape hatch, not a supported application writer.

The history migration refuses populated rollback under an exclusive lock. Stop writers and export/reconcile history before an intentional downgrade; do not delete live history to force rollback. Shared `app_documents` storage is never dropped by it.

Verification: isolated PostgreSQL covers creation/update conflicts, independent answer races, one-time drafting, preimage/history/outbox rollback, legacy migration and writer races, detached snapshots, invalid answers, generic-write refusal and non-destructive downgrade. Real compiled Razor components exercise both conflict and reload behavior. Actual CLI subprocesses use a loopback synthetic provider to verify successful import and an edit made while extraction is in flight. These tests do not contact production or assert real-model writing quality.

The full five-module migration, common employer gateway, remaining durable preparation and final real-provider benchmarks/release are separate open requirements in `docs/REFACTORING.md`.
