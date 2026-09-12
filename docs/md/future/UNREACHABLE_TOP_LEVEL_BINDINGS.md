# Unreachable top-level bindings: investigation and landing blockers

Investigated 2026-09-12 against `200ab9c9`, using a separate worktree from `origin/main`.
**No elision pass or compiler configuration change was landed.** The experiments below were
removed; this report records why the proposed general pre-lowering pass is not ready to ship.

The intended benefit is compile time and, where unused code survives the backend, executable
size. It is not a runtime-heap optimization.

## Findings

1. Current stage 0 already fails the fannkuch memory gate without elision. The regression is
   isolated to call-site element specialization, introduced by
   [PR #971](https://github.com/MattiasHognas/Ashes/pull/971).
2. A reconstruction of the proposed elision sweep does not introduce additional fannkuch memory
   growth on this revision. With element specialization disabled experimentally, elision completes
   N=11 at 8,208 KiB. This does **not** establish the cause of the historical, reverted attempt's
   reported 52–54 GB regression: that incident remains unproven as an elision-induced regression.
   Do not equate it with the newly isolated specialization defect.
3. The proposed sweep has an independent semantic blocker: removing a lambda before binding/type
   checking can suppress diagnostics inside its body.
4. Reporting must not gate the optimization. Disabling it for `--explain` or `--emit-ir` would make
   reporting change generated code, contrary to the [CLI contract](../reference/cli.md#compiler-reports).

## Experimental pass

The temporary reconstruction ran after top-level name/inlining registration and before
`DesugarTopLevel`. It used `FreeVars` and a work queue to compute transitive reachability from the
trailing expression and every non-lambda initializer. A declaration was eligible only when
`RegisterInlinableStrip` exposed a lambda. Recursive groups and their dependencies were roots;
registrations in the inline/specialization tables did not exempt otherwise unreachable lambdas.

An experiment-only environment switch enabled the sweep, independently of report flags, including
for the IR comparisons. It is **not** a supported compiler option and is not present in the repository.

This retained the bare `fold = foldLeft` alias and its dependency, while dropping the unused List
exports in the requested example:

```ash
import Ashes.Collection.List as list
Ashes.IO.print(Ashes.Text.fromInt(list.length([1, 2, 3])))
```

Both executables printed `3`. Final IR lost `append`, `filter`, `map`, `mapGo`, `merge`, `reverse`,
`sortBy`, `splitAlt`, `head`, and `tail`; `foldLeft` remained through the strict alias initializer.

| Measurement | Unmodified lowering | Experimental elision |
|---|---:|---:|
| Final IR function entries, including entry point | 35 | 11 |
| `-O0` executable bytes | 211,784 | 174,920 |
| `-O2` executable bytes | 122,256 | 122,256 |
| Median `-O2` CLI compile wall time, five alternating runs | 0.76 s | 0.69 s |

The five baseline times were 0.75, 0.76, 0.76, 0.74, and 0.76 seconds; experimental times were
0.69, 0.69, 0.69, 0.70, and 0.69 seconds. These include process startup, using a Release-built
CLI directly rather than `dotnet run`. They demonstrate a small-program compile-time reduction
and an `-O0` size reduction, **not** an `-O2` size reduction or a general benchmark result.

## Fannkuch: a pre-existing specialization regression

Compilers were rebuilt at each measured revision; outputs were compiled at `-O2` for linux-x64.
Runtime memory was measured with `/usr/bin/time`, not compiler-process RSS.

| Compiler / experiment | Input | Peak RSS (KiB) | Result |
|---|---:|---:|---|
| `0149f0c2` (PR #920) | N=9 | 8,204 | Correct |
| `0149f0c2` (PR #920) | N=11 | 8,212 | Correct, 29.35 s |
| `4fdd8aa5` (before TMC) | N=9 | 8,204 | Correct |
| `599bb65a` (parent of #971) | N=9 | 8,204 | Correct |
| `200ab9c9`, no elision | N=9 | 253,968 | Correct |
| `200ab9c9`, experimental elision | N=9 | 253,964 | Correct |
| `200ab9c9`, no elision, 1 GiB virtual-memory cap | N=11 | 1,044,492 | Allocation failure, exit 1 after 0.71 s |
| `200ab9c9`, experimental elision, element specialization disabled | N=11 | 8,208 | Correct, 26.44 s |

The successful N=11 runs produced checksum `556355` and `Pfannkuchen(11) = 51`.
The capped failure is **not** an uncapped N=11 peak measurement. No attempt was made to reproduce
a tens-of-gigabytes allocation on the host. N=9 also grew without elision at `-O0` (253,972 KiB),
and with reporting absent at `-O2` (253,964 KiB), excluding LLVM optimization level and reporting
as explanations for this baseline failure.

### Current leak mechanism

The changed call is `setAt(r)(cr)(count)` inside fannkuch's `nextPerm` (also used by `resetCounts`).
On current main it routes to `lambda_85__element`, whose innermost helper is `lambda_106` in this
specific dump; generated labels are not stable identities.

The final IR shows the following retain/release mismatch:

1. The caller retains the named, runtime-managed `count` with `RcDup` before the specialized call.
2. The specialized `setAt` does not adopt/normalize its input parameter. It constructs its result
   independently: TMC allocates the prefix as RC cells, and the terminating `v :: t` branch copies
   its result with `CopyOutList RuntimeManaged=true Purpose=RcNormalization`. Its return path can
   pass that owned result directly, without retaining the input list root or releasing the caller's
   extra reference.
3. The caller recognizes the result as runtime-managed, so it does not perform the former
   caller-side result copy. `LowerCallFinish` then leaves `resultCopySeversArgumentReferences`
   false. `LowerCallDropConsumedRuntimeArguments` skips the handed-over argument's release on that
   path, assuming the reference may remain in the result.
4. The retained input root actually remains unaccounted for. The later release of its original
   owner sees a shared root and decrements it, leaving the old list alive.

This is not evidence that an unreachable function must remain lowered for a fixed point to work.
It is a mismatch between a concrete specialized producer's independent RC result and the caller's
conservative hand-over/release protocol. Relevant implementation:
`Lowering.ElementSpecialization.cs` (`TryRouteToElementSpecialization`), and `Lowering.cs`
(`LowerCallFinish`, `PrepareRuntimeManagedCallArgument`, `LowerCallDropConsumedRuntimeArguments`).
Do not fix it by unconditionally dropping handed-over references: other callees genuinely adopt
them or preserve them in their results.

### Standalone reproduction

Save this as `setat-churn.ash` and compile it with a Release-built stage-0 CLI at `-O2`:

```ash
let recursive setAt i v xs =
    match xs with
        | [] -> []
        | h :: t ->
            if i == 0
            then v :: t
            else h :: setAt(i - 1)(v)(t)

let recursive run rounds xs =
    if rounds == 0
    then
        match xs with
            | [] -> 0
            | h :: _ -> h
    else run(rounds - 1)(setAt(rounds % 3)(rounds)(xs))

match Ashes.IO.args with
    | arg :: _ ->
        match Ashes.Text.parseInt(arg) with
            | Ok(rounds) -> Ashes.IO.print(Ashes.Text.fromInt(run(rounds)([1, 2, 3])))
            | Error(_) -> Ashes.IO.print("bad argument")
    | [] -> Ashes.IO.print("missing argument")
```

At 200,000 rounds this used 24,588 KiB; at 2,000,000 it used 192,524 KiB. Both printed `3`.
Changing only the experimental compiler configuration to `EnableElementSpecialization = false`
made the 2,000,000-round run use 8,208 KiB, also printing `3`.

For bounded reproduction, cap **the emitted program**, not the .NET compiler:

```bash
(ulimit -v 1048576; /usr/bin/time -f 'elapsed=%e rss_kb=%M exit=%x' ./setat-churn 2000000)
```

## Diagnostic preservation is a separate prerequisite

```ash
let unused x = missing(x)
0
```

Normal compilation rejects this with `ASH001 Undefined variable 'missing'`. The reconstructed
pre-lowering sweep removed `unused`, accepted the program, and emitted an executable. A lambda's
construction being effect-free is therefore insufficient to justify skipping its semantic checking.
Forward-reference, type, trait, capability, and other diagnostics need preservation too; do not
use successful output on well-typed programs as the entire correctness criterion.

A future design must separate semantic validation/analysis from selective emission, or prune
already-validated IR while preserving strict initializers and their dependencies. The latter avoids
skipping lowering analyses but has a different compile-time ceiling; it still needs a sound account
of closure creation, captures, and cleanup in the entry point. Neither alternative was implemented
or validated in this investigation.

## Landing gates

- Restore and test the current specialization/call-ownership contract first, including the
  standalone churn case and actual fannkuch N=11 near its 8.2 MB floor. Carry that contract into
  the self-hosted OPT-60 port rather than mirroring #971's leak.
- Preserve diagnostics in unused declarations, strict non-lambda initialization, aliases,
  recursive groups, and transitive captures. Preserve inspection-oriented lowering APIs without
  making CLI report flags select a different executable.
- Require paired elision-on/off ownership and memory measurements. The historical 52–54 GB
  claim still needs its original revision/patch reconstructed to attribute it conclusively.
- Run the complete compiler/LSP/end-to-end/format gate on any implementation candidate, plus
  explicit N=11 RSS and binary-size/compile-time measurements. No implementation candidate
  passed that landing gate here; the temporary experiments were removed instead of shipped.
