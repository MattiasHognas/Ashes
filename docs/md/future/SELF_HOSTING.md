# Self-Hosting: Building the Ashes Compiler in Ashes

Status as of 2026-08-15. This document contains both the capability audit of what Ashes-the-language,
its compiler/runtime, and its standard library must provide before a compiler can be written in Ashes,
and the implementation handoff for the active self-hosted toolchain migration. See
[FUTURE_FEATURES.md](FUTURE_FEATURES.md) for how self-hosting fits the broader roadmap and the
[self-hosted toolchain README](https://github.com/MattiasHognas/Ashes/blob/main/selfhost/README.md)
for package boundaries and commands.

## Migration state

The new implementation lives entirely under `selfhost/`. It is pure Ashes: Python, shell, C#, and
Node.js helpers are not part of its implementation or test path. The existing C# compiler and Node.js
VS Code extension remain the bootstrap oracle and must not be removed or changed merely to make the
self-hosted port easier.

| Area | Ported surface | State |
|---|---|---|
| Frontend | Tokens, UTF-8 source spans, lexer, typed syntax model, leading import-header separation, expressions, patterns, types, and whole-program parsing for all current declaration forms | Implemented and covered by pure-Ashes tests |
| Formatter | Canonical formatting for complete programs, declarations, expressions, patterns, and types, including precedence and idempotence coverage | Implemented and covered by pure-Ashes tests |
| Semantics foundations | Stable symbols/scopes, semantic types, substitution, unordered open-row unification, constrained schemes, and source type resolution | Implemented and covered by pure-Ashes tests |
| Expression/program inference | Core and structural expressions, operators, records, guarded matches, Result pipelines, `let?`, annotations, constructors, recursive groups, aliases, zero-cost types, and sequential top-level inference | Implemented for the listed surface |
| Capabilities | Declaration and operation schemes, effect propagation, handlers and `resume`, provider registration, exact concrete provider satisfaction, abstract requirement preservation, and provider/handler ambiguity rejection | Implemented for inference; lowering and code generation remain |
| Traits | Operator constraints; trait declaration/method registration; forward supertrait validation; cycle rejection; qualified method schemes; default-body type checking; ordinary implementation registration with rigid heads, requirements, optional defaults, and type-checked supplied methods; deterministic duplicate/structural-overlap rejection; package orphan ownership for traits and nominal head types; decreasing conditional requirements; selected-default dependency validation; canonical constraints with transitive supertrait elimination; written binding-requirement boundary validation; recursive concrete instance evidence resolution; canonical failure traces; deterministic hidden-dictionary ABI shape planning; ABI-ordered call-site evidence argument planning; constrained-function application/partial-capture planning; active evidence forwarding with deterministic supertrait paths; active trait-method slot planning; concrete dictionary-construction input planning with supplied/default method selection; and dependency-aware selected-method construction order | Declaration, ordinary implementation, coherence, termination, default-cycle, constraint-canonicalization, written `requires` validation, evidence-plan resolution, structured resolution failures, dictionary ABI layouts, call-site evidence arguments, constrained-function application plans, recursive/sibling evidence-forwarding plans, active method-access plans, concrete construction inputs, and selected-method build order implemented; value rewriting and lowering remain |
| IR, optimizer, ownership, backend, linker | No self-hosted implementation yet | Not started |
| CLI, LSP, DAP, TestRunner, fuzzing runner, registry commands | Package boundaries are defined, but the tools are not ported | Not started |
| Bootstrap | No stage-1/stage-2 compiler build or equivalence comparison yet | Not started |

The current packages intentionally form the same strict dependency graph as the existing toolchain:
`frontend` has no compiler dependency, `formatter` depends only on `frontend`, and `semantics` depends
only on `frontend`. Do not move backend behavior into those packages. Future packages must follow the
dependency table in the
[self-hosted README](https://github.com/MattiasHognas/Ashes/blob/main/selfhost/README.md#package-dependency-graph)
and
must reference only the packages they actually consume.

### Planned work

Work should continue in dependency order. Each item below is a reviewable milestone or a short series
of milestones; split an item when its tests and public contract can stand alone.

1. **Complete trait semantics.** Thread evidence through every value shape and lower dictionaries and
   method dispatch. Add `deriving` expansion only after ordinary evidence works end to end.
2. **Close whole-program semantic gaps.** Port import/module and export resolution, external declaration
   typing and ABI metadata, package/project stitching, remaining declaration namespace rules, exhaustive
   diagnostics, and any expression/type-inference behavior not yet represented by the focused tests.
   Compare observable results with the C# compiler rather than copying its internal object graph.
3. **Define and lower the complete IR.** Port the current IR model and text form, expression and
   declaration lowering, trait/capability evidence, async state machines, optimization passes, ownership
   and move analysis, Perceus lifetime placement, reuse, and compiler explanation/tooling metadata. Keep
   ownership inferred and retain the current no-GC memory contract.
4. **Port native code generation and linking.** Implement LLVM emission, target ABI handling, runtime
   and bitcode selection, ELF/PE construction, external libraries/resources, debug information, and all
   four target RIDs. Start with the host target but preserve target-independent APIs from the outset.
5. **Port the toolchain consumers.** Build the Ashes CLI orchestration and registry commands, then the
   TestRunner, LSP, DAP, and deterministic fuzzing/fuzzyrunner packages. LSP and DAP remain consumers of
   compiler packages; they must not duplicate parsing, inference, lowering, or runtime behavior.
6. **Bootstrap and prove parity.** Produce a stage-1 compiler with the existing compiler, use stage 1 to
   build stage 2, compare deterministic compiler artifacts or normalized observable output, and run the
   same source/test corpus through both implementations. Add reproducible bootstrap and release jobs
   only after host-target parity is stable, then extend execution/structural validation to every RID.

### Toolchain implementation checklist

This is the authoritative feature inventory for the self-hosted toolchain. It tracks observable
compiler and tool behavior, not the presence of similarly named data types. The status markers are:

- `[x]` — implemented in pure Ashes and covered by an executable pure-Ashes test;
- `[~]` — a useful part is implemented, but the current compiler's complete observable contract is
  not yet covered;
- `[ ]` — not implemented in the self-hosted toolchain.

The checklist covers the currently shipped toolchain. Features explicitly listed as unsupported or
future work in the language reference are not self-hosting requirements until they become part of the
shipped language. The existing C#, .NET, and Node.js implementations remain the compatibility oracle;
their internal class boundaries are not requirements when a smaller pure-Ashes design preserves the
same public behavior.

#### Package and test foundations

- [x] Define pure-Ashes `frontend`, `formatter`, and `semantics` packages with the required dependency
  direction and no host-language implementation helpers.
- [x] Keep package tests in separate `devDependency` projects and compile them into standalone native
  executables.
- [x] Exercise frontend, formatter, and semantics tests with reuse enabled and with
  `--debug-disable-reuse`.
- [x] Supply the language, standard-library, host-environment, filesystem, process, byte-buffer, JSON,
  regex, and installed-layout capabilities identified by the prerequisite audit below.
- [ ] Add cross-implementation parity fixtures as each self-hosted phase gains a stable serialized
  public result: tokens, syntax, formatted source, diagnostics, inferred schemes, IR, and executables.
- [ ] Make every self-hosted package buildable from a restored source-only dependency graph without
  undeclared checkout-relative inputs.

#### Frontend and source model

- [x] Model tokens, structured diagnostics, and canonical UTF-8 byte spans.
- [x] Lex identifiers, keywords, operators, comments, whitespace, strings, runes, signed and unsigned
  integers, floats, and malformed input, including Unicode scalar validation.
- [x] Model the complete typed syntax surface for programs, declarations, expressions, patterns, and
  type expressions without collapsing those categories.
- [x] Parse literals, variables, qualified references, calls, tuples, lists, records, record updates,
  unary/binary operators, pipes, and their precedence and associativity.
- [x] Parse lambdas, conditionals, nested and recursive bindings, `let?`, `let!`, matches, guards,
  handlers, `perform`, and every current pattern form.
- [x] Parse named, applied, tuple, function, pointer, capability-row, annotated, and constrained type
  syntax.
- [x] Parse exports, aliases, algebraic/record/zero-cost types, flat top-level bindings, recursive
  groups, and optional trailing expressions after the project layer has separated any import header.
- [x] Parse external functions and types, ownership annotations, resources and destructors, native
  strings, pointers, buffers, out parameters, capability rows, and `symbol@library` aliases.
- [x] Parse capability declarations, static providers, trait declarations, implementations,
  supertraits, requirements, defaults, and deriving clauses.
- [x] Preserve source-ordered recovery diagnostics and the current declaration-boundary behavior for
  incomplete editor input and project-stitched programs.
- [ ] Compare the complete frontend diagnostic corpus with the C# frontend by diagnostic code, span,
  ordering, and recovery result rather than only by accepted syntax.

#### Formatter

- [x] Canonically format every expression, pattern, and type form while preserving precedence.
- [x] Canonically format complete programs and every top-level declaration form.
- [x] Preserve intentional source spellings where the formatter contract requires them and sort only
  semantically unordered surfaces such as requirement sets.
- [x] Cover golden output, parse-format-parse behavior, and formatter idempotence.
- [ ] Preserve written import headers and leading/standalone comments around the formatted AST body.
- [ ] Apply formatter options for indentation/newlines and preserve the current pipeline-layout choice
  made from the source form.
- [ ] Compare the full formatter corpus and malformed-input behavior with the C# formatter.

#### Semantic foundations and ordinary inference

- [x] Model stable symbols, qualified identities, immutable lexical scopes, and deterministic fresh
  type variables.
- [x] Model semantic primitive, unsigned, function, tuple, list, pointer, named, capability, and open-row
  types.
- [x] Compute free variables, substitutions, occurs checks, structural unification, generalization,
  instantiation, and constrained rank-1 schemes.
- [x] Unify capability rows independently of declaration order and allow open tails to absorb unmatched
  capabilities.
- [x] Resolve source primitives, parameters, functions, tuples, pointers, aliases, nominal types,
  zero-cost types, and capability rows.
- [x] Infer literals, variables, lambdas, calls, tuples, lists, conditionals, ordinary lets, recursive
  lets, and annotations with let-polymorphism.
- [x] Infer all operator families while retaining their trait constraints.
- [x] Infer matches, guards, literal/list/tuple/constructor/record/as/or patterns, and consistent
  pattern-local bindings.
- [x] Register algebraic, record, alias, and zero-cost declarations; infer constructors, constructor
  patterns, record literals, and record updates.
- [x] Infer `Result` map/flat-map/error-map pipelines and `let?` propagation.
- [x] Infer sequential top-level bindings, shared-monomorphic recursive groups, and an optional trailing
  expression.
- [ ] Infer async bodies, `await`, `let!`, task result/error propagation, and structured task APIs.
- [ ] Perform complete match exhaustiveness, redundancy, large-ADT hardening, and source-compatible
  diagnostic reporting.
- [ ] Enforce resource move/borrow/consume rules, deterministic cleanup constraints, and use-after-move
  diagnostics at the semantic boundary.
- [ ] Seed and resolve the complete shipped builtin and standard-library type environment rather than
  only definitions introduced by focused tests.
- [x] Validate every written binding `requires` clause against the inferred canonical external
  requirement set, including recursive groups and ambiguity checks.
- [ ] Port the remaining declaration namespace, duplicate-name, shadowing, annotation, and inference
  diagnostics with stable codes and source spans.

#### Capabilities and handlers

- [x] Register capability declarations and parameter-sharing operation schemes.
- [x] Propagate ambient effects through implicit/explicit operations, lambdas, ordinary and
  higher-order calls, partial application, and `Result` pipelines.
- [x] Infer handler operation arms, shared instances, `resume`, return arms, arm effects, and residual
  row discharge.
- [x] Register complete, coherent, instance-specialized static providers and type-check their operation
  implementations.
- [x] Satisfy exact concrete capability requirements from providers while retaining abstract
  requirements and rejecting provider/handler ambiguity.
- [ ] Lower dynamic handler evidence, one-shot continuation state, pre/post handler control flow, and
  dynamically scoped handler globals into IR.
- [ ] Lower static-provider dictionaries and generic capability evidence into IR.
- [ ] Validate capability explanations and observable behavior against normal, optimization-disabled,
  and reuse-disabled C# compilation.

#### Traits, implementations, and evidence

- [x] Infer operator constraints and retain them in generalized schemes.
- [x] Register trait declarations, qualified method schemes, forward supertraits, acyclic supertrait
  graphs, and type-checked default bodies.
- [x] Register ordinary `implement` declarations with resolved rigid heads, requirements, supplied
  methods, and inherited defaults.
- [x] Validate implementation trait/arity, method uniqueness and completeness, substituted method
  signatures, capability rows, and requirement variables.
- [x] Reject exact duplicate and structurally overlapping implementation heads independently of source
  or traversal order.
- [x] Track package provenance for traits and nominal head types and enforce the orphan ownership rule.
- [x] Validate decreasing conditional requirements for generic implementation heads while allowing
  fixed requirements on fully concrete heads.
- [x] Reject dependency cycles among the defaults selected by an implementation while allowing a
  supplied method override to break the cycle.
- [x] Canonicalize constraints, remove exact duplicates, and remove supertraits implied by stronger
  constraints.
- [x] Validate written binding `requires` clauses against inferred canonical constraints, including
  nested lets, recursive groups, invalid trait heads, and ambiguous requirement variables.
- [x] Resolve unique concrete instances recursively with cycle/depth guards while preserving abstract
  constraints as hidden dictionary parameters.
- [x] Diagnose missing, ambiguous, incoherent, non-terminating, and ambiguous-type-variable goals with
  canonical requirement traces.
- [~] Plan hidden trait dictionary parameters, method fields, specialized direct-supertrait fields,
  concrete-or-parameter call-site evidence arguments in deterministic ABI order, and ordinary
  arity/remaining-argument evidence capture for constrained partial applications. Plan exact and
  inherited active-dictionary forwarding across recursive and sibling call edges, including the
  selected method slot after following a supertrait path. Plan ABI-ordered supplied/default method
  fields, dependency-aware selected-method build order, and conditional-requirement/supertrait inputs
  for concrete dictionaries. Thread those dictionaries through functions, closures, aggregates, and
  async frames.
- [ ] Lower dictionary construction, inherited evidence, default dispatch, method selection, and safe
  concrete specialization without changing unoptimized behavior.
- [ ] Register the shipped primitive and structural implementations and standard traits.
- [ ] Expand `deriving {Eq, Ord, Show, Hash}` into ordinary implementations before coherence checking.

#### Modules, projects, externals, and whole-program semantics

- [x] Separate and validate leading import headers while preserving their written forms, aliases,
  selectors, source lines, and imports-stripped UTF-8 body offsets for formatting and diagnostics;
  retain uppercase-final paths for the resolver to disambiguate as modules or type selectors.
- [~] Resolve whole-module, aliased, value-selector, and type-selector imports using typed module
  interfaces, longest-module-path ambiguity rules, export validation, and post-resolution collision
  checks. Map module names to source paths and select project, include, dependency, or shipped-library
  sources with ambiguity and reserved-namespace checks; filesystem discovery and recursive plan
  construction remain.
- [x] Validate explicit exports and build value/type/constructor/submodule interfaces from parsed
  programs without exporting externals, trailing bodies, private declarations, or imported modules
  implicitly.
- [ ] Enforce sequential visibility, qualification, reserved namespaces, module cycles, and stable
  compiler-private names across stitched modules.
- [ ] Parse and validate `ashes.json`, defaults, entry points, source roots, includes, output settings,
  dependencies, and dev dependencies.
- [ ] Discover projects upward, honor explicit project selection, and construct a deterministic
  compilation plan from source-only packages.
- [ ] Resolve path and registry package graphs, lock files, package identities, one-version-per-package
  coherence, and program-global providers/implementations.
- [ ] Stitch the complete project while preserving original file/module spans, definition identities,
  package provenance, and source-function origins.
- [ ] Lift and resolve inline modules, enforce their restricted declaration surface, and integrate them
  with cross-file imports, exports, aliases, and selector ambiguity rules.
- [ ] Type external functions, opaque/declared resource types, ownership modes, native strings, arrays,
  pointers, buffers, out parameters, symbols, libraries, and capability requirements.
- [ ] Validate external ABI combinations and produce the metadata required by lowering, code generation,
  linking, LSP, and package capability auditing.
- [ ] Match the current compiler's entry-expression rules, project diagnostics, and deterministic
  diagnostic ordering across files.

#### IR model and lowering

- [ ] Model the complete `IrProgram`, functions, registers, locals, literals, coroutine metadata,
  ownership instructions, and stable function-origin lineage.
- [ ] Implement the canonical lowered/final IR text format and deterministic function selection used by
  `--emit-ir` and compiler reports.
- [ ] Lower constants, locals, strict left-to-right evaluation, calls, closures, captures, partial
  applications, and lifted functions.
- [ ] Lower control flow, conditions, matches, guards, recursion, mutual recursion, and tail calls.
- [ ] Lower tuples, lists, strings, bytes, nominal/record/zero-cost ADTs, constructors, field access,
  patterns, and record updates.
- [ ] Lower operators, BigInt, text/number conversions, program arguments, panic, standard I/O,
  filesystem, environment, process, networking, TLS/HTTP, regex, and other builtin operations.
- [ ] Lower external calls, resources/destructors, native ownership conventions, library/resource
  references, and target ABI metadata.
- [ ] Lower capability handlers/providers and trait evidence according to the completed semantic plans.
- [ ] Transform async functions into state machines with coroutine frames, resumptions, task scheduling,
  cancellation/error paths, and structured parallelism.
- [ ] Retain source maps, definition/hover identities, diagnostic locations, function origins, and
  explanation metadata through generated helper functions.
- [ ] Validate lowered IR invariants and compare normalized IR fixtures with the C# compiler.

#### Optimization, ownership, and reuse

- [ ] Port compile-time evaluation and the current deterministic IR optimization pipeline, including
  constant simplification, dead-code cleanup, inlining/specialization, and metadata preservation.
- [ ] Port ordinary and mutual tail-call optimization, stack-safety rules, and profitability/cost
  signals without changing strict evaluation order.
- [ ] Infer parameter/capture ownership, result reachability and freshness, moves, borrows, forwarding,
  and whole-program SCC provenance summaries.
- [ ] Classify copy, RC-managed, resource, borrowed-view, region, and unsupported heap layouts with
  constructor-specific child/drop information.
- [ ] Insert Perceus duplication/drop operations and deterministic resource cleanup across ordinary,
  exceptional, handler, and coroutine control flow.
- [ ] Place stack, scoped-region, task/capability-region, persistent-region, RC, special-resource, global,
  and OS-backed allocations under the current no-GC contract.
- [ ] Normalize complete graphs and insert deep-copy boundaries where region or ownership rules require
  them.
- [ ] Detect top-cell freshness and uniqueness, synthesize structural droppers, and implement safe
  allocation reuse for tuples, ADTs, closures, and tail-recursive paths.
- [ ] Compute coroutine-frame ownership, async capture lifetimes, parallel handoff rules, and cleanup of
  cancelled or completed tasks.
- [ ] Preserve semantics under `--debug-disable-reuse`, optimization levels, trait specialization
  changes, and explanation/report instrumentation.
- [ ] Produce stable `ownership`, `rc`, `reuse`, and `memory` explanation snapshots equivalent to the
  current public reports.

#### LLVM code generation and runtime integration

- [ ] Define pure-Ashes bindings to the required LLVM C API and load the installed-layout host
  `libLLVM` without checkout-relative assumptions.
- [ ] Select target triples, data layouts, CPUs, optimization levels, verification, object emission,
  and host/target-independent compile options.
- [ ] Emit LLVM for the complete IR: primitives, control flow, locals, closures, ADTs, strings, bytes,
  allocations, RC/drop/reuse, globals, and calls.
- [ ] Implement platform ABIs, stack handling, external calls, native arrays/strings/buffers/out
  parameters, resources, destructors, and debug-safe symbol naming.
- [ ] Emit the runtime support for buffered stdout/stderr, program arguments, process exit, environment,
  terminal raw/poll operations, files/directories/memory maps, subprocesses, clocks/entropy, sockets,
  HTTP/TLS, regex, math, and BigInt.
- [ ] Emit scheduler, task, async I/O, structured-parallelism, worker-stack, cancellation, and graceful
  shutdown runtime support.
- [ ] Select and link the shipped Mbed TLS, openlibm, and PCRE2 bitcode and any external library/resource
  payloads hermetically.
- [ ] Emit source-level debug information and preserve valid DWARF/target debug sections through every
  supported optimization level.
- [ ] Generate verified object files for `linux-x64`, `linux-arm64`, `win-x64`, and `win-arm64` from the
  corresponding native host compiler bundle.

#### Object parsing and executable linking

- [ ] Parse LLVM-emitted ELF and COFF objects, sections, symbols, string tables, data/BSS, and relocation
  addends using immutable byte buffers.
- [ ] Lay out and relocate x86-64 ELF64 images and emit the Linux entry trampoline and executable mode.
- [ ] Lay out and relocate AArch64 ELF64 images with the complete supported relocation set.
- [ ] Lay out AMD64 PE32+ images, imports, BSS, entry trampoline, stack probing, and relocations.
- [ ] Lay out ARM64 PE32+ images, imports, unwind/runtime requirements, entry code, and relocations.
- [ ] Resolve compiler runtime symbols, platform APIs, linked bitcode symbols, external libraries, and
  embedded resources deterministically.
- [ ] Write final executables atomically, preserve installed-layout behavior, and produce deterministic
  structural diagnostics for malformed or unsupported objects.
- [ ] Execute host-target outputs and preserve the current Wine/QEMU/native/structural validation policy
  for non-host targets, including structural-only win-arm64 validation on x64 hosts.

#### CLI, package management, and registry client

- [ ] Implement shared argument scanning, help, validation, exit codes, stdout/stderr discipline, target
  selection, CPU/worker/stack options, optimization levels, and debug options.
- [ ] Implement `compile` for files, expressions, projects, output selection, IR dumps, and compiler
  reports.
- [ ] Implement `run`, program argument forwarding, temporary outputs, and propagation of program exit
  status.
- [ ] Implement the stateful `repl`, target/optimization commands, recovery after diagnostics, and
  deterministic cleanup.
- [ ] Implement `fmt` discovery, preview/write behavior, project awareness, malformed-file handling, and
  canonical exit codes.
- [ ] Implement `init`, `add`, `remove`, `restore`, `tree`, and `why` over manifests, path/registry
  dependencies, lock files, frozen/offline modes, and the content-addressed source cache.
- [ ] Implement semantic versions, version constraints, deterministic dependency solving, `ash1:` source
  hashes, archive validation, and package materialization.
- [ ] Implement registry configuration and credentials plus `login`, `publish`, `yank`, `search`, and
  `info`, including package capability extraction from compiler metadata.
- [ ] Preserve the documented retired-`install` diagnostic and compatibility behavior for every current
  command and flag.
- [ ] Render structured diagnostics and the `ownership`, `rc`, `reuse`, `traits`, `authority`,
  `concurrency`, and `memory` reports with stable filtering and stderr behavior.

#### TestRunner and validation infrastructure

- [ ] Discover individual files, directories, and project tests with the documented project-mode rules
  and deterministic ordering.
- [ ] Parse and enforce stdout, stderr, compile-error, exit-code, stdin, file/text, file/bytes,
  executable-directory, working-directory, TCP fixture, and formatter-skip directives.
- [ ] Compile tests normally, with the requested raw/reuse-disabled pipeline, and for selected targets;
  execute through native, Wine, or QEMU runners as appropriate.
- [ ] Enforce timeouts, isolate temporary files/processes/ports, terminate process trees, and report
  failures without leaking fixtures.
- [ ] Match exact output normalization, compiler-error matching, skip behavior, summaries, and exit
  codes.
- [ ] Run the full existing `.ash` corpus through both toolchains and classify every difference before
  bootstrap acceptance.

#### LSP, DAP, editor integration, and fuzzing

- [ ] Implement Content-Length JSON-RPC transport, lifecycle, cancellation, document state, UTF-8/UTF-16
  coordinate conversion, and structured error handling for the LSP.
- [ ] Provide compiler-backed diagnostics, hover schemes/effects/evidence, definitions, completions,
  references, semantic tokens, and canonical formatting without duplicating compiler logic.
- [ ] Resolve projects, imports, dependencies, standard-library documentation, and multi-file updates in
  the LSP with deterministic invalidation.
- [ ] Implement the standalone DAP transport and session lifecycle plus launch, breakpoints, stepping,
  threads, stack frames, scopes, variables, termination, and disconnect requests.
- [ ] Broker GDB, LLDB, and `lldb-dap` processes with portable command/response parsing, timeouts, value
  formatting, and source-path mapping; keep the DAP independent of compiler implementation packages.
- [ ] Preserve the existing VS Code extension's compiler/LSP/DAP acquisition and launch contracts; the
  extension itself remains JavaScript/TypeScript because it runs inside the VS Code extension host.
- [ ] Port deterministic seeds, profiles, generation budgets, typed program generation, invalid-source
  mutation, AST/IR invariants, execution oracles, coverage guidance, and interaction templates to the
  pure-Ashes fuzzing package.
- [ ] Port shrinking, stable size metrics, corpus replay, artifact writing, failure classification,
  replay commands, isolated workers, timeouts, and campaign summaries.
- [ ] Differentially fuzz the self-hosted and C# phases without making host-language helpers part of the
  self-hosted implementation or its normal test path.

#### Bootstrap, release, and replacement gates

- [ ] Define a reproducible stage-0 input consisting of the released C# compiler, pinned LLVM/runtime
  payloads, restored source dependencies, and the pure-Ashes compiler sources.
- [ ] Build a stage-1 host compiler with stage 0, then use stage 1 to build stage 2 without invoking C#,
  Python, shell, or Node.js as an implementation step.
- [ ] Compare stage-1/stage-2 deterministic artifacts where possible and otherwise compare normalized
  tokens, diagnostics, schemes, IR, object structure, executable behavior, and reports.
- [ ] Compile and run the compiler, standard library, examples, and complete test corpus with the
  self-hosted host-target compiler.
- [ ] Build self-contained compiler, CLI, LSP, DAP, TestRunner, and fuzzing bundles for every host RID,
  including the matching native `libLLVM` and runtime payload layout.
- [ ] Cross-compile and execute/structurally validate all four target RIDs under the documented host
  matrix.
- [ ] Add deterministic bootstrap, parity, packaging, and release jobs to local CI and hosted CI with
  cached but reproducibly verifiable native assets.
- [ ] Demonstrate acceptable compile time, peak memory, produced-code behavior, diagnostics, and tool
  compatibility on representative projects before changing the default compiler.
- [ ] Make removal of the C# compiler, C# tooling servers, or compatibility paths a separate reviewed
  decision after sustained bootstrap and release parity; do not couple removal to the port itself.

### Continuation discipline

- Start behavior changes with a pure-Ashes failing test under `selfhost/tests/<package>/`; keep unit tests
  within the owning package and add cross-package tests only at real public boundaries.
- Keep test suites flat: compose small named checks through pipelines instead of sequencing them with
  deeply nested `let ... in` pyramids.
- Keep each milestone on a fresh `feature/...` branch and worktree. Copy `runtimes/` from the main
  checkout into a new worktree before backend-dependent validation; runtime payloads are intentionally
  not regenerated by the self-hosted work.
- Do not delete or modify the existing C# or Node.js implementations as part of the port. They remain
  the compatibility oracle until bootstrap parity and the eventual removal decision are reviewed
  separately.
- For the current frontend, formatter, and semantics tests, compile the corresponding
  `selfhost/tests/*/ashes.json` project and execute the emitted host binary. Run semantics tests both
  normally and with `--debug-disable-reuse` so ownership/reuse differences cannot hide a defect.
- Before publishing a milestone, format every changed `.ash` file, build `Ashes.slnx`, run the compiler
  and LSP unit suites, and verify C# formatting. Record exact commands and counts in the PR. Add focused
  bootstrap parity fixtures as soon as a self-hosted phase can serialize the same public result as C#.
- Update this migration table and the implementation status in `selfhost/README.md` in the same PR when
  a milestone changes either one. Do not mark an area complete merely because its data model exists.

## Language/stdlib prerequisites

`Required` means Ashes still needs a design or implementation. An area tag names the eventual
consumer, not the project that must implement the prerequisite. Tests are required for every
delivered change. `Complete` capabilities link to the normative documentation for the shipped
surface. If a future audit finds an incomplete capability, its row must link to an actionable Ashes
work package added to this document.

| Capability | Status | Areas |
|---|---|---|
| [Unsigned integer support (`u8`, `u16`, `u32`, `u64`)](../reference/language.md#_2-1-integers) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker` |
| [Byte type (`u8`) and byte literals](../reference/language.md#_2-1-integers) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker` |
| [Bitwise operators (`&`, `\|`, `^`, `<<`, `>>`, `~`)](../reference/language.md#_3-5-bitwise) | Complete | `Compiler/Backend`, `Compiler/Linker` |
| [Numeric text conversions (`parseInt`, `parseFloat`, `fromInt`, `fromFloat`, `toHex`)](../reference/standard-library.md#ashes-text) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter`, `CLI` |
| [Basic FFI (`external` functions/types, pointers, resources, `symbol@library`)](../reference/language.md#_5-1-external-declarations) | Complete | `Compiler/Backend` |
| [Call-scoped native arrays of opaque handles](../reference/language.md#_5-1-external-declarations) | Complete | `Compiler/Backend`, `LSP`, `Fuzzing` |
| [LLVM out parameters](../reference/language.md#_5-1-external-declarations) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `LSP`, `Fuzzing` |
| [Returned native strings](../reference/language.md#_5-1-external-declarations) | Complete | `Compiler/Backend`, `LSP`, `Fuzzing` |
| [Foreign pointer-plus-length buffers](../reference/standard-library.md#ashes-ffi) | Complete | `Compiler/Backend`, `LSP`, `Fuzzing` |
| [Immutable `Bytes` with indexed reads and append helpers](../reference/standard-library.md#ashes-byte) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker`, `LSP`, `DAP` |
| [Little-endian byte encode/decode helpers (`u16/u32/u64`)](../reference/standard-library.md#ashes-byte) | Complete | `Compiler/Linker`, `DAP` |
| [Efficient preallocation, range copy, and random-access binary patching](../reference/standard-library.md#ashes-byte) | Complete | `Compiler/Linker`, `LSP`, `Fuzzing` |
| [Binary file output (`Ashes.IO.File.writeBytes`)](../reference/standard-library.md#ashes-io-file) | Complete | `Compiler/Linker`, `CLI`, `TestRunner`, `Fuzzing` |
| [Path normalization, joining, parent/basename, and relative paths](../reference/standard-library.md#ashes-io-path) | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `TestRunner`, `Fuzzing` |
| [Current/executable/temp/cache directories and environment lookup](../reference/standard-library.md#ashes-io-environment) | Complete | `Compiler/Semantics`, `Compiler/Backend`, `CLI`, `LSP`, `DAP`, `TestRunner`, `Fuzzing` |
| [Directory enumeration, creation, deletion, and atomic rename](../reference/standard-library.md#ashes-io-directory) | Complete | `Compiler/Semantics`, `Compiler/Backend`, `CLI`, `LSP`, `TestRunner`, `Fuzzing` |
| [Marking emitted ELF files executable](../reference/standard-library.md#ashes-io-file) | Complete | `Compiler/Semantics`, `Compiler/Backend`, `CLI`, `LSP`, `TestRunner`, `Fuzzing` |
| [stderr output and controlled process exit codes](../reference/standard-library.md#ashes-io) | Complete | `CLI`, `LSP`, `DAP`, `TestRunner`, `Fuzzing` |
| [Installed-layout host-tool integration workflow](../guide/testing.md#execution-model) | Complete | `Compiler`, `CLI`, `TestRunner`, `Fuzzing` |
| [String helpers (`substring`, `length`, `indexOf`, `startsWith`, `contains`, `split`, `trim`)](../reference/standard-library.md#ashes-text) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter`, `CLI`, `LSP`, `DAP` |
| [Unicode scalar classification through `Rune`](../reference/standard-library.md#ashes-rune) | Complete | `Compiler/Frontend`, `Formatter`, `LSP` |
| [Canonical UTF-8 source offsets and UTF-16/LSP coordinate conversion](../reference/language.md#source-encoding-and-coordinates) | Complete | `Compiler/Frontend`, `Formatter`, `LSP`, `DAP` |
| [Persistent immutable map (`Ashes.Collection.Map`)](../reference/standard-library.md#ashes-collection-map) | Complete | `Compiler/Semantics`, `Compiler/Backend`, `LSP`, `DAP` |
| [Persistent immutable array (`Ashes.Collection.Array`)](../reference/standard-library.md#ashes-collection-array) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Compiler/Backend`, `Formatter` |
| [Records, named patterns, and record-update syntax](../reference/language.md#_4-3-record-types) | Complete | `Compiler`, `Formatter`, `LSP` |
| [User-written type annotations, aliases, and zero-cost nominal types](../reference/language.md#_4-2-zero-cost-nominal-types) | Complete | `Compiler`, `Formatter`, `LSP` |
| [Project/module compilation with explicit exports and path dependencies](../guide/projects.md) | Complete | `Compiler/Semantics`, `CLI`, `LSP` |
| [Catchable error propagation for compile-pipeline flows](../reference/language.md#_13-4-error-handling) | Complete | `Compiler`, `Formatter`, `CLI`, `LSP`, `DAP` |
| [RC-Perceus deterministic memory without cyclic graphs](../internals/architecture.md#memory-model) | Complete | `Compiler`, `Formatter`, `LSP`, `DAP` |
| [Large-ADT exhaustiveness and performance hardening](../reference/language.md#_11-pattern-matching) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter` |
| [JSON parsing/serialization for `ashes.json` and JSON-RPC](../reference/standard-library.md#ashes-text-json) | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `DAP` |
| [Stdio JSON-RPC Content-Length framing](../reference/standard-library.md#ashes-net-rpc) | Complete | `LSP`, `DAP` |
| [Interactive subprocess control with piped streams and timeouts](../reference/standard-library.md#ashes-io-process) | Complete | `DAP`, `TestRunner`, `Fuzzing` |
| [Regex utilities for tooling text](../reference/standard-library.md#ashes-text-regex) | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `DAP`, `TestRunner` |
| [`Ashes.Test` unit assertions](../reference/standard-library.md#ashes-test) | Complete | `Tests` |

All currently identified Ashes prerequisites for self-hosting are complete. This audit does not claim
that compiler sources have been ported or that bootstrap staging has begun.

Registry/package commands, LSP, DAP, TestRunner, fuzz-harness parity, bootstrap staging, persistent
sets, generic hashing, and a named text builder are not missing Ashes prerequisites. They may become
separate port or optimization work later, but are outside this capability audit until a concrete
language/runtime/stdlib blocker is demonstrated.
