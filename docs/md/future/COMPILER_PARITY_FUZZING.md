# Compiler Parity Fuzzing

Status: proposed; implementation has not started as part of this document.

Baseline: static inspection of `MattiasHognas/Ashes` at commit `0a66bb54cb7b9720fc4b8fdf61980f4281b9d5e9`,
2026-09-08. Recheck the current branch before implementation. No compiler builds or campaigns were
executed for this proposal.

## Problem and intended outcome

Ashes has two compiler implementations: the C# stage-0 compiler and the developing self-hosted
compiler. A defect can survive optimization-level differential testing when every tested
configuration shares the faulty lowering. Comparing the two implementations provides a different
source of disagreement.

Extend the existing C# fuzzing application to compile the same generated program with both compilers
and compare its observable behavior. Begin with a verified common language/runtime subset on native
Linux x64. Produce reproducible artifacts that identify the exact compiler pair, preserve the failure
while shrinking, and distinguish unsupported features from regressions.

The C# implementation is a behavioral reference, but a disagreement does not prove the self-hosted
implementation is wrong. Resolve disagreements against the language specification and a minimized
example.

## Research motivation

[*Fuzzing the Gleam Compiler*](https://www.kurz.net/posts/fuzzing-gleam-compiler), published
2026-08-25, describes generation of typed programs and differential execution through Gleam's Erlang
and JavaScript backends. The sections "The echo Problem" and "Duplicate Findings And Blocking Issues"
are especially relevant: output representation and overly broad failure suppression can undermine an
otherwise useful oracle.

This proposal adapts that experience to two Ashes compiler implementations. It does not reproduce
Gleam's harness or assume that its target-specific behavior applies to Ashes.

## Existing implementation and concrete gaps

Paths in this table are repository-relative. [Browse the inspected snapshot](https://github.com/MattiasHognas/Ashes/tree/0a66bb54cb7b9720fc4b8fdf61980f4281b9d5e9).

| Existing component | Observed behavior | Proposed extension |
|---|---|---|
| `src/Ashes.Fuzzing/Execution/CompilerExecution.cs` | `CompileAndRunAsync` invokes the .NET CLI and runs a result only when the target matches the host | Select a compiler adapter; retain bounded process execution |
| `src/Ashes.Fuzzing/Oracles/ExecutionOracles.cs` | Optimization, reuse, trait-evidence, cross-target and memory-growth oracles exist | Add an implementation-parity oracle |
| `src/Ashes.Fuzzing/Oracles/FuzzOracle.cs` | `FuzzExecutionContext` holds one `CompilerExecution`; default registry has no implementation-parity oracle | Carry a configured compiler pair and register the new oracle |
| `src/Ashes.Fuzzing/Execution/ObservableValueRenderer.cs` | Generates source that renders supported result types | Reuse it after proving the selected subset and its renderer work on both compilers |
| `src/Ashes.Fuzzing/Execution/FuzzCampaign.cs` | Constructs the execution context; shrinking accepts any failure from the same oracle | Preserve the original failure class and compiler configuration during shrinking |
| `FuzzCampaign.RunCorpusAsync` | Runs parse, format, semantic and IR oracles | Replay parity regressions through their saved native comparison configuration |
| `src/Ashes.Fuzzing/Persistence/FuzzFailureReport.cs` | Maps existing oracle IDs to human-readable compiler configurations | Record actual compiler identities, flags and artifact metadata |
| `selfhost/parity/semantics/` | Contains existing parity fixtures | Keep those fixtures; add generated behavioral coverage alongside them |

Generation, combination templates, seeds, shrinking and artifact writing already exist. This feature
extends them. The self-hosted runner itself does not need to be ported to deliver the first version.

## Design

### Compiler adapters

Introduce a small execution abstraction in `Ashes.Fuzzing`, with an existing-behavior .NET adapter and
an explicitly configured self-hosted executable adapter. Names such as `ICompilerAdapter` and
`CompilerParityOracle` are proposed names, not current APIs.

Each adapter should expose its identity, supported invocation options and compilation operation. A
compilation result must retain the command arguments, exit code, timeout/output-limit state,
stdout/stderr and emitted executable path. Compiler identity should include a binary hash and source
commit when known; do not fabricate a commit when only a binary is available.

Keep process isolation, timeouts, output bounds, cancellation and temporary-directory cleanup. Pass
arguments as structured argument lists. Avoid shell interpolation and do not assume both CLIs support
the same flags. In particular, do not pass `-O2`, `--target` or reuse-debug flags to the self-hosted
CLI without verifying support.

### Common feature profile

Define a named, versioned generation profile based on execution probes and current source inspection.
A conservative initial candidate is integers, booleans, deterministic arithmetic with explicitly safe
operands, lexical bindings and supported pure function calls. Admit branches, records, lists, ADTs,
closures and bounded recursion incrementally.

Verify both the generated expressions and the observation code: rendering lists or ADTs can itself
require recursion, constructors, strings and library functions. Unknown or unsupported capability must
exclude a rule before generation. A compilation failure for an admitted feature is a test finding, not
an automatic skip.

Do not begin with external resources, networking, timing, nondeterministic effects, parallel
scheduling or async behavior. Add them only with a defined observation contract and demonstrated
runtime support.

### Comparison contract

Compile exactly the same saved source against equivalent standard-library inputs. Run both outputs
under the same target, inputs, working-directory policy and deterministic environment. Compare
structured outcomes rather than a single success boolean.

| Outcome | Treatment |
|---|---|
| Successful executions with equal canonical values and exit status | Pass |
| Different values or exit status | Behavioral mismatch |
| Crash or rejection of an admitted valid program | Compiler/runtime failure, preserving the failing side |
| Timeout or output limit | Separate inconclusive/resource-limit outcome, retained for investigation |
| Missing compiler, runtime dependency or unsupported host | Setup failure before the campaign |
| Feature excluded by the common profile | Not generated; report the exclusion in profile coverage |

For pure successful cases, unexpected runtime stderr should remain visible. Do not compare compiler
progress/timing text as program semantics. Start with exact deterministic representations. Any later
floating-point policy must follow Ashes's semantics; an arbitrary tolerance can hide a real
miscompilation.

### Replay, shrinking and findings

Persist original and minimized source, source hashes, result type, profile version, seed/case index,
compiler identities, standard-library identity, invocation options, timeout limits and separate
outputs for both sides. Persist the observation wrapper as part of the exact reproducer.

`ReportFailureAsync` must require the candidate to preserve the relevant failure signature. A value
mismatch must not shrink into a compiler crash, unsupported feature or timeout. Recheck common-profile
admissibility after each shrink. Report limits reached during shrinking without converting the
original finding into a pass.

Replay should support saved source directly; seed replay alone depends on generator stability. Extend
corpus handling so native parity regressions actually execute the parity oracle. Do not assume the
current frontend/IR corpus checks reproduce them.

Initially keep distinct minimized reproducers. Optional grouping may use oracle, failing side and
normalized diagnostics, but broad substring filters must not silently discard cases.

## Implementation sequence

- [ ] Reaudit current compiler options, self-hosted executable requirements and existing parity
  tooling; record the tested common subset.
- [ ] Extract the .NET invocation behind an adapter without changing existing oracle behavior.
- [ ] Add explicit self-hosted compiler selection and fail-fast setup checks.
- [ ] Add the parity oracle and a small deterministic profile to the existing registries.
- [ ] Extend outcome classification, artifact metadata and exact-source replay.
- [ ] Make shrinking preserve failure class and profile admissibility.
- [ ] Add native parity corpus replay and framework tests.
- [ ] Add a proposed `just fuzz-parity` recipe and document it in the fuzz-testing guide. Do not make
  the default fast gate depend on an unavailable self-hosted build.
- [ ] After reliable provisioning, add a bounded fixed-seed CI campaign and a separate longer
  campaign.

## Validation and acceptance criteria

Use adapter fixtures that deliberately disagree to verify mismatch detection, not only examples where
both implementations succeed. Check setup failure, timeout, crash, unexpected stderr and output
truncation classification. Ensure shrinking preserves an injected value mismatch and does not accept
an injected crash instead.

Run an initial fixed set of 100 admitted native cases through both real compilers, retaining counts of
generated, compared, failed and inconclusive cases. This number is an initial reproducibility target,
not a correctness guarantee. Replaying a saved finding must invoke the same compiler pair and
reproduce the same failure class, or explicitly report identity/configuration drift.

Existing optimization/reuse oracles must keep working. A parity campaign must never report success
with zero comparisons because every case was skipped. Follow the current repository validation gates
when implementing the change.

## First useful task

Refactor `CompilerExecution` behind an adapter and run ten fixed pure scalar programs through the two
real compilers. Resolve invocation and output-contract differences before expanding generation.
Porting missing language features is a separate task; record those prerequisites without weakening the
test oracle.
