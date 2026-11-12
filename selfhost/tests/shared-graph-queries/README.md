# Shared type-graph classification oracle

This focused project compares the indexed classifier against the implementation
at `54535403`. `LegacyClassification.ash` preserves that implementation with
prefixed private symbols. Its public fact types are imported from the candidate
so comparisons use exact typed values; no candidate classification functions
are called by the legacy implementation. `IndexedExpectedTests.ash` additionally
runs the existing heap-layout expected-value suite with the full classifier
replaced by its indexed entry point.

`check` compares cold and warm full facts, all narrow views, and blocked-path
owned-field/tuple gates on generic, nonregular, resource, missing-constructor,
identity, alias and environment-change cases. `expected` runs the expected-value
suite. `full-1000` and `full-10000` create fresh named types and environments,
exercise all views in sequence while retaining the index, and compare every
answer to the frozen oracle. `new-1000` and `new-10000` cover repeated fresh
resource/unresolved queries; corresponding `old-` modes use the frozen code.

Compile and execute through the session's memory scopes and heavy-job limiter.
Use `ASHES_RC_LEAKCHECK=1`; compile a second binary with `ASHES_RC_POISON=1`.
These checks require the generic-loop and TMC-constructor ownership prerequisites
from `5bcfa2dc`; the old compiler could leak or release the query path while it
was still held by a visited map. The test does not work around those defects.

Acceptance: plain and poison expected/oracle checks pass. Fresh full-view queries
remain live6 at both counts; resource/unresolved queries remain live1 at both.
One-shot expected and oracle runs leave live4 and live9 respectively; these are
not asserted to be zero. This is focused stage0 validation, not a compiler census
or proof that the old stage1 compiler produces leak-free versions of this code.
The next rebuilt stage1 and full stage2 build remain required integration checks.
