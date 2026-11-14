# Indexed whole-program ownership approval

`bash selfhost/tests/handoff-ownership-index/render.sh` generates the focused
project under `work/handoff-ownership-index`. It extracts the production Core
approval and its overlay/name guards, with a narrow state adapter. The old
approval is frozen at `ce3fbd27`; its only adaptations are its local function name
and access to the fixture's old table. The old linear lookup is extracted from
OwnershipInference. The production index module is imported unchanged.

Compile plain and with `ASHES_RC_POISON=1`, and using stage 1. With
`ASHES_RC_LEAKCHECK=1`, run `check`, `old100`, `old1000`, `new100`, and `new1000`.
Each implementation's fresh-query pair must have equal live counts. `check`
compares lookup answers and exact approval lists for duplicates (including an
empty first answer), qualified names, Unicode, missing/unregistered names, and
mismatched parameter chains. Fresh runs allocate unique keys, parameter names,
lists, the index and a containing state record each iteration.

`old-bench`/`new-bench` perform 10,000 real approval queries against a 2,048-entry
table, including registration and parameter-name guards and the ownership overlay.
The measured lookup is near the table's end; index construction is included once.
Whole-compiler performance and complete Core integration are separate gates.

Validation: stage0 plain, stage0 poisoned and bootstrap21 stage1 all print
`equivalent`. Old and new fresh-query counts are each 2→2 at 100/1000 queries;
all benchmark/check runs also end at 2. Fixture compile times/peak RSS:
2.33s/263276KiB, 2.42s/245844KiB, 3.49s/239264KiB respectively.
The old/new 10,000-query benchmark took 0.11/<0.01s plain, 0.06/<0.01s poisoned,
and 0.06/<0.01s stage1. These focused timings include one index construction,
not a whole-compiler speedup measurement. Core wiring awaits the next complete
compiler build; no full repository suites were run for this change.
