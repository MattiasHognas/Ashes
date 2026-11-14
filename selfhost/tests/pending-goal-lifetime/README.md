# Pending trait-goal string lifetime reproducer

This records an existing stage1 ownership defect exposed while testing deferred logical negation.
It is a diagnostic fixture, not a passing regression gate. Neither source contains the new
`finishOrDeferCoreLogicalNot` helper. Both contain the existing `resolvedTraitGoals` body and
small representations of its lowering state. Helpers are frozen so this reproducer does not
require an old Git object in a fresh or shallow clone.

Render from the repository root:

```sh
bash selfhost/tests/pending-goal-lifetime/render.sh state
bash selfhost/tests/pending-goal-lifetime/render.sh literal
```

Compile each generated `ashes.json` with `ASHES_RC_LEAKCHECK=1`, then run the executable with
`1`, `1000`, and `10000`. Set `ulimit -s 1048576` before using stage1. All compiler and native
invocations must use the configured heavy-job wrapper and systemd scopes: 8G for these small
compiles, 4G for their programs, and `MemorySwapMax=0`.

The counter-equality runner applies these limits and records source/compiler hashes, output,
elapsed time and peak memory under each generated project's `<tag>/` directory:

```sh
bash selfhost/tests/pending-goal-lifetime/validate.sh /absolute/path/to/compiler checkpoint
```

Pass `poison` as the third argument only for stage 0; stage 1 has no poison backend.
`ASHES_HEAVY_RUNNER` can select the configured heavy-job wrapper. The script compares counters
at 1000 and 10000 rounds for both cases and returns nonzero if either grows. The known stage-1
failure is deliberately not accepted as a pass. The runner has been syntax-checked; the results
below came from the equivalent individually scoped commands, not a new invocation of this runner.

The `state` case creates a dynamic string key, stores its unresolved goal in the state, binds
the goal variable to Bool, and reads the pending list through `statePendingTraitGoals` before
calling `resolvedTraitGoals`. It checks that the resulting list has one element. The `literal`
control calls the same unchanged helper with a fresh singleton `(dynamicKey, SemBool)` list.
Both print `direct passed`.

Observed with frozen bootstrap28 stage1 (PR1154 checkpoint c0e23905):

| Case | 1 round | 1,000 rounds | 10,000 rounds |
| --- | ---: | ---: | ---: |
| State-derived pending list | 2 | 1001 | 10001 |
| Literal pending list | 1 | 1 | 1 |

The larger negation-policy fixture is flat at 1/1 under stage0 plain and poisoned compilation,
but that does not establish stage0 results for this separately reduced fixture. The state-case
stage1 compile took 6.05 seconds with 262,684 KiB maximum RSS; consult the captured timing file
if these values are regenerated.

## Attribution evidence

A reduction of the original policy fixture showed seed construction, key construction,
empty-proof-map pinning, deferral alone, and binding alone all flat. Adding the existing
`resolvedTraitGoals` call changed counts to 2/1001; subsequent proof-map construction and
second-pass pinning added no further growth.

In that reduction, a GDB hardware watchpoint followed the surviving key string
`2:3:Not:not` at RC header `0x100000000510`. Its reference count changed
`1 -> 2 -> 3 -> 2 -> 3 -> 4 -> 3 -> 2 -> 1` and never reached zero. The final live-region scan
found exactly this string plus the baseline cell. The retained-reference instruction PCs were
`0x445b6a`, `0x445e93`, `0x44627e`, and `0x44693c`; the last observed drops were at
`0x4495b6` and `0x44983b`. These addresses belong to the original reduced executable, not the
standalone fixtures above, and must be regenerated after recompilation.

Rendering the tracked inputs reproduces the validated source bytes exactly:

- State: `4104056f73fc31f1ada4d9b947dd083acf518e7b4cccbb418f5f896d0409cf85`.
- Literal: `a0708000fcd56aceba081c6bbf97383de2fec3c1fcc3c75dcd0d3850dd34e541`.

Stage1's region allocator has a 16-byte block prefix before the ordinary RC header. Freed
blocks retain their old reference counts; a correct live-cell scan must subtract the allocator's
free lists. The older stage0 `rccells` scanner misidentifies these blocks and is unsuitable here.

The exact unmatched ownership operation still requires IR/source attribution before a fix.
No release or source-level workaround is proposed by this fixture. Local detailed reduction,
GDB scripts, disassembly, and logs are retained in the `step-f-not-lifetime-review` worktree under
`work/logical-not-lifetime/` and `work/logical-not-direct/`.
