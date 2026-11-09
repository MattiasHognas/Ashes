# 32_3 census class: cause notes (W1, round 11, 2026-11-09)

Worktree: /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-post-guards
Branch: leak/round11-32-3 (based on 1bc2a5d7, no commits; all work below is untracked files under work/).

## What 32_3 is

A 32-byte RC cell (count 1, size word 0x20) whose payload is two scalars, 3 and 2: the tuple `(3, 2)`.
Every growing instance is the head of a 32-byte cons cell that no live cell points at, so the
growth is whole orphaned association lists of `(IrTemp, IrTemp)` pairs. The census shows 32_3 at
+2 per function (s1-cr1a: 21 such cells compiling loops10, 23 compiling loops11; every other 32_3
cell is an unchanged 3-byte string). Most of the 32_ptr +5/fn is likely the cons cells of the same lists.

## Where it is allocated

`selfhost/packages/semantics/src/AshesCompiler/Semantics/IrOptimizer.ash`,
`elideTrivialOwnershipCopies`:

- line 307: `remap |> setAssociation(dest)(resolvedSrc) |> buildRemap(tail)` allocates the entry.
- line 313: `let remap = buildRemap(instructions)([])`.
- `applyRemap` (lines 315-335) reads `remap` through its closure environment.

The list `buildRemap` returns is never released after `applyRemap` reads it. A watchpoint on the
leaked cons cell shows count 1 from creation at line 307, then balanced retain/release pairs at
line 311 (the pass-through back edges), and no release at the function's exit.

## dl vs dl2

- dl (stage 1's lowering only, compiled by stage 0, debug): no 32_3 growth.
- dl2 (dl plus `optimizeIrProgram`): 32_3 +2/fn, 32_ptr +5, NET 17, GROWTH 28. This is nearly the
  real stage-1 tally, so the leak is in IrOptimizer, and it is stage 0's code generation of that
  file (dl2 is compiled by stage 0).

Tools (copied into this worktree):
- `work/q41/dl2/` is the dl2 project (`Main.ash` adds `import AshesCompiler.Semantics.IrOptimizer`
  and formats `optimizeIrProgram(lowered)`).
- `work/q41/dlrun2.sh <tag>` builds dl2 with `--debug` and full-tallies loops10/11. Run it under
  `systemd-run --user --scope -q -p MemoryMax=16G -p MemorySwapMax=0`, gated by
  `work/q41/waitheavy.sh`.
- `work/q41/w.gdb` and `w2.gdb` are the watchpoint scripts. The run line must match dlrun's exactly
  (absolute paths, same redirect) or the heap layout differs.
- `work/w9c/d3/cw` prints the words of the cells at the given addresses.

## Stage 0 IR (dl2, `--emit-ir final`, `work/q41/dl2/ir-final.txt`, function lambda_19069)

Stage 0 does NOT fuse `buildRemap(instructions)([])` into one direct call here. It emits
`CallKnown lambda_19070` (the first stage, which returns a closure; heap env of 6 captures), then
`CallClosure` with `[]`. The CallClosure's list result goes to the let slot of `remap` and is never
dropped. The exit drops only the `applyRemap` result and the stage closure.

When stage 0 does fuse the call (a single-file or two-file copy of the function: one CallKnown into
the second stage with a stack environment), the exit releases `remap` and nothing leaks.
`work/w9c/pj` is such a project, and it stayed flat until I split the call.

Open question: why the real call is not fused. Possibly the capture set: in the real code the env
holds module private values, `__ashes_private_value_...`.

## Reproducer

`work/w9c/p4.tpl` (substitute ROUNDS) is `p3.tpl` with the call split into
`let step = buildRemap(instructions) in let remap = step([])`. That forces the same
CallKnown + CallClosure shape:

- stage 0: 5 -> 9 live at rounds 1/2 (+4/round), plain and ASHES_RC_POISON=1 (`work/w9c/lk.sh`)
- stage 1 (s1-cr1a): 5 -> 9 (`work/w7/s1lc.sh`)
- unsplit `p3.tpl`: flat 1 in stage 0

A minimal plain version (`p5.tpl`: a one-capture `build` closure split the same way) stays flat,
so more of p4's shape matters. Not reduced further. p4's stage-0 IR is in `work/w9c/p4.ir`: slot 24
(`remap`, the CallClosure result) is never dropped.

## Suspected rule

A CallClosure whose result is a general-RC value (here `List((Int, Int))`) through a closure that is
a partial application of a known function (the stage chain of `buildRemap`) takes no ownership of
the result. It reads no returns bit and has no arena request (`RequestsArenaResultFromUnknownCallee`
only covers TNamedType results). The callee hands back an RC list (its parameter, or
setAssociation's fresh result), and nobody releases it.

Likely fix directions:
- (a) Resolve such a CallClosure to the chain's known label with the full argument count
  (TryResolveKnownFunctionLabel through the let-bound partial application, or through the
  unfused direct form) and use that label's compiled result fact, as the fused CallKnown does.
- (b) Extend the unknown-callee arena request to general-RC list results.

Check whatever is changed against the GRC_NO_CURRIEDCHAINRESULT machinery
(Lowering.GeneralRc.cs ~530), which already walks curried chains for loop owned slots.

Switch sweep of p4 over the call/closure/result/owned/let switches: see `work/w9c/sw-p4s.log`
(result appended below when done).

### Sweep result (73 switches matching CALL|CURRI|CLOSURE|RESULT|OWNED|LET|ESCAP|CAPTURE|PARTIAL|STAGE)
No switch removes the leak (baseline 5 -> 9). Only GRC_NO_CALL changes it, making it worse (19 -> 37).
So no existing rule covers this shape; it needs a new rule (direction (a) or (b) above).
