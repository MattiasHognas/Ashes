# Recursive discovery replay count

`perl selfhost/tests/recursive-replay/render.pl` extracts the production
`lowerRecursiveMembersPinningSiblingResults` helper unchanged. A reporting
member-lowering callback models goal discovery and sibling-result settlement;
this test checks how often the helper invokes it, and the retained pinned flag.
It does not replace the actual-Core inference and expression-boundary tests.

Compile `work/recursive-replay/ashes.json` with the stage-0 CLI at `--debug -O0`
in the agreed heavy-job wrapper and 8G systemd scope, run the binary in a 4G
scope, then compare stdout exactly with `work/recursive-replay/expected.txt`.
Use `MemorySwapMax=0` for both scopes. Format the generated `Main.ash` before
compilation. Repeat with compile-time `ASHES_RC_POISON=1`.

The expected callback counts are:

| Caller / discovery | Member passes |
| --- | ---: |
| Pinned, empty pending goals, no settled siblings | 1 |
| Unpinned, empty pending goals | 1 |
| Pinned, new unresolved goal | 2, final pass rejects it |
| Unpinned, new goal | 1, retains discovery |
| Pinned, pre-existing pending goal | 2, conservative replay |
| Pinned or unpinned, settled sibling result | 2 |

Every retained result restores the caller's pinned flag. The pinned new-goal
case must reject the unresolved evidence rather than retain the provisional
value. Public expression entry-point tests in `CoreLogicalNotInferenceTests`
exercise that boundary with the real compiler state and real IR emission.

Validated with the round3 stage-0 CLI: plain and poisoned builds both produced
exact expected stdout (exit 0). Builds took 0.69s and 0.68s, respectively, with
peak RSS below 186 MiB. Native checks took less than 0.01s with 8 MiB peak RSS.
A control generated from the preceding committed helper produced exactly one
extra `member` line in the pinned-empty case; all other callback counts matched.
Local logs and the control are under `work/recursive-replay/` and
`work/recursive-replay-before/`.
