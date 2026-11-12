# Heap copy-plan view regression

Compile this project with stage 0 plain, stage 0 with `ASHES_RC_POISON=1`, and
stage 1. With `ASHES_RC_LEAKCHECK=1`, run `check`, `1000`, and `10000`.
Use the repository's scoped memory budgets and `ulimit -s 1048576` for stage 1.

`check` compares the dedicated copy-plan view against a projection of full
classification over the broad heap-layout fixture: primitives, resource/view and
unresolved types, zero-cost wrappers, generic instantiations, tuples/lists,
recursive ADTs, records, shallow types, and closure fields. It retains the
existing view comparisons as additional classification context.

The two round counts rebuild constructor environments and query results on every
round, including children whose drops are unsupported. Equal live counts check
release of temporary view payloads. The Core planner decisions and settled-cache
authority are independently checked by `core-copy-plan-view`.

Validated with integration stage 0 plain/poisoned and `s1-f-bootstrap17-debug`:
all comparisons pass; fresh 1,000/10,000 rounds are **5/5 live cells** in all
three modes. Fixture compile times were 22.07 s, 22.97 s, and 2:53.85 respectively;
maximum measured RSS was 1,554,120 KiB. These are focused fixture measurements,
not full compiler build timings.
