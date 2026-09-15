# Self-Hosting Log

The completed work of the self-hosted toolchain migration, kept out of
[SELF_HOSTING.md](SELF_HOSTING.md) so that document stays a plan rather than a history. Entries are
verbatim and grouped by the same subsystem headings; a partially complete item appears here with the
part that is done, and its open tail stays in the plan.

This file is a record, not a queue. Nothing here is work. Identifiers are never reused, so it is
also where to look before numbering a new item in a series. It closes with the capability audit that
preceded the port.

## Completed work

### Package and test foundations

- [x] **PKG-1** Define pure-Ashes `frontend`, `formatter`, and `semantics` packages with the required dependency
  direction and no host-language implementation helpers.
- [x] **PKG-2** Keep package tests in separate `devDependency` projects and compile them into standalone native
  executables.
- [x] **PKG-3** Exercise frontend, formatter, and semantics tests with reuse enabled and with
  `--debug-disable-reuse`.
- [x] **PKG-4** Supply the language, standard-library, host-environment, filesystem, process, byte-buffer, JSON,
  regex, and installed-layout capabilities identified by the prerequisite audit below.
- [x] **PKG-5** Document every production module's responsibility and load-bearing invariants, preserving
  behaviorally relevant stage-0 contracts without copying host-language API boilerplate.
- [x] **PKG-7** Make every self-hosted package buildable from a restored source-only dependency graph without
  undeclared checkout-relative inputs.
- [x] **PKG-8** Run the three script-style project test files (`ProjectDiscoveryTests.ash`,
  `ProjectSourceEnumerationTests.ash`, `ProjectCompilationPlanningTests.ash` under
  `selfhost/tests/semantics/`) from a suite: they are trailing-expression scripts that no runner
  invokes today, so their assertions have not been executed since they were written. Wire them
  into the projects suite (or a runner of their own if they need the repository layout), fix
  whatever has rotted, and keep them in the standing gate.
  Done: the three files now live in `selfhost/tests/projects/`, export their `run...Tests`
  entry, and run first in that suite's `Main.ash` ahead of the dependency-graph and lock-file
  groups, with a pass line of their own. They build their fixtures under the process's
  temporary directory and remove them afterwards, so they need no repository layout. Nothing
  had rotted: every assertion passes as written against the current discovery, enumeration,
  and planning modules.


### Frontend and source model

- [x] **FE-1** Model tokens, structured diagnostics, and canonical UTF-8 byte spans.
- [x] **FE-2** Lex identifiers, keywords, operators, comments, whitespace, strings, runes, signed and unsigned
  integers, floats, and malformed input, including Unicode scalar validation.
- [x] **FE-3** Model the complete typed syntax surface for programs, declarations, expressions, patterns, and
  type expressions without collapsing those categories.
- [x] **FE-4** Parse literals, variables, qualified references, calls, tuples, lists, records, record updates,
  unary/binary operators, pipes, and their precedence and associativity.
- [x] **FE-5** Parse lambdas, conditionals, nested and recursive bindings, `let?`, `let!`, matches, guards,
  handlers, `perform`, and every current pattern form.
- [x] **FE-6** Parse named, applied, tuple, function, pointer, capability-row, annotated, and constrained type
  syntax.
- [x] **FE-7** Parse exports, aliases, algebraic/record/zero-cost types, flat top-level bindings, recursive
  groups, and optional trailing expressions after the project layer has separated any import header.
- [x] **FE-8** Parse external functions and types, ownership annotations, resources and destructors, native
  strings, pointers, buffers, out parameters, capability rows, and `symbol@library` aliases.
- [x] **FE-9** Parse capability declarations, static providers, trait declarations, implementations,
  supertraits, requirements, defaults, and deriving clauses.
- [x] **FE-10** Preserve source-ordered recovery diagnostics and the current declaration-boundary behavior for
  incomplete editor input and project-stitched programs.
- [x] **FE-11** Compare the complete frontend diagnostic corpus with the C# frontend by diagnostic code, span,
  ordering, and recovery result (the versioned `ashes-diagnostic-v1` parity format under
  `parity/frontend/diagnostics`, checked from both stage 0 and
  `selfhost/tests/frontend-diagnostic-parity`). Porting surfaced and fixed: unsigned-literal
  overflow picking the wrong diagnostic, an unmatched closing bracket permanently disabling
  declaration-boundary detection, three divergent end-of-input wordings unified behind one shared
  check, a refutable-let-pattern span read after its arena scope was reclaimed, and a spurious
  `ASH003` code on the constructor-less-type diagnostic.


### Formatter

- [x] **FMT-1** Canonically format every expression, pattern, and type form while preserving precedence.
- [x] **FMT-2** Canonically format complete programs and every top-level declaration form.
- [x] **FMT-3** Preserve intentional source spellings where the formatter contract requires them and sort only
  semantically unordered surfaces such as requirement sets.
- [x] **FMT-4** Cover golden output, parse-format-parse behavior, and formatter idempotence.
- [x] **FMT-5** Preserve written import headers and leading/standalone comments around the formatted AST body
  (`formatSource`): the leading comment block kept verbatim, imports re-rendered canonically,
  standalone comments reinserted at their whitespace-insensitive token-signature anchors (next
  anchor, then previous, then top of file — no comment text is ever dropped).
- [x] **FMT-6** Apply formatter options for indentation/newlines (`FormattingOptions`, applied as a
  post-processing rescale over the fixed-4-space internal output) and the opt-in pipeline-layout
  collection (`x |> f |> g` from a nested call chain, all-or-nothing, stopping at a capitalized
  constructor once an outer stage exists). Porting found and fixed a pre-existing stage-0
  double-newline-conversion bug (`\r\r\n`) and a stage-order reversal in the first selfhost port.
- [x] **FMT-7** Keep the parentheses around a record update used as a record-literal field value (and in
  multiline call arguments and list elements) whenever another sibling follows — dropping them
  makes the update absorb the following fields, so the formatted file no longer parses the same.
  The rule is written into the formatter reference; covered by `selfhost/tests/formatter/Main.ash`.
- [x] **FMT-8** Compare the full formatter corpus and malformed-input behavior with the C# formatter. The
  whole-file pass found one crash — a nested `let name param = ...` binding's sugar-parameter list
  clobbered by arena reuse one call after parsing, fixed by deep-copying the built value before
  span construction in `parserParseTopLevelBinding` — and a boundary-splitter gate (a declaration
  value is no longer cut at an indented `then`/`else`/`in`/pipe line after a completed call).


### Semantic foundations and ordinary inference

- [x] **SEM-1** Model stable symbols, qualified identities, immutable lexical scopes, and deterministic fresh
  type variables.
- [x] **SEM-2** Model semantic primitive, unsigned, function, tuple, list, pointer, named, capability, and open-row
  types.
- [x] **SEM-3** Compute free variables, substitutions, occurs checks, structural unification, generalization,
  instantiation, and constrained rank-1 schemes.
- [x] **SEM-4** Unify capability rows independently of declaration order and allow open tails to absorb unmatched
  capabilities.
- [x] **SEM-5** Resolve source primitives, parameters, functions, tuples, pointers, aliases, nominal types,
  zero-cost types, and capability rows.
- [x] **SEM-6** Infer literals, variables, lambdas, calls, tuples, lists, conditionals, ordinary lets, recursive
  lets, and annotations with let-polymorphism.
- [x] **SEM-7** Infer all operator families while retaining their trait constraints.
- [x] **SEM-8** Infer matches, guards, literal/list/tuple/constructor/record/as/or patterns, and consistent
  pattern-local bindings.
- [x] **SEM-9** Register algebraic, record, alias, and zero-cost declarations; infer constructors, constructor
  patterns, record literals, and record updates.
- [x] **SEM-10** Infer `Result` map/flat-map/error-map pipelines and `let?` propagation.
- [x] **SEM-11** Infer sequential top-level bindings, shared-monomorphic recursive groups, and an optional trailing
  expression.
- [x] **SEM-12** Infer async bodies, `await`, `let!`, task result/error propagation, and structured task APIs:
  the standard `Task(e, a)` type is seeded next to `Maybe`/`Result`, `await task` types as the
  `Result(e, a)` the task runs to (`ExpectedTaskType` otherwise), and `let!` exposes that `Result`
  to the ordinary propagation rules; lowering `await` belongs to core lowering.
- [x] **SEM-13** Perform complete match exhaustiveness, redundancy, large-ADT hardening, and source-compatible
  diagnostic reporting (`matchCoverageError`, stage 0's message text; also enforced during
  single-file lowering via `checkCoreMatchCoverage`, so the `tests/pattern_*` diagnostics fail
  through the self-hosted CLI with stage 0's wording). Porting found the parser had no column rule
  for match/handler arm attachment — a nested match silently swallowed the enclosing match's
  remaining arms; `parserLeadingPipeColumn` now ends an arm list at any `|` dedented past the
  first arm's column, as stage 0 does.
- [x] **SEM-14** Enforce resource move/borrow/consume rules, deterministic cleanup constraints, and use-after-move
  diagnostics at the semantic boundary. In `CoreLowering.ash`, every owned `let` or pattern
  binding whose resolved type is a resource — the compiler-provided `FileHandle` and `Process`,
  or a declared `external type T resource destructor f` by its opaque name — is live, closed, or
  moved. Closed: an explicit `File.close`, or a `consume` argument of the resource's own
  destructor (closing a moved or closed binding is reported: moved-close, double close). Moved:
  stored into a constructor, tuple, list, or cons cell; passed to a consuming callee (a let-bound
  lambda borrows a parameter only when stage 0's `isParamUsedOnlyAsBorrowRead` proves it reads
  it, any other callee and any non-destructor `consume` external consumes; a `borrow` external
  only reads); captured by a closure; or returned as its arm's result. A resource builtin or
  external reading a released binding reports stage 0's use-after-close/use-after-move messages,
  and a live binding gets stage 0's `CleanupResource` at its `let` or arm exit — naming the
  destructor ABI for a declared resource — which the backend lowers to `close` for a handle and
  pipe-close plus reap for a process (the destructor call itself is CG-8's). Residue tracked
  elsewhere: sockets join the resource names with milestone 6's net builtins; the recursive
  cleanup of a resource-bearing aggregate and the dropper of a resource-capturing closure are
  OPT-25's; the cleanup a tail self-call must run before its back edge is OPT-25's TCO tail
  (today the arm's cleanup follows the call, which keeps that call an ordinary call rather than
  a fused tail call).
- [x] **SEM-16** Validate every written binding `requires` clause against the inferred canonical external
  requirement set, including recursive groups and ambiguity checks.
- [x] **SEM-18** Resolve a polymorphic `==` the way stage 0 does when the operands' type is still a
  variable at the comparison. `tests/runtime_rc_tco_nested_tuple_pattern_alias.ash` is rejected by
  the self-hosted lowering with a `String`/`Int` mismatch (`CoreCallTypeMismatch`) where stage 0
  accepts the program: the comparison's operand type is defaulted before the pattern alias that
  pins it is seen. Establish which side stage 0 takes (the deferred operator default sealed at
  finalization against the finished substitution), mirror it, and register the fixture in the
  loop sweep. The trait-evidence lowering of `==` on a derived type stays under TRT-15.
  Done: stage 0 maps such a comparison to the `Eq` trait, emits a placeholder when the operand
  is still a variable, and lowers the binding again with a late type hint once the body has pinned
  it, so the fixture's `lit == ch` becomes a `CmpStrEq`. The self-hosted lowering now takes the
  path its deferred `+` already took: two unresolved operands are unified with each other, an
  integer comparison is emitted speculatively and recorded under its target temp, and the shared
  variable joins the body's unresolved call results, so a body that pins it later is lowered a
  second time against the resolved type. The finalization rewrite covers the still-speculative
  survivors (a lambda pinned by the enclosing program): `CmpStrEq`/`CmpStrNe`, the float forms,
  and the BigInt compare expansion, which takes two fresh temps past the function's count. A
  variable no body pins seals to the integer comparison, as `+` seals to `AddInt`, until TRT-15's
  dictionaries. Both fixtures (`runtime_rc_tco_nested_tuple_pattern_alias`,
  `runtime_rc_whole_string_pattern_recursion`) compile and match stage 0 in the loop sweep.
- [x] **SEM-19** Report an argument type mismatch once per call. Stage 0 reports the same mismatch
  three times at the call span on an ordinary call (the expected-type unification in `LowerExpr`
  plus the contextual unification) and twice on a tail self-call; the self-hosted lowering rejects
  the same programs through `ensureFunctionType` binding the open return to a fresh arrow and
  reports a location-less `CoreCallTypeMismatch`. Dedupe stage 0's diagnostics at the call span,
  give the self-hosted mismatch the call's span, and pin both in the diagnostic parity fixtures.
  Done: stage 0's call site owns the report. A `LoweredValueRequest` whose expected type is
  caller-reported (`WithCallerReportedExpectedType`: a call argument's parameter type, a list
  literal's element type) makes the expected-type unification in `LowerExpr`, the call result
  pre-constraint, and the result unification in `LowerCallFinish` silent
  (`SuppressUnificationDiagnostics`), the literal pre-constraint is always silent, and the tail
  self-call's contextual unification reports at the call span like the ordinary path, so an
  ordinary call, a tail self-call, a call-shaped argument (one report per call), and a list-literal
  argument (one `ASH005`) each report a mismatch exactly once. The self-hosted `CoreCallTypeMismatch`
  carries a `CoreMismatchSite`: every unification failure takes the innermost enclosing span and
  its resolved location (stage 0's span stack), and a call argument's failure takes the call's
  span plus the argument's ordinal and the callee's display name, attached by the ordinary and tail
  self-call argument bindings and by the argument request the expected type travels under
  (`ConsumerRequest.argumentSite`, so a nested call's result pre-constraint and the unforwarded
  expected-type unification report the outer argument, as stage 0's silenced unifications leave
  the outer call site to). `LoweringDiagnostics.ash` renders it as stage 0's `ASH002` text with
  stage 0's type spelling (`List<Int>`, shared `a`, `b` variable names) and the CLI prints
  `path:line:col ASH002 message`. Porting the tail self-call fixture surfaced a typing gap fixed
  on the way: a recursive member's first parameter annotation was dropped (`lambdaParts` kept no
  annotation) and its body was lowered without the member's result type as the expected type, so
  a self-call met fresh arrows instead of the annotated parameter types and the mismatch surfaced
  at the outer call; the annotation now binds before the body and the body request carries the
  result type (`withRecursiveBodyRequest`), as stage 0's lambda-chain lowering does. The new
  `selfhost/parity/semantics/diagnostics` fixtures pin both calls in the `ashes-diagnostic-v1`
  format, checked by `SelfhostSemanticDiagnosticParityTests` and the
  `selfhost/tests/semantics-diagnostic-parity` suite. Not ported: stage 0's enclosing contexts
  (`-> in if branches`) and the excerpt lines under a diagnostic.


### Capabilities and handlers

- [x] **CAP-1** Register capability declarations and parameter-sharing operation schemes.
- [x] **CAP-2** Propagate ambient effects through implicit/explicit operations, lambdas, ordinary and
  higher-order calls, partial application, and `Result` pipelines.
- [x] **CAP-3** Infer handler operation arms, shared instances, `resume`, return arms, arm effects, and residual
  row discharge.
- [x] **CAP-4** Register complete, coherent, instance-specialized static providers and type-check their operation
  implementations.
- [x] **CAP-5** Satisfy exact concrete capability requirements from providers while retaining abstract
  requirements and rejecting provider/handler ambiguity.
- [x] **CAP-6** Lower dynamic handler evidence, one-shot continuation state, pre/post handler control flow, and
  dynamically scoped handler globals into IR — the mechanism is described under the
  "Lower capability handlers/providers and trait evidence" entry in "IR model and lowering".

### Traits, implementations, and evidence

- [x] **TRT-1** Infer operator constraints and retain them in generalized schemes.
- [x] **TRT-2** Register trait declarations, qualified method schemes, forward supertraits, acyclic supertrait
  graphs, and type-checked default bodies.
- [x] **TRT-3** Register ordinary `implement` declarations with resolved rigid heads, requirements, supplied
  methods, and inherited defaults.
- [x] **TRT-4** Validate implementation trait/arity, method uniqueness and completeness, substituted method
  signatures, capability rows, and requirement variables.
- [x] **TRT-5** Reject exact duplicate and structurally overlapping implementation heads independently of source
  or traversal order.
- [x] **TRT-6** Track package provenance for traits and nominal head types and enforce the orphan ownership rule.
- [x] **TRT-7** Validate decreasing conditional requirements for generic implementation heads while allowing
  fixed requirements on fully concrete heads.
- [x] **TRT-8** Reject dependency cycles among the defaults selected by an implementation while allowing a
  supplied method override to break the cycle.
- [x] **TRT-9** Canonicalize constraints, remove exact duplicates, and remove supertraits implied by stronger
  constraints.
- [x] **TRT-10** Validate written binding `requires` clauses against inferred canonical constraints, including
  nested lets, recursive groups, invalid trait heads, and ambiguous requirement variables.
- [x] **TRT-11** Resolve unique concrete instances recursively with cycle/depth guards while preserving abstract
  constraints as hidden dictionary parameters.
- [x] **TRT-12** Diagnose missing, ambiguous, incoherent, non-terminating, and ambiguous-type-variable goals with
  canonical requirement traces.
- [x] **TRT-16** Stage 0: compile a concrete instance's implementation lambdas once instead of
  rebuilding them at every use. Every `==`, `show`, `compare`, or `hash` on a derived type
  rewrote the whole nested dictionary value at the call site, closures for each method of each
  nested type included, so the emitted program grew linearly with the number of uses of a large
  derived type rather than with the number of types: a three-level derived type cost about 660
  functions, 19,000 IR lines, and 1 MB of binary per use, and the self-hosted semantics test
  program (`selfhost/tests/semantics`) was 79,348 functions and 4.06 million IR lines, of which
  78,713 functions were copies of another function after masking numbers; 1.2 million lines
  carried `Ashes.Trait` source locations and another 1.4 million the derived instances of
  `Types.ash` and `Token.ash`. Its stage-0 compile had gone from 36 s at `--debug` and about
  100 s at the default level on 2026-08-26 to 4.5 min and 10.6 min on 2026-09-07 (212 MB and 53
  MB binaries, 14 GB peak resident set), while the source grew 1.8x.
  Done (2026-09-07): `BuildTraitImplementationMethod` offers the implementation lambda it lowers
  for sharing under the goal's stable key, the method, and the construction context (the
  enclosing instances' self-ties, the hidden dictionary parameters active at the site, and the
  coroutine placement), and `LowerLambdaCore` reuses the recorded function when a later
  construction in the same context computes the same captures, emitting only the environment and
  the closure object; the self-tie ordinal is assigned once per goal and method so every
  construction binds the same tie name. Only fully concrete plans share. The repro's four
  `==`/`show` sites went from 2,654 functions to 193; the semantics test program to 19,619
  functions and 1.35 million IR lines, its default-level compile to 5.5 min, 13 MB, 7.7 GB.
  Three more stage-0 levers followed in the same change: the lifetime placement's alias-store
  scan indexed by slot (5.5 to 4.0 min), hover-type recording skipped outside the language
  server and resolved layout types memoized (4.0 to 3.4 min), and parallel code generation over
  partitions of the program module merged into one relocatable object (3.4 to 1.9 min; see
  [Parallel code generation](../internals/architecture.md#parallel-code-generation)). Compile
  phases are reported under `ASHES_TIMING`. Stage 1: the self-hosted trait lowering (TRT-13 to
  TRT-15) must build dictionaries over shared implementation functions from the start, never the
  per-use form; the other levers are tracked as OPT-53, CG-17, and CLI-11. Track the stage-0
  compile time and peak resident set of the self-hosted packages in the phase benchmark
  (BOOT-8) so the next drift is caught at a milestone close.


### Modules, projects, externals, and whole-program semantics

- [x] **MOD-1** Separate and validate leading import headers while preserving their written forms, aliases,
  selectors, source lines, and imports-stripped UTF-8 body offsets for formatting and diagnostics;
  retain uppercase-final paths for the resolver to disambiguate as modules or type selectors.
- [x] **MOD-2** Resolve whole-module, aliased, value-selector, and type-selector imports using typed module
  interfaces, longest-module-path ambiguity rules, export validation, and post-resolution collision
  checks. Map module names to source paths and select project, include, dependency, or shipped-library
  sources with ambiguity and reserved-namespace checks. Construct deterministic reachable-module plans
  in dependency-first order, reject cycles, and enumerate filesystem-backed sources deterministically.
- [x] **MOD-3** Validate explicit exports and build value/type/constructor/submodule interfaces from parsed
  programs without exporting externals, trailing bodies, private declarations, or imported modules
  implicitly.
- [x] **MOD-4** Enforce sequential visibility, qualification, reserved namespaces, module cycles, and stable
  compiler-private names across stitched modules. Dependency planning, cycle rejection, and
  dependency-ordered semantic scopes assign deterministic definition identities, record ordinary
  versus recursive visibility boundaries, realize resolved selectors and whole-module imports, validate
  full/short qualifiers and collisions, and assign stable public and compiler-private names. Syntax-tree
  rewriting preserves lexical shadows and source spans while replacing declaration, value, constructor,
  type, trait, and capability references with those compiler names.
- [x] **MOD-5** Parse and validate typed `ashes.json` manifests, including entry extensions, package versions,
  defaults, source roots, includes, output settings, registry/path dependencies, dev dependencies,
  root-level local `overrides`, and forward-compatible unknown fields. Filesystem path resolution and
  entry existence checks belong to project discovery.
- [x] **MOD-6** Discover projects upward, honor explicit project selection, load manifests, resolve project
  paths, validate entry existence, and deterministically plan reachable modules from source-only
  packages.
- [x] **MOD-9** Lift and resolve inline modules, enforce their restricted declaration surface, and integrate them
  with cross-file imports, exports, aliases, and selector ambiguity rules. Pure-Ashes lifting covers
  header recognition, indentation and dedenting, nested name composition, child-before-parent order,
  same-scope qualifier rewriting, and restricted-body, reserved-name, and duplicate-name validation.
  Reachable compilation planning now publishes synthetic sources with stable provenance, orders nested
  children before parents, rejects reachable file/inline collisions, honors compatibility and explicit
  parent exports, and resolves cross-file whole-module, alias, value-selector, and uppercase type imports.
- [x] **MOD-10** Type external functions, opaque/declared resource types, ownership modes, native strings, arrays,
  pointers, buffers, out parameters, symbols, libraries, and capability requirements. External opaque
  types are registered before function typing; source-call shapes omit compiler-owned out parameters,
  append their values to results, retain ABI syntax for validation, and keep direct-only contracts out
  of first-class bindings.
- [x] **MOD-11** Validate external ABI combinations and produce the metadata required by lowering, code generation,
  linking, LSP, and package capability auditing. Pure-Ashes validation resolves transparent aliases and
  zero-cost representations, preserves ordered parameter/source shapes and native ownership, verifies
  resource and owned-string destructors, and publishes canonical function, resource, symbol/library,
  direct-call, and sorted runtime-authority metadata with the inferred program.
- [x] **MOD-12** Match the current compiler's entry-expression rules, project diagnostics, and deterministic
  diagnostic ordering across files. Declaration-only entries infer Unit, non-entry trailing bodies
  are ignored, and reachable parse diagnostics retain their structured source/span data in stable
  discovery, span, and emission order.
- [x] **MOD-13** A stitched generic type used without its argument. The BOOT-2 probe (the stage-1
  CLI compiling its own CLI package) stopped in the lowering with
  `UnsupportedTypeDeclaration("Type 'AshesCompiler_Frontend_Syntax_Expr' expects 1 type
  argument(s) but got 0.")` (2026-09-13): `Expr` is declared without type parameters, so the
  stitched declaration got one from `collectImplicitTypeParametersFromConstructors`, whose
  `isKnownTypeName` only knew the layouts registered so far, and a constructor field naming a
  type declared later in the stitched order (`Expr`'s `TypeParameter`) read as an implicit
  parameter. Two causes, both fixed: the lowering now pre-scans every type, alias, zero-cost
  and external opaque declaration into `declaredTypeNames` before registering any (stage 0's
  `knownTypeNames`), and the stitcher keeps an `external type` declaration's compiler name bare
  (stage 0's `RenamePrivateModuleMembers` never renames one) where it used to rename the
  declaration `AshesPrivateType_...` while the constructor fields still named `LLVMValueRef`,
  which made `CopyOutRuntime`'s field an implicit parameter. Tests:
  `expectForwardTypeReferenceKeepsArity` and `expectExternalOpaqueFieldKeepsArity` in the
  semantics suite.
- [x] **MOD-14** The stage-1 lowering of the CLI package failed with
  `CoreCallTypeMismatch(TypeArityMismatch(0, 4))` reported at `Package.ash:20`, reached once
  MOD-13 closed (2026-09-13). The location was borrowed from the entry file (the stitched
  line-mapping gap); the span was `ProjectManifest.ash`'s `findField (key: Str) (json:
  ManifestJson)`, where `type alias ManifestJson = Json(Bool, Int, Float, Str)`. Two causes,
  both fixed: the core lowering never expanded type aliases, so an alias in an annotation
  became a bare nominal type with no arguments and failed to unify with the four-argument
  constructor patterns (stage 0's `ResolveTypeAlias`); `registerProgramTypes` now collects every
  alias into `typeAliases` and `expandTypeAliases` substitutes its target, parameters bound to
  the written arguments, wherever a type expression is converted (annotations, constructor
  fields, capability signatures), with a fuel against alias cycles. And `Ashes.IO.args` had no
  standard builtin layout (`UnknownLoweringBinding` in a single-file compile), added as the
  nullary `List(Str)` value. Tests: `expectTypeAliasAnnotationKeepsArguments` (an alias chain
  over a generic tree) and `expectStandardProgramArgsValue` in the semantics suite; the
  reduced programs compile with the stage-1 CLI and run correctly.
- [x] **MOD-15** The stage-1 lowering of the CLI package failed with
  `UnknownLoweringBinding("__ashes_private_external_AshesCompiler_Backend_Llvm_LLVMContextCreate")`,
  reached once MOD-14 closed (2026-09-13). Three causes, all fixed: the core lowering never
  registered a program's `external` declarations (every program-mode entry built its state
  with no external layouts), so `registerProgramExternals` now types them with
  `validateExternalProgramAbi` against a resolution context naming the program's own types,
  the compiler-provided `Maybe`, `Result` and `Unit`, and the opaque external types, and
  installs a `CoreExternalFunctionLayout` per function under its declared name (stage 0's
  `RegisterExternalFunctions`); the stitcher renamed a private module's `external` function
  `__ashes_private_external_...` while a native-string destructor reference
  (`FfiStr(owned LLVMDisposeMessage)`) kept the source name, where stage 0 hoists an external
  program-wide under its own name, so the stitcher now keeps it bare like an opaque type; and a
  nullary external called as `f(Unit)` with a spanned argument fell off the direct-call path
  into the first-class reference and its `CoreExternalDirectOnlyViolation`. Test:
  `expectExternalFunctionDeclarationLowers` (opaque type, string and nullary externals) in the
  semantics suite; the projects and cli suites are green.
- [x] **MOD-16** The stage-1 compile of the CLI package failed with `ASH002 Type mismatch: Str vs
  Result<a, b>` reported at `Main.ash:3:24433`, reached once MOD-15 closed (2026-09-13). The
  offset pointed into `Llvm.ash`'s `match LLVMCopyStringRepOfTargetData(targetData) with |
  Ok(layout) -> ...`: the self-hosted external lowering typed a copied native string
  (`CopyFfiString`) as a bare `Str`, or `Maybe(Str)` when nullable, where stage 0's
  `MaterializeNativeString` types it as `Result(Str, Str)` or `Result(Str, Maybe(Str))` (the
  `Error` carries the conversion's message) and an `out` native string always as the nullable
  form. `CoreExternalLowering.ash`'s `fromExternalAbiType` and both materializations now give
  the `Result` types; the external lowering tests assert them.
- [x] **MOD-18** The stage-1 compile of the CLI package failed with
  `UnsupportedCoreLoweringPattern("unknown constructor TypeAt")`, reached once MOD-17b closed
  (2026-09-14, 31.6 s and 13.9 GiB), in `TypeResolution.resolveTypeExpression`, which gets
  `TypeExpr` through the type selector import `import AshesCompiler.Frontend.Syntax.TypeExpr`
  and matches its constructors bare. Stage 0 normalizes a type selector into a whole-module
  import of the parent beside the selector (`NormalizeTypeSelectors`), so the module's
  constructors and other exports are in scope; the self-hosted stitcher bound the selected
  type alone and left the constructor patterns unresolved (a nullary one became a variable
  pattern). Done (2026-09-14): `addTypeSelectorImport` imports the module wholesale beside
  the selector unless the scope already imports it; the language reference states the rule;
  `tests/import_type_selector_project` and a stitching test cover it.
- [x] **MOD-19** The stage-1 compile of the CLI package failed with
  `UnsupportedCoreLoweringPattern("unknown record AshesPrivateType_AshesCompiler_Semantics_TypeInference_HandlerOperationArmDefinition")`,
  reached once MOD-18 closed (2026-09-14, 41.5 s and 14.1 GiB). A private type and its
  record constructor carry different stitched compiler names (`AshesPrivateType_` and
  `AshesPrivateConstructor_`), while an exported record's coincide; the stitcher rewrote a
  record literal's and a record pattern's name as the type, and the lowering looks both up by
  constructor name. Done (2026-09-14): `rewriteRecordName` resolves them as the constructor;
  `tests/private_record_pattern_project` (a private record built and matched in its own
  module) and a reference-rewriting test cover it.
- [x] **MOD-20** FIXED 2026-09-16: a lambda never captured a record receiver it read only as
  `receiver.field`. `receiver.field` parses as a qualified name, so a body that reads a record
  that way mentions the receiver nowhere the self-hosted free-variable walk looked — `collectFree`
  in `CoreLowering.ash` had no `ExprQualifiedVar` case at all, so the closure captured nothing and
  lowering the field access then found no binding. Stage 0 was never affected: its
  `FreeVarsVisitQualifiedVar` carries the rule explicitly, for exactly this reason. The fix adds
  the receiver as a free name; over-reporting is safe because `capturedBindings` already drops
  every name that is not a binding, which is what keeps a genuine module path like `Ashes.Text`
  out. The probe stopped on `TcoPromotionCostSignal` with
  `UnknownLoweringBinding("facts.consumedListTail")` and on `ModuleSemanticStitching` with
  `UnknownLoweringBinding("entry.modulePath")`; `TcoPromotionCostSignal` now compiles and links in
  0.9 s and 3.0 GiB, and `ModuleSemanticStitching` runs past that diagnostic into OPT-85's memory
  wall. Regression: `selfhost/tests/semantics/QualifiedReceiverCaptureTests.ash`.
- [x] **MOD-21** FIXED 2026-09-16, and the self-hosted compiler was the one in the right. The probe
  stopped on `PatternBindingOwnership` with `UnsupportedCoreLoweringPattern("unknown record
  TextSpan")` because that module matches `PatternAt(TextSpan { start = start }, inner)` while
  importing only `AshesCompiler.Frontend.Syntax`; `TextSpan` is declared in
  `AshesCompiler.Frontend.Token`, which `Syntax` imports but does not re-export. The language
  reference is explicit that there is no implicit re-export and that each importer must import what
  it needs directly, so the fix is the missing `import AshesCompiler.Frontend.Token.TextSpan`
  rather than a stitcher change. `ResultReachSummaries` relies on the same leniency at
  `ExprAt(TextSpan { start = start }, _inner)` and got the same import; a sweep of the semantics
  and formatter packages found no others. `PatternBindingOwnership` now compiles and links in
  5.2 s and 18.7 GiB. Stage 0 still accepts the un-imported use, which is a leniency worth
  narrowing on its own some day, not here.


### IR model and lowering

- [x] **IR-1** Model the complete `IrProgram` — functions, registers, locals, literals, coroutine metadata,
  ownership instructions, and stable function-origin lineage — covering all 229 instruction
  variants, source locations, task-frame ABI constants, and external/trait metadata.
- [x] **IR-2** The canonical lowered/final IR text format and deterministic function selection used by
  `--emit-ir` and compiler reports, matching stage 0's ordering, annotations, operand rules, and
  the complete 229-instruction textual vocabulary.
- [x] **IR-3** Lower constants, locals, strict left-to-right evaluation, calls, closures, captures, partial
  applications, and lifted functions, driving a whole `ProgramSyntax` (top-level items threaded
  directly; `let recursive ... and ...` groups split into member and continuation lowerers;
  duplicate top-level bindings rejected). Covered by `CoreProgramLoweringTests.ash` and the
  byte-for-byte `selfhost/tests/ir-program-parity` fixtures.
- [x] **IR-4** Lower control flow, conditions, matches, guards, recursion, mutual recursion, and tail calls
  (recursive groups predeclare monomorphic member types and share one environment; tail-position
  recursive applications stay ordinary calls at this phase — the optimization milestone owns the
  back-edge transforms).
- [x] **IR-6** Lower operators, BigInt, text/number conversions, program arguments, panic, standard I/O,
  filesystem, environment, process, networking, TLS/HTTP, regex, and other builtin operations.
- [x] **IR-7** Lower external calls, resources/destructors, native ownership conventions, library/resource
  references, and target ABI metadata.
- [x] **IR-10** Resolve a dependency module's combined-source positions through stitched item regions — the
  self-hosted stitcher combines syntax trees, so a module's spans stay offsets into its own file,
  and every emitted instruction carries the innermost enclosing `ExprAt` span. Covered by
  `MetadataAndOriginsTests.ash`. (Stage 0's re-rendered text regions needed `SourceLineAnchor`
  fragment anchors instead — recorded there.)
- [x] **IR-11** Validate lowered IR invariants (program- and function-level) and compare normalized
  lowered-IR fixtures byte-for-byte with the C# compiler
  (`selfhost/parity/semantics/lowered-ir/`).


### Optimization, ownership, and reuse

- [x] **OPT-1** Port compile-time evaluation (bounded step/depth budgets, scalar call folding) and the
  deterministic IR optimization pipeline: ownership-copy elision, RcDup sinking and RcDup/RcDrop
  fusion, known-closure devirtualization, constant propagation/folding, identity elimination and
  strength reduction, unreachable/dead-code elimination, and redundant arena-bracket stripping.
- [x] **OPT-2** Constant propagation computes a true meet-over-paths at multi-predecessor labels (one fact
  snapshot per incoming edge, intersected once all are observed; unobserved back edges clear) and
  tracks local-slot state — essential, since real lowered IR routes every `let` and join through a
  slot, so temp-only facts fold nothing. Covered by `selfhost/tests/semantics/IrOptimizerTests.ash`.
- [x] **OPT-3** Fold statically-known conditional branches and `SwitchTag`s, recomputing predecessor edges
  from the post-fold instruction list so a newly-unreferenced label dies with its body.
- [x] **OPT-4** Re-run ownership-copy elision after identity elimination/strength reduction (the identity
  rewrite introduces copies the earlier elision pass never revisits; the pass recomputes its facts
  per call, so a second run is safe).
- [x] **OPT-5** Devirtualization reaches a curried call's later applications via a whole-program
  known-returned-label fixpoint (`CallKnown` to a function proven to return one heap `MakeClosure`
  label rewrites to an env-word load plus direct `CallKnown`, iterated to a local fixed point).
  A stack closure never qualifies as a known returned label — its environment dies with the frame.
- [x] **OPT-6** Block-local common-subexpression elimination over duplicate `GetAdtField` reads and pure
  `CallKnown` calls, keyed through a LoadLocal/StoreLocal/Borrow/RcDup alias map with seeded
  env/arg-slot identities; invalidated by potential aliased writes but NOT by arena/stack
  bookkeeping (cursor moves, not writes).
- [x] **OPT-7** Store-to-load forwarding through provably-fresh allocation targets. The cached value must be
  the write's raw source temp, never its alias-canonicalized identity — a canonicalized sentinel is
  a valid cache key but crashes codegen if emitted as a value.
- [x] **OPT-8** Closure environment scalarization for one scalar capture (the captured value rides the env
  argument of a memoized `__scalarenvN` callee variant; `LoadEnv`-only callees, coroutines
  excluded), and
- [x] **OPT-9** for two captures (the second rides the free ownership-flag word), reaching let-bound local
  helpers via slot-resolved devirtualization with dead-load and dropper-free cleanup removal.
- [x] **OPT-10** Prune closure captures the lowered body never reads (`pruneDeadCaptures`): fills deleted,
  survivors renumbered compactly, environment shrunk; self-referential lambdas and
  mutual-recursion groups decline.
- [x] **OPT-11** Fold left-nested single-use string-concatenation chains into one N-ary `ConcatStrN` as the
  pipeline's last step. Single-use analysis alone is insufficient: the fold delays reads across the
  chain, so any arena save/restore/reclaim, stack-pointer bracket, or branch between the innermost
  part and the fold point declines the whole chain (a reclaim can reuse an earlier part's address).
- [x] **OPT-12** The two whole-program closure-environment passes between the per-function pipeline and
  scalarization: captured-closure-call devirtualization (every creation site stores the same label,
  settled by a fixpoint over the capture graph) and currying-stage inlining (a copy-only stage's
  chain rewritten to a caller-frame environment). These took the stitched packages from
  almost-all-`CallClosure` to mostly-direct calls.
- [x] **OPT-14** Control-flow simplification (jump threading, unreferenced-label removal, redundant
  fallthrough elision), iterated with unreachable-code elimination to a true fixed point — one pass
  cannot fully collapse a real multi-arm match cascade.
- [x] **OPT-15** Tag-grouped match compilation (`planTagGroups`/`lowerMatchArmsViaTagGroups`): arms grouped by
  outer tag, one switch, linear testing scoped inside a group. Sharp edges, each confirmed by a
  real failure: unify the scrutinee against the patterns BEFORE deciding (its type can still be a
  variable that only these patterns pin down); a group's fail target is the match's trailing
  default arm when one exists, never the exhaustiveness-failure label; and a bare `None`-style arm
  is a variable pattern syntactically — it must resolve against the constructor table or it becomes
  a catch-all that absorbs every later arm.
- [x] **OPT-16** Gate the dead-arm trim to shapes the coverage engine analyzes exactly (catch-alls, bool
  literals, empty list, constructors whose children are all catch-alls). The "Missing case" engine
  under-approximates by design — correct for a diagnostic, unsound as an unreachability proof
  (record sub-patterns contribute no constraints; per-column independence misses cross-column
  gaps), and the unsound trim deleted live arms in the semantics package itself.
- [x] **OPT-17** Ordinary and mutual tail-call optimization, stack-safety rules, and profitability/cost
  signals, with SCC decomposition and tag-based dispatch trampoline plans. Covered by
  `selfhost/tests/semantics/TcoTests.ash`.
- [x] **OPT-18** Upgrade the advisory `tail` marker to `musttail` for proven-eligible non-loop tail calls,
  gated on a whole-function scan for native stack allocations; `IrCodegen.ash` fuses direct,
  stored-to-join-slot, and fallthrough-into-join shapes through arbitrarily deep copy-forwarding
  chains (`computeTailJoins`, including an `if` join reached only by a jump that forwards into the
  enclosing `match` join), and currying-stage inlining heap-allocates a self-re-entering
  chain's environment so recursive back edges stay fusable.
- [x] **OPT-21** Infer parameter/capture ownership, result reachability and freshness, moves, borrows,
  forwarding, and whole-program SCC provenance summaries.
- [x] **OPT-22** Prove open-world inspect-only parameters as a monotone least fixpoint over every registered
  function, so in-place reuse borrowing survives a hand-off to a proven read-only helper
  (`FunctionOwnershipSummary.ParameterOwnership` cannot answer this — it classifies a plain
  inspecting helper's parameter as consumed).
  Done (`OwnershipInference.ash`): `inferProgramParameterOwnership` classifies every registered
  function's parameters as a whole-program fixpoint in stage 0's direction (the proven set
  starts empty, a parameter is promoted to borrowed once every mention is a borrow read, passes
  repeat until stable), so a hand-off to a proven inspecting helper or a chain of them stays
  borrowed, a genuine hand-off cycle never converges and stays consumed, and a shadowed,
  unregistered, ambiguous, or partially applied callee still consumes;
  `inferProgramOwnership` reports the fixpoint verdict. `CoreLowering` seeds its state with the
  fixpoint over the program's top-level functions (`withProgramParameterOwnership`) and its
  call-site borrow decision (`markCallArgumentsMoved`) overlays the proven verdict on the
  single-function summary the way stage 0's call lowering consults its proven inspect-only set:
  a parameter the fixpoint proved borrowed stays a borrow where the single-function summary saw
  a consuming hand-off, for a callee that is a registered top-level function with the classified
  parameter chain; a local lambda or a shadowing name keeps the single-function verdict.
- [x] **OPT-24** Lay out a single-constructor ADT without a tag word (payload at offset 0), the tagless flag
  carried on every ADT instruction; skip tag tests in matches, load the tag as a literal in
  synthesized droppers/copiers, and keep reuse tokens layout-exact. Build the classifier with this
  layout from the start rather than unboxing the tagged layout later. Done: `TaglessAdtLayout.ash`
  decides the flag once per type declaration (a sole constructor of arity at least one that is
  not compiler-provided, zero-cost, a resource handle, or resource-bearing through any field, type
  argument, list, or tuple) and owns the offset/size helpers the lowering and backend share;
  `CoreConstructorLayout.tagless` carries the decision, and `CoreLowering.ash` emits it on
  `AllocAdt`/`SetAdtField`/`GetAdtField` at construction, record access and update, and
  pattern-field loads, skipping the tag compare (the `ptr != 0` guard stays) and the sole-group
  `GetAdtTag`/`SwitchTag`; `IrText` prints `Tagless=true` as stage 0 does; the backend sizes the
  cell, skips the tag store, offsets fields from 0, and refuses a `GetAdtTag` of a tagless cell
  before emitting a function. Covered by `TaglessAdtLayoutTests.ash` and the tagless record,
  nested, generic, tail-loop, and nullary programs in `selfhost/tests/backend/Main.ash`. The
  synthesized droppers and copiers read the flag: field loads and stores carry it, the
  deep-copy plan of a sole-constructor type never switches on a tag, and the constructor-switching
  ADT dropper loads a tagless cell's tag as the literal 0 (stage 0's `EmitAdtTag`), checked by
  `StructuralDroppersTests.ash`. Verified (2026-09-09): `AllocReusing` is emitted, tagless flag
  included — `CoreLowering.ash`'s ordinary-match-arm reuse path (OPT-42)
  `emit(AllocReusing(resultTemp)(tag)(fieldCount)(token.temp)(token.runtimeManaged)(false)(tagless))`
  — stale by the time this note was last read; OPT-42's own entry already documented the mechanism,
  just never crossed off this sibling claim. Reuse-token layout exactness reaches list cells too
  (2026-09-09): the `f$reuse` specialization's cons arm mints a two-field token and the rebuild
  consumes it as an `AllocReusing` carrying `ListCell`, the shape stage 0 emits for the same
  program. A named-type accumulator layout followed with OPT-42's to-space materialization
  (2026-09-09), and `AllocAdtToSpace` with it: `emitFreshConstructorCell` is stage 0's
  `EmitFreshConstructorCell` `_inSpecialization` branch, so a cell a rebuild could not take from a
  reuse token is allocated in the never-reset to-space with its tagless flag rather than the arena,
  and is not marked runtime-managed. That is also what keeps the specialized body's reset safe: the
  scan rejects a plain `AllocAdt`, which could put a live part of the result above the watermark, and
  never a to-space cell. `AllocAdtStack` has its backend half too (2026-09-09):
  `emitStackAllocAdt` is stage 0's `EmitStackAllocAdt`, the same `[tag][fields...]` layout the arena
  and RC forms use over `emitStackAlloc`'s `[n x i64]` frame storage, with no tag word for a tagless
  cell; `selfhost_backend_alloc_adt_stack_e2e` writes and reads back a tagged cell through a tagless
  one across an arena bracket whose chunk is unmapped, since frame storage is not the arena's to
  reclaim. `functionAllocatesStackMemory` deliberately does NOT list it, matching stage 0's own
  `FunctionAllocatesNativeStackMemory`: a stack ADT is dead before any tail call in its frame, so
  downgrading `musttail` for it would cost a real loop its tail call. The placement decision followed
  (2026-09-09), closing the gate: `constructorExpression` is stage 0's `IsConstructorExpression`
  plus a saturation check (only a saturated application reaches `lowerConstructor`; a partial one
  builds a closure and would leave the request for whatever constructor came next),
  `immediateSingleArmDestructuringMatch` its `IsImmediateSingleArmAdtDestructuringMatch`, and
  `stackAllocatableScrutineeCases` its `ShouldStackAllocateImmediateMatchScrutinee`. The request
  rides `stackAllocateConstructor` on the lowering state rather than a parameter on every
  constructor-lowering signature, and `lowerConstructor` takes it and clears it before lowering its
  own arguments, so a nested constructor argument never claims the one its parent made. A stack cell
  consumes no reuse token and is not marked runtime-managed. Two deliberate narrowings against
  stage 0, both costing an optimization and never soundness: `exprMentionsName` is shadow-blind, so a
  pattern that rebinds the `let`'s name declines where stage 0 allows it; and stage 0 also withholds
  the placement inside a coroutine body, which this lowering has none of yet.
- [x] **OPT-27** Decide a `let`'s runtime-RC ownership from what its value temp IS, not how it is
  represented: only a fresh producer or a transferred value confers a releasable reference; a
  plain read of an RC-normalized slot is a borrowed read, and registering it as an owner
  double-releases every iteration. Regression: `tests/tco_let_alias_of_rc_parameter.ash`.
  `adoptRuntimeLetValue` registers an owner only for a `RuntimeNewlyProduced` value temp — a
  fresh producer's result, a known call marked by its callee's body placement, a copy-out, or a
  transferred read (`transferRuntimeOwner`); a parameter `LoadLocal`, a `LoadEnv`, a pattern-field
  load, and the `Borrow` of an owner's read are never marked, so `let r = acc` inside a loop
  releases nothing at its exit (`TcoOwnershipRulesTests.ash`; the regression program runs through
  the backend suite). The RC-normalized loop parameter the rule guards against arrives with
  OPT-25's parameter entry normalization. Selfhost port: `acc` itself (the parameter `r`
  aliases) is now placed runtime-managed and carries its own back-edge predecessor release and
  transfer-checked exit release (OPT-26/OPT-29's shared "runtime-managed loop parameters" tail),
  while `r`'s own reads still add neither — `TcoOwnershipRulesTests.ash`'s
  `expectLetAliasOfParameterIsNotAnOwner` now asserts exactly the parameter's own two releases
  and no duplicate from the alias.
- [x] **OPT-31** Keep a heap aggregate alive when stored through a generic parameter of a function neither
  inlined nor specialized: both call-lowering paths copy the argument into the persistent to-space
  region (the RC heap is NOT immune — it shares the arena's reclaimable cursor). Covers `Str` and
  `List(Str)`; extend on new failing shapes. Regression:
  `src/Ashes.Tests/GenericParameterHeapValueUafTests.cs`.
- [x] **OPT-32** Retain the elements a generic function (`Ashes.Collection.List.reverse`) moves from a
  consumed list into cells it builds — the generic cons allocates an arena cell around a
  type-variable head with no retain. Root cause was one call-boundary gap, not the cons cell
  itself: `GetCallCopyOutKind` recognized only a `Str` head or a list of arena-resettable elements
  and fell straight to `CopyOutKind.None` for anything else a generic function's element type
  variable can be instantiated with — a tuple, a named record/ADT, `Bytes`, `BigInt`, or a nested
  list over one of those — so a generic function's returned list of such elements escaped the call
  with no normalization at all, in place of the retain a monomorphic accumulator's own TCO
  parameter-entry normalization already performs for the identical element shape via
  `EmitRuntimeManagedTcoListDeepCopy`. Fixed by routing that already-proven recursive per-element
  deep-copy machinery through the call-result path too (`CanEmitRuntimeManagedListElementDeepCopy`,
  `LowerUncoveredCallResultCopyOut`, `LowerCallDeepCopyOutListResult` in `Lowering.cs`/
  `Lowering.Ownership.cs`), both for the immediate and the type-inference-deferred copy-out sites.
  Regression: `src/Ashes.Tests/GenericListRetainsRuntimeManagedElementsTests.cs` (an IR-level
  assertion that the deep-copy walk now appears at the call site, plus a churn-loop execution
  test) and `tests/generic_reverse_retains_runtime_managed_elements.ash`. Two consequences of
  that copy now owning what was arena-placed before, both caller-side in `LowerCallFinish`: a
  call's consumed runtime-managed arguments are released only after its result is normalized
  (the deep copy of `append(map(f)(xs))(map(f)(ys))` read records the release had already
  freed through the callee's arena cells), and a deep-copied generic result consumed by a callee
  whose result stays in its own region (neither normalized here nor produced runtime-managed)
  and whose ownership summary is poisoned is not released at all — that callee may have borrowed
  the records' strings into a region that outlives the call (the self-hosted lowering's
  `finishMatchArm` stores an `ArmOwner` type name into an emitted `CleanupResource`), exactly as it
  could when the list was arena-placed, so the copy is left with the callee the way its arena
  predecessor was (`ConsumedDeepCopiedListStaysWithCallee`). Regression:
  `ConsumedArgumentsReleasedAfterResultNormalizationTests.cs`,
  `tests/generic_append_of_generic_map_results_releases_after_copy.ash`,
  `tests/generic_result_consumed_by_opaque_callee_keeps_parts.ash`. The same release rule also
  trusted a result-reach summary that `Lowering.MoveAnalysis.cs` had left empty: a record update
  and a dotted field read of a local (`x with f = v`, `x.f`, the latter parsed as a qualified
  name) were unmodeled and poisoned with no parameter in the reach set, so a fresh
  reference-counted argument a callee embedded through `with` was released by the caller right
  after the call and its block reused by the next allocation of the same size. Both are modeled
  now: the update's target and a field read reach the result through a component, the updated
  values whole (`OwnershipProvenanceTests.cs`).
- [x] **OPT-33** Check an inlined helper's references transitively before inlining it inside a reuse arm or
  specialization (a helper's own body must resolve in the isolated scope too; an already-visited
  helper counts as resolved). Regression: `ReuseInlineResolutionTests`. Not yet applicable to
  selfhost: OPT-42's ordinary match-arm reuse path never inlines a helper call into an arm (no
  `InlineCall`/`_inliningInProgress` family is ported), so there is nothing for this check to gate
  yet — it becomes relevant once helper inlining or fold specialization lands.
  Done: helper inlining landed with OPT-48, and its `inlinedReferencesResolveHere` walks a
  candidate's free names transitively (lexical binding, constructor, a helper already accepted
  on the walk, or an inlinable helper whose own references resolve in turn). Pinning it against
  stage 0 found three divergences, all closed: a top-level function the caller never captured
  now resolves by label the way stage 0's `_topLevelFunctionRefs` does (`topLevelFunctionRefs`,
  recorded at a top-level `let` whose closure has an empty environment and rebuilt with a null
  environment by `lowerTopLevelFunctionReference`, also accepted by the reference check); the
  runtime-managed temp facts are now cleared when a lambda's body begins
  (`prepareLambdaBodyState`), since an outer function's temp numbers leaked into a spliced
  helper's nested loop and gave its closure a returns bit stage 0 does not set; and the forced
  retain flag of a pending call argument is reserved for a pattern binding extracted from a loop
  parameter (`patternBindingArgumentRootSlot`), not a read of the parameter itself, as stage 0's
  `TryGetRuntimeManagedPatternBindingArgument` has it. The whole-program parity fixtures
  `inlined_helper_chain_under_back_edge` (a helper chain spliced under a back edge),
  `inlined_helper_sibling_by_label` (a spliced helper calling an uncaptured top-level function),
  `inlined_helper_sibling_spliced` (a helper whose sibling is spliced in turn), and
  `helper_call_without_inline_trigger` (the same helper left as a call outside any trigger) pin
  the decisions.
- [x] **OPT-34** Admit a tuple whose elements include a list of records to runtime-RC placement, or retain
  rather than clone the string elements of an escaping arena tuple — threading a large string
  through such a tuple currently deep-copies it per rebuild (the self-hosted parser moved to a
  `Bytes` view to sidestep this; the general cost remains). Shipped: the per-rebuild copy came
  from `MaterializeEscapingArenaTupleElements` cloning every string binding placed into an
  arena-shell tuple, including one bound out of the borrowed parameter, whose release is an arena
  identity marker. Stage 0 now clones only a string whose owner really releases it (a
  runtime-managed let or match owner, a stable pattern owner, or an untracked binding); a
  borrowed parameter part is carried as is, since the parameter outlives the call and the call
  boundary copies the escaping result out as a whole. A 128 KB state string threaded through
  20000 rebuilds went from 0.49 s to under 10 ms; the shared
  `tests/escaping_tuple_borrowed_state_string.ash` runs the carried and the still-cloned shapes
  through both backends. The self-hosted lowering never had the clone. Admitting the tuple itself
  to runtime-RC placement (a list-of-records element needs a synthesized list dropper over record
  heads and a runtime-managed cons for the rebuilt list) stays open as a later placement widening.
- [x] **OPT-35** Retain, rather than copy, a borrowed string returned out of an aggregate parameter when the
  caller can prove the aggregate is reference-counted (accessor shape:
  `Borrow` + `CopyOutArena RcNormalization` copies the whole string per call). Root cause: an
  accessor's own compiled body (`let name (p: Person) = p.name`, a bare `GetAdtField` + `Return`)
  never allocates anything RC, so its per-callee `ReturnsRuntimeManaged` bit is `false`, baked once
  into every closure value built for that function (`LowerVar`'s top-level-function-ref case) — it
  can never reflect what a *specific call site's* own argument is, so `CopyOutArena
  RcNormalization` ran on every call regardless. Fixed with a call-site-local decision, not a
  change to the callee's bit: `IsTrivialParameterFieldAccessorBody` (`Lowering.cs`) recognizes the
  callee's body shape (a bare `LoadLocal`/`[Borrow]`/`GetAdtField` of its own single parameter,
  restricted to a Shallow-copyable field); `ClassifyAccessorArgumentRc` classifies the call site's
  own argument as `NotRc` (unchanged copy behavior), `DefinitelyRc` (a fresh temp, or a named
  binding whose own `OwnershipInfo` already says `RuntimeManaged` — the same per-name fact OPT-27's
  let-ownership rules read), or `PendingTcoSlot` for a self-recursive loop's own parameter, whose
  arena-vs-runtime-RC placement is not settled this early in lowering (`TryGetRuntimeManagedCallArgument`'s
  own documented timing) — resolved later by `FinalizeAccessorResultRetains`, an `RcDup`-marker
  mechanism mirroring `TcoParameterAggregateRetain`. Verified: a 200000-call loop over a `Person`
  sourced from a genuinely RC-placed `List(Person)` (the TCO parameter-entry path places it RC)
  went from 15.06s / 38.6M minor page faults (the old unconditional copy of a ~768 KB field) to
  0.06s / 4K page faults after the fix — a ~250x improvement — with RSS materially unchanged (the
  per-iteration arena bracket already reclaimed the copy either way; the win is the eliminated copy
  work, not peak memory). Regression:
  `src/Ashes.Tests/AccessorRetainsRcAggregateFieldTests.cs` (an IR-level assertion that the call
  site emits a forced-true ownership flag followed by an `RcDup ... RuntimeManaged=true` in place
  of the old packed-word bit-63 read, plus a correctness execution test) and
  `tests/accessor_returns_retained_string_from_rc_record.ash`.
- [x] **OPT-36** Keep a large string alive when a tail-recursive loop moves it from the list (or tuple state)
  it consumes into its accumulator — the consumed cell's release frees the moved element, read
  back freed for any string past one arena chunk. Repro: split a 15 KB line, walk it inline
  consing the lines, join the result. Closed by the consumed-argument release rule: a fresh list
  consumed by a callee whose result reaches its parts is released spine-only in the caller
  (`LowerCallDropConsumedRuntimeArguments`, the child-preserving walk), so the moved lines stay
  alive for the join. Regression: `tests/tco_loop_moves_split_line_into_accumulator.ash` (three
  5 KB lines split inline, walked, then read back after 20000 unrelated allocations).
- [x] **OPT-37** Release a TCO loop's aggregate result in its caller when the exit arm builds an ADT from the
  loop's own runtime-managed accumulators — the shell is recognized as runtime-manageable when its
  field is the enclosing loop's own parameter slot (narrowly — not any outer variable).
  Regression: `LinuxBackendCoverageTests.cs` (mechanism + RSS-plateau behavior tests).
- [x] **OPT-38** Release a plain runtime-RC value extracted by a match pattern and passed by name as a TCO
  back-edge argument: argument evaluation retains it for the successor, so the back edge must also
  release the pattern-bound owner's reference — it is not a moved value and must not follow the
  moved-argument rule written for resources. Only a plateau-over-iterations test catches this
  class (confirmed 2.4 GB → 8.2 MB on fannkuch-redux).
  Selfhost port: `let recursive f n xs last = match xs with | [] -> last | head :: rest ->
  f(n - 1)(rest)(head)` used to store the raw pattern-extracted `head` into the runtime-managed
  `last` slot with no retain while the back edge released the predecessor `last` — an
  over-release rather than the leak first suspected: with `xs: List(Str)`, the list's strings
  were freed while the list still held them (a use-after-free the selfhost-compiled
  `tests/tco_pattern_head_forwarded_to_other_parameter.ash` crashed on). Closed by porting stage
  0's pattern-binding ownership: `PatternBindingOwnership.ash` classifies every binder a `match`
  extracts from a loop parameter by its uses (borrowed by a plain call, transferred to its own
  parameter, embedded in an aggregate, forwarded to another parameter or captured, or
  unclassified), keyed by the binder's span; `CoreLowering.ash` binds a protected binder as a
  pattern owner (`CoreBinding.patternOwner`) whose reads always borrow, records a placement site
  after the arm's pattern, takes an identity `RcDup` where the binding is read into a tail
  self-call argument, a cons, a list literal, or an owning constructor, releases it at the arm exit
  under its resolved type name (`PatternBinding` while unresolved), and at the loop's finalize
  promotes the markers of a binder whose root parameter is runtime-managed or whose own type is
  `Str`/`Bytes`/`BigInt` to real retains and releases and splices stage 0's protective duplicate
  right after the pattern (`PatternBindingOwnershipTests.ash`, `TcoOwnershipRulesTests.ash`).
  A runtime-managed root with an aggregate-typed escaping binder names the structural dropper
  on the promoted release, and the consumed-tail guard over escaping heads is gone (see OPT-25's
  note).
- [x] **OPT-39** Release the RC-managed result of a call consumed only by a read-only builtin once nothing
  else owns it. Three facts must stay consistent: the release fires only for freshly-produced
  arguments; an if/match join keeps "newly produced" only when every arm was fresh; a let-scope's
  save/reload preserves the fact across the reload. Needs a long-running plateau test. Shipped:
  stage 0 releases the consumed result in all three shapes and its
  `Linux_backend_llvm_read_builtin_consumed_call_result_memory_should_plateau` test holds the RSS
  flat over 200000 iterations; the self-hosted lowering emits the same `RcDrop` after the read
  for a direct call result and for an `if`/`match` join of fresh branches, keeps a join with a
  borrowed branch and a let-owned result unreleased (the owner's scope-exit drop covers it), and
  the shared `tests/rc_release_read_builtin_join_result.ash` runs all four shapes through both
  backends. The self-hosted binaries plateau too since the backend places `ConcatStr`/
  `ConcatStrN`/`TextFromInt` results by their runtime-managed flag (CG-4): the direct, join,
  let-scope and discarded-result probes run 200000 iterations in 5.6 MB of RSS, down from 55 to
  190 MB when every string result was a `malloc` that nothing freed. `Text.uncons`/`unconsText`
  results are placed by the same flag: an escaping runtime-managed result owns copied strings, a
  reference-counted tuple, and a reference-counted option cell, while the immediate arena result
  keeps zero-copy views over the scrutinee's bytes (`emitArenaStringView`, stage 0's
  `EmitStringView`) with its tuple and option cell in the arena. `Text.length` walks a string by
  `unconsText` in a non-tail recursion, so a 15 KB string cost 121 MB of tail copies held by
  the nested frames and now costs 9 MB, and
  `tests/tco_loop_moves_split_line_into_accumulator.ash` runs in 11.6 MB instead of 124 MB.
  That recursion also overflowed the stack — the self-hosted `fmt` segfaulted on every source
  above roughly 90 KB, stage 0's own binaries above roughly 250 KB — so `Ashes.Text.length`,
  `take`, and `drop` now walk the bytes with an index (`countCodepoints`, `cpByteOffset`) and
  slice; regression `tests/text_length_take_drop_large_string.ash`. That fix and the
  optimization-level one below raised the threshold without removing it: `fmt` still segfaults on a
  241 KB source, from a cause neither of them addressed. Tracked as OPT-67 — do not read this item
  as having closed `fmt` on large files. What remained was the frame
  size: this backend emitted at LLVM optimization level none, so a call frame was about four
  times stage 0's, and `Collection.List.map`/`filter`/`append` (one frame per element by design,
  the shape the reuse optimizer relies on) overflowed the 8 MB machine stack at about 100000
  elements against stage 0's 400000. Fixed (2026-09-11): optimization-level selection landed —
  the backend now runs LLVM's own `default<O2>` pass pipeline (`OPT-58`'s neighbor; see
  `project_selfhost_gaps_found_task3` session memory for the original bisection). Measured
  directly: the same non-TMC `List.map` shape now survives to roughly 170000 elements, matching
  stage 0's own threshold, up from roughly 30000 before. Landing it surfaced and fixed a genuine,
  separate linker bug on the way (read-only sections joined at a fixed 16-byte boundary regardless
  of what LLVM's optimizer actually needed, faulting the first time the SLP vectorizer folded a
  string literal into an aligned 32-byte vector load — rodata joins are now 32-byte aligned) plus
  two missing libc symbols (`bcmp`, `memset`) LLVM's idiom recognition can introduce even when the
  codegen itself never calls them directly.
  Still open: the self-hosted `fmt` still faults on the three sources whose token lists were
  previously blamed on this same gap (`TypeInference.ash`, `ProgramInference.ash`,
  `OwnershipInference.ash`) — confirmed directly (`TypeInference.ash` still segfaults) even with
  the optimization pipeline now running, so `fmt`'s own stack cost has a further, separate cause
  not covered by this fix (plausibly the parser/formatter's own recursive-descent shape rather
  than the one-frame-per-element `List.map` pattern this fix specifically measured) — not yet
  investigated.
- [x] **OPT-41** Normalize complete graphs and insert deep-copy boundaries where region or ownership rules require
  them. Done (2026-09-06): a generic callee's deep-copied list result shares nothing with the
  call's consumed arguments, so stage 0 releases them with their elements after the copy instead
  of spine-only (`resultDeepCopied` through `LowerCallRestoreArena` to
  `LowerCallDropConsumedRuntimeArguments`); `tests/generic_append_map_churn_plateau.ash` leaked
  every record and string once per iteration before (32.8 MB at 200000 iterations, 8.2 MB at
  20000). Done (2026-09-06, both compilers): the arena-result boundary. A generic body applying
  a closure parameter has no static layout for the result, stores it wherever an arena value
  goes, and its own caller deep-copies the whole result out, so a reference-counted result
  returned into it was never released (a `map` over a function returning a fresh string or
  record leaked one value per element). Such a call site now sets bit 1 of the hidden ownership
  word (`RequestsArenaResult`, a type variable anywhere in the application's result type), and a
  callee whose result is reference-counted honors it in its epilogue
  (`LowerLambdaCoreNormalizeRequestedArenaResult` / `normalizeRequestedArenaResult`): the
  result is deep-copied into the arena (`CopyOutPurpose.ArenaResultBoundary`) and the original
  released; the entry normalization masks bit 0 for its own flag. Beside it, a string parameter
  the entry normalization owns because the result always reaches it now counts as a fresh owned
  child of the record storing it (`IsNormalizedAlwaysReturnedStringParameterRead`), so that
  record is placed on the RC heap and releases the owned copy with itself; the selfhost decides
  the normalization before the body is lowered for the same reason (`withNormalizedAlwaysReturnedParameter`)
  and computes the function-body request against the prepared body state, not the outer one.
  A function whose body result is a plain read of that normalized parameter (`identity (s: Str)
  = s`) returns the owned value on both flag paths, so both compilers count the body result as
  runtime-managed for a plain function (`ReturnsNormalizedAlwaysReturnedParameter` /
  `adoptNormalizedParameterResult`): the closure carries the returns bit beside the accepts
  bit, callers adopt the value instead of copying it, and the epilogue honors a generic
  caller's arena-result request, which `apply(identity)(fresh)` through a parameter function
  had been missing (512 bytes leaked per call in both compilers;
  `tests/runtime_rc_normalized_parameter_returned_through_parameter_function_plateau.ash`).
  The churn fixture is flat at 8.2 MB (stage 0) and 5.6 MB (selfhost) at both 20000 and 200000
  iterations. Done (2026-09-06): the same loop over a string-returning function
  (`tests/generic_map_string_churn_plateau.ash`) grew from 8.2 MB to 12.3 MB, because the
  conditional list copy-out with string heads copies the heads on its arena branch while the
  consumed first argument was released spine-only on both branches; the release now follows the
  result's branch (`EmitConsumedArgumentDropByResultBranch` / `emitConsumedArgumentDropByResultBranch`:
  spine-only on the owned branch, with the elements on the copied one), and stage 0 also
  releases the elements outright after an unconditional head-copying list copy-out (the selfhost
  has no unconditional call copy-out yet, so only its conditional case applies). The parity fixture
  `consumed_string_list_copied_release` covers the branch in both compilers. Done (2026-09-06):
  the string fixture still grew in the selfhost (9.4 MB to 43 MB) because its result-reach
  analysis was a per-function approximation: a plain-name call summed the callee's and the
  argument's reach (so the argument read as reached whole), a `let recursive` name was bound to
  its analyzed body instead of the nested function being registered, and an unbound name was
  bottom instead of poison, so `append`'s summary said its result keeps both parameters whole
  (stage 0: `left` by component, poisoned) and both fresh `map` results handed over without a
  retain or release. `ResultReachSummaries.ash` now ports stage 0's `MoveAnalysis` result reach:
  a registry of every let-bound function, nested ones included, with the functions in scope of
  its body (sequential top-level scoping, recursive groups, parameter and pattern shadowing), the
  Map.set shape registered over its outer parameters plus accumulator with the inner self-call
  resolved against the enclosing summary, a least fixpoint from bottom, call-site substitution
  of the callee's stored summary (whole roots versus `name/*` components, multiplicity capped at
  two with internal-sharing and path-ancestor poison, over-application inlined one level over
  argument markers), constructor and record fields skipping copy-typed scalars, sole nullary
  constructors, dotted field reads through named sub-cells, and poison for free names, lambdas,
  pipes, handlers, and unmodelled calls. The lowering resolves a callee's summary by name and
  lambda identity (`letLambdaIdentities`), taking the registered parameters and body, and the
  decision snapshot lists nested functions with their qualified names. The string fixture is flat
  at 5.6 MB and the ownership report of the append probe matches stage 0 line for line;
  `aggregate_children_retain`'s ownership explain fixture matches and its known difference is
  retired. Done (2026-09-06): the selfhost suppressed the returns-bit read for every call to the
  enclosing recursive binding (the backend fuses a self call and its return into a loop, which a
  copy-out block between them would break), so a self call outside tail position
  (`bang(head) :: stamp(tail)`) kept its string-list result in an open window and placement
  never protected the pattern-owned tail across it. The consumer request now carries the tail
  position of every function body (`tailCall`, stage 0's `InTailPosition` without the loop
  condition), the suppression applies to tail self calls only, and the closure a self reference
  rebuilds carries its environment size in bytes rather than its capture count; parity fixture
  `non_tail_self_call_list_result`. Done (2026-09-06): a `match` on a fresh reference-counted
  call result made its scrutinee owner only in arms binding nothing heap-typed (the selfhost
  assumed stage 0 aliases a bound field to the owner and hands the owner over), so an arm binding
  a string head (`head :: _ -> print(head)`) never released the list, and one returning the head
  would have released it under the result once the owner existed. Every arm now adopts the owner
  (stage 0's `TrackRuntimeManagedMatchScrutineeOwner`), and an arm whose result is a heap value
  its pattern bound out of the scrutinee retains it and releases the owner right there through
  its structural dropper (`transferScrutineeChildResult`, stage 0's
  `EmitRuntimeManagedParentFieldTransfer`), the dropper's instructions carrying the match's
  location as stage 0's do; parity fixtures `match_fresh_scrutinee_owner_release` and
  `match_fresh_scrutinee_head_returned`. Done (2026-09-06): the self-hosted lowering infers as it
  lowers, where stage 0 lowers against the whole program's finished inference, so a call whose
  result type a later expression of the same body resolves (a self call under a string
  concatenation, `"a" + go(n - 1)`, whose result only the concatenation pins) still saw a type
  variable at the call: it asked the callee for an arena result and skipped the returns-bit read
  and the copy-out stage 0 emits. The lowering now records every call whose result type was
  unresolved when it was lowered (`unresolvedCallResults`), and a function body that finishes
  with such a type resolved is lowered a second time from its entry state
  (`lowerFunctionBodyResolvingCalls`) carrying only the finished substitution and variable
  supply, so counters, labels, and lifted functions are numbered as in one pass over resolved
  types; a body whose call results stay unresolved (a generic function's own parameter) keeps
  the single pass, and compile time of the larger tests is unchanged. A recursive member's
  epilogue now also honors the arena-result request (`normalizeRequestedArenaResult`) as a
  plain lambda's does. Parity fixture `self_call_operand_string_result`. Resolving the type
  exposed a second gap the arena path had hidden: the heap-typed bindings a pattern bound out of
  a fresh reference-counted scrutinee (`library :: reversedSymbol` over a `reverse` result) were
  read as borrows nothing owned, so a constructor or call carrying one past the arm's release
  stored the freed string (`tests/escaping_tuple_nested_adt_lifetime.ash` printed the other
  call's library). Stage 0 aliases such a binding to the arm's scrutinee owner; the self-hosted
  lowering now records the same aliases (`runtimeOwnerAliases`, `aliasArmBindingsToOwner`) and
  resolves them wherever a live owner is looked up (`liveRuntimeOwnerSlot`,
  `namesRuntimeOwner`), so the transfer retain, the aggregate child retain, and the call
  argument hand-off treat the binding as the owner's value. Done (2026-09-06): a self call
  passing a loop parameter, or a pattern binding extracted from one, whose placement the
  finalize pass still decides (a list walk's `1 + count(tail)`) now takes stage 0's pending
  argument-retain skeleton: the callee's accepts bit is read and the argument retained under it
  (or under a forced flag when the callee's result may keep the binding), the flag is registered
  under the parameter's slot (`pendingRuntimeArgumentFlags`), and finalize rewrites the flag's
  definition to zero for a parameter the frame keeps in the arena
  (`resolvePendingArgumentFlags`, stage 0's `ResolvePendingRuntimeArgumentFlags`). Every such
  argument stays pending until finalize: the per-slot admission the body can read
  (`tcoAdtSlotAdmitted`) is still demoted with the frame, and handing an arena value over under
  the accepts bit let the callee reuse it in place (`tco_deep_adt_accumulator` rebuilt its count
  list on top of the loop's own). Parity fixture
  `tco_non_tail_self_call_in_operator_operand` is registered in the runner and its six loops
  compared in `TcoLoopLoweringTests`. Done (2026-09-06): a closure capturing a string, bytes, a
  big integer, or a list over scalars now gets its environment normalizer and closure dropper
  when its closure is emitted, as stage 0 synthesizes them (`recordClosureNormalizer`,
  `synthesizeClosureDropper`): `lambda_N$env_normalize` copies each such capture out into the
  escaping environment (`CopyOutArena`, `CopyOutList`) beside the word copies of the scalars and
  returns the address of the `__rc_cdrop_N` dropper, synthesized once per owned-capture layout
  and taking a lambda id like any lifted function, which walks each owned capture. A closure
  whose captures include a still-unresolved type keeps the deferred scalar-only decision; one
  capturing a tuple, a multi-constructor named type, a list over heap elements, or a function
  gets none, as in stage 0 for the latter. A sole-constructor named capture (2026-09-07)
  qualifies: a scalar-field cell copies out by its size, a cell with owned children copies out
  and re-establishes each owned child by its own kind behind the temp stage 0's deep-copy
  emitter burns (`AdtCaptureCopy`), and the dropper releases it through the unique-guarded
  walk. Parity fixture `tco_list_walk` joins the runner. Still open
  (cosmetic): the selfhost attaches no source
  location to instructions synthesized outside any located expression (a curried stage's
  closure construction, epilogue blocks), where stage 0 attaches the declaration span. Done
  (2026-09-06, both compilers): an un-annotated parameter missed the pre-body decision in both
  compilers, since both interleave inference with lowering and the parameter's type resolved
  only inside the body: `let toEntry n = Item(name = n, flag = true)` normalized its argument
  after the body but kept the record in the arena, orphaning the owned copy
  (`tests/generic_append_map_churn_unannotated_plateau.ash` grew from 8.2 MB to 20.5 MB in
  stage 0 and from 7.4 MB to 24 MB in the selfhost at 200000 iterations). Before the body is
  lowered, an un-annotated parameter whose type is still a variable now takes the declared type
  of the first constructor field it is passed to directly anywhere in the body (the bare
  parameter as a record literal field or a positional constructor argument, outside any binder
  that shadows it) when that field holds a string or a named type
  (`LowerLambdaCoreSeedParamTypeFromConstructorFields` / `seedParameterFromConstructorFields`),
  the unification the body would perform later; the fixture is flat in both compilers and the
  un-annotated builder lowers exactly as the annotated one (parity fixture
  `unannotated_parameter_record`). Done (2026-09-06, selfhost): the generic list deep-copy
  call path. A known callee whose declared scheme yields a list over one of its own quantified
  variables (`mapAll(toItem)(xs)`) builds its result for every instantiation alike, so a
  result over records has no call copy-out and stayed in an open window in the selfhost. The
  call now closes its window through the per-element deep copy the loop entry normalization
  already uses (`emitCallDeepCopyOut`, stage 0's `LowerCallDeepCopyOutListResult`: the
  `rc_normalize_list` walk, unconditionally when the call read no returns flag, else on the
  flag's arena branch), the result is recorded as deep-copied (`genericDeepCopiedListTemps`),
  the consumed arguments are released with their parts after it, and a consumed list an earlier
  deep copy produced is left with a callee whose result reach is unknown when that result was
  neither copied out nor produced reference-counted (stage 0's
  `ConsumedDeepCopiedListStaysWithCallee`). A call result whose type still holds an unresolved
  layout also takes the slot and temp stage 0's deferred copy-out placeholder takes, so a
  generic body's numbering matches. The churn fixture's loop matches stage 0 apart from the
  inlined `List.length` entry helper (not ported), and `CallWindowLoweringTests` pins the entry
  function of parity fixture `generic_list_result_deep_copy` line for line (the whole program
  stays out of the runner until the synthesized copier and epilogue carry stage 0's locations).
  Closed (2026-09-08): the theorized gap — a borrowed `Str`/`Bytes`/`BigInt` part of a parameter or
  pattern binding stored into a constructor argument with no compensating retain — does not
  reproduce in stage 0: every admission rule that lets a constructor result count as a fresh
  runtime-RC value (`CanRuntimeManageFreshHeapChildAdtConstructorApplication`,
  `CanRuntimeManageFreshOwnedChildExpression`, `IsRuntimeManageableFreshGenericPayload`) requires
  the field to be a proven producer (`IsRuntimeRcStringProducer`/`IsRuntimeRcBigIntProducer`/
  `CanMaterializeOwnedBytes`) or the narrow `IsNormalizedAlwaysReturnedStringParameterRead` case; a
  bare borrow never passes, so it never reaches a store site that needs a retain — confirmed with
  targeted probes (an ordinary function's field projection, a TCO loop's dot-field read, and a TCO
  loop's pattern-match-destructured field, all storing a non-literal string into a sibling
  constructor and consed across 300000 iterations) that stayed either correctly arena-classified or
  already retained through `RetainRuntimeManagedTcoConstructorArguments`/the pattern-binding
  bind-time retain, in both compilers.

  The self-hosted mirror's actual, verified divergence was one level down, in
  `PerceusLifetimePlacement.ash`'s general Perceus dup/alias pass (the same machinery OPT-25
  covers): stage 0's `PerceusLifetimePlacement.cs` collects every arena- or stack-allocated ADT
  cell once per function (`CollectArenaAdtCells`) and uses it in three places — `AddCallDups` skips
  the compensating `RcDup` for a tracked owner's field store into such a cell (an arena cell embeds
  the owner's reference without a reference of its own, so the owner's own release is the only one
  that ever fires), `PropagateAlias`'s `SetAdtField` case extends the owner's tracked aliases
  through such a cell (so a later read of the cell keeps the owner artificially live, the same
  "relies on the borrowed value's owner outliving the region" rule OPT-32's release side already
  leans on), and `PlaceOwner` empties that exemption when a `CopyOutArena`/`RcNormalization` alias
  carries the cell across a tail-call back edge (a by-name arena successor's copy releases its
  children as owned references, so that store needs the retain after all). The self-hosted
  `PerceusLifetimePlacement.ash` had none of the three: `arenaAdtCells` did not exist, so its
  `callDupsAfterLoad`'s `SetAdtField` case (and `propagateAlias`'s, which had no `SetAdtField` case
  at all) treated every field store alike regardless of the target cell's own placement. The
  observable effect is a permanently unbalanced `RcDup` lifetime marker (currently a no-op at
  codegen for a non-runtime-managed value, since only a `RuntimeManaged` `RcDup`/`RcDrop` pair does
  real reference counting — a full-cost divergence once arena values gain their own runtime
  bookkeeping) every time an owner-tracked value's field is stored into an arena cell, which is a
  common shape (any record- or tuple-update building a fresh arena aggregate out of a still-live
  local). Ported `arenaAdtCellTarget`/`collectArenaAdtCells`, the `arenaAdtCells`-gated skip in
  `callDupsAfterLoad`, the `SetAdtField` case in `propagateAlias`, and the
  `arenaCellCopyOutReleasesOwner`/`borrowingArenaCells` back-edge exception, threading
  `arenaAdtCells` through the whole owner-placement call chain
  (`placeInstructionLifetimesIn`→`placeOwnerSlots`→`placeOwner`→`placeOwnerRegion`→
  `placeOwnerBetween`→`placeOwnerInRegion`→`collectOwnerAliases`/`collectInsertions`→`callDups`).
  `PerceusLifetimePlacementTests.ash` gained
  `expectRecordFieldStoreIntoArenaCellIsNotDuplicated` (fails on the pre-fix code: an unwanted
  `RcDup` appears before the `SetAdtField`) and
  `expectRecordFieldStoreIntoRuntimeManagedCellIsDuplicated` (the contrasting case, unchanged by
  the fix) built directly on synthetic IR, the same idiom as
  `expectBorrowedCallArgumentIsDuplicated`. The whole-program parity fixture
  `reuse_shared_falls_back` exercised the case end to end and needed its checked-in `.ir`
  regenerated (one fewer `RcDup`, `temps=36` → `35`). All ten selfhost test suites, the full C#
  suite (2553/2553), the LSP suite (72/72), and the e2e suite are green.
- [x] **OPT-42** Detect top-cell freshness and uniqueness, synthesize structural droppers, and implement safe
  allocation reuse for tuples, ADTs, closures, and tail-recursive paths. Done: a first consumer of
  `HeapLayoutClassification.ash`'s reuse-eligibility flags for the ORDINARY (non-TCO,
  non-specialization) match-arm path — `ReuseSpecialization.ash` (the pure Expr/Pattern-shape
  analysis: `reusePatternConstructorArity`/`reusePatternFieldBindings` extract a matched
  constructor's field-index-to-bound-name map, `reuseArmBodyRebuildsSameConstructor` recognizes a
  same-name, same-arity rebuild through nested `let`s including a field-order-projected record
  literal, `reuseTransferredFieldsSafe` checks every pointer-typed field is passed straight through
  unchanged rather than dropped or replaced, `exprMentionsName` is the shadow-blind dead-cell
  check) plus the state-threaded `CoreLowering.ash` hooks: `withReuseScrutinee` gates a whole match
  (exhaustive, guard-free, every case a distinct constructor of one type, every case's own rebuild
  transfer-safe — narrower than stage 0's cross-constructor reuse by requiring every arm, not only
  the ones with pointer fields, to rebuild its own matched constructor, so a produced token is
  always consumed by construction and the unconsumed-token release path stage 0 needs never
  arises), `reuseTokenIfEligible`/`reuseTruncateArmTokens` publish and bookkeep one `DropReuse`
  token per arm, and `allocateOrReuseConstructorCell`/`reuseEmitTransferredChild` consume it with
  `AllocReusing`, branching a transferred pointer field on the token's own runtime nullness exactly
  like stage 0's `EmitRuntimeReuseTransferredChild`. Verified: 16 existing whole-program parity
  fixtures re-checked byte-identical (no regression), 19 unit tests in
  `ReuseSpecializationTests.ash` covering the analysis functions directly, and a stage-0 oracle
  pair (`reuse_record_update.source`/`.ir`, `reuse_list_map.source`/`.ir`) that DOES emit
  `DropReuse`/`AllocReusing` for this exact mechanism, plus a `reuse_shared_falls_back` pair where
  stage 0 correctly emits neither (the scrutinee is provably shared by a second top-level binding).
  Activated end to end (2026-09-05): the three oracle pairs are registered in
  `ir-program-parity/Main.ash` and match byte for byte (the runner drops stage 0's `trait
  evidence` section, which trait lowering does not produce until milestone 3). The gate is stage
  0's `TryGetRuntimeManagedReuseScrutinee` rather than a runtime temp: the scrutinee must be a
  `let` binding still owning its reference-counted value (`liveRuntimeOwnerSlot`), every arm
  guard-free and leaving the cell dead, and the match a transfer-safe rebuild; the owner is then
  released into the arms' tokens (`withReuseScrutinee`), so no scope-exit release or arm adoption
  competes with `DropReuse`. Feeding it, four placement gaps closed: an ordinary `let` body now
  carries stage 0's `LowerEscapingResult` request (`escapingLetBodyRequest`, chain-aware like
  `LowerSequentialBindingChain`, applied to the remaining program body for a top-level `let`); a
  self-recursive copy ADT `let` matched immediately by a reuse-safe rebuild is placed on the
  reference-counted heap (`isImmediateSafeAdtMatchUse`, stage 0's
  `RuntimeReusePointerFieldsAreSafe` branch, with positional constructors matched by arity); a
  bare nullary constructor keeps the consumer's aggregate request (`aggregateRequestForwards`,
  stage 0's `LowerNullaryConstructor`), so a runtime-managed parent's `Nil` field lands beside
  it; and `let y = x` over an owned binding is an alias the original owner alone releases
  (`letAliasesOwnedBinding`, stage 0's `TrackLetOwnership`). A live token is consumed whatever
  the rebuild's own placement request, its transferred pointer child read without a borrow
  (`reuseTransferredNames`, stage 0's alias to the dead scrutinee owner) and guarded on the
  token's runtime nullness. The TCO-loop-native ARENA direct-reuse mechanism
  (`LowerLambdaCoreScanDirectReuse` and `CollectCtorMatchedScrutinees`'s constructor-pattern-only
  scan) is ported under OPT-25's aggregate tail (2026-09-07: linear reuse roots, arena tokens,
  the no-structural-reuse revert, and the move-safe entry-copy elision; see OPT-25's note and
  `tco_variant_parameter_reused_in_place`). Corrected (2026-09-09): `RcIsUnique` and structural
  droppers are NOT part of the remaining gap — both are already fully ported end to end
  (`RcIsUnique`'s IR kind, backend codegen `emitRuntimeRcIsUnique`, and three real `CoreLowering.ash`
  emission sites plus `StructuralDroppers.ash`'s own dropper-sharing check). What is still missing
  is specifically the `f$reuse` whole-function SPECIALIZATION stage 0's
  `GetOrCreateReuseSpecialization` builds (`Lowering.Reuse.cs`): a compile-time, per-instantiation
  monomorphization of a registered single-accumulator recursive function with its parameter treated
  as linear, self-calls redirected to the specialization, and `IsFullyReusing` statically proving
  every constructor in the specialized body is already an in-place `AllocReusing` before the
  function's own arena reset is trusted safe — the qualification is static (call-site proofs of a
  provably-unique argument), not an `RcIsUnique` runtime branch. Its to-space materialization side
  (`AllocAdtToSpace`, `CopyOutArenaToSpace`, `CopyFixedInto`) has no self-hosted lowering emission
  site at all yet (the IR kinds, backend codegen, and `PerceusLifetimePlacement.ash` awareness
  exist; only hand-built backend test IR and stage-0 parity fixtures reach them). Given the
  soundness risk (a wrong `IsFullyReusing` verdict corrupts memory, not merely leaks), the safest
  first port slice restricts to a copy-type element (no to-space needed at all, since
  `AccumulatorIsFullyPersistent` degenerates trivially): specialization generation, call-site
  qualification, and the reset-safety scan, deferring the to-space materialization of a heap-leaf
  accumulator field to a follow-up. Ported (2026-09-09), the analysis half of that slice:
  `ReuseResetSafety.ash` carries stage 0's `IsFullyReusing` whole — the forbidden-allocation scan
  (`AllocAdt`/`AllocAdtStack`/`AllocStack`/`ConcatStr` and the four copy-outs, each rejecting with
  its own `ReuseDecisionReason` and the offending instruction's location), the temp-reader and
  local-slot dataflow tables, the safely-consumed walk (writes into, field reads from,
  `CopyFixedInto`/`CopyOutArenaToSpace` materialization, closure-environment capture, and
  borrow/single-store-slot moves, bounded at stage 0's four levels), and the closure-consumed-as-
  call-target walk that rejects a closure passed as an argument — plus
  `specializationRebuildsAccumulator` (a rewriter's result type is the same list or named type as
  its last parameter; a reader is declined) and `accumulatorIsFullyPersistent`, which admits a
  copy-type-element list and nothing else, since a named type's fresh heap leaf fields needed a
  to-space materialization no self-hosted lowering site emitted at that point. Verified by 30 unit
  tests in
  `ReuseResetSafetyTests.ash` over hand-built instruction lists. Ported next (2026-09-09), the
  generation and call-site half, narrowed to the fresh-result path over one copy-element list
  accumulator: `ReuseFunctionSpecialization.ash` scans the parsed program for its self-recursive
  top-level functions (stage 0's `RegisterRecursiveReuseCandidate`) and names their specializations
  the way stage 0 does (`f__reuse`, then `f__reuse$N`); `CoreLowering.ash`'s `reuseSpecializedCallOf`
  qualifies a call whose callee is one of them, takes exactly its own parameter count of arguments,
  rebuilds its accumulator (`specializationRebuildsAccumulator`), carries a layout the
  specialization can keep persistent (`accumulatorLayoutIsPersistable`: a list always does, since
  its cells are rebuilt in the ordinary heap and carry whatever their elements already were, while
  a named ADT needed the to-space materialization no lowering site emitted at that point), is handed
  a value
  the callee's whole-program result reach proves fresh (stage 0's `IsFreshOwnershipResultCall`),
  and mentions only names a body lowered here can bind; and
  `lowerReuseSpecializedCall` lowers the candidate's own lambda as a one-member recursive group
  under that label, with `armSpecializationLinearParameter` making the accumulator a linear reuse
  root and the group's self binding redirecting the recursion into the specialization, the call
  itself becoming the group's continuation. Feeding it, list cells learned the reuse token the
  constructor path already had: a cons arm on a linear reuse root publishes an arena `DropReuse`
  token (`listCellReuseToken`), and `allocateListCell` consumes it as an
  `AllocReusing`/`ListCell` rebuild instead of a fresh `Alloc` — a reference-counted cell still
  takes neither, since its transferred children would need the runtime nullness guard only the
  constructor path emits. The accumulator may be the last of several parameters, the earlier ones
  being configuration the specialization carries unchanged (stage 0's own `moveBodies` shape):
  `armSpecializationLinearParameter` runs at both the recursive member's own parameter and every
  nested lambda's, so whichever binds the accumulator claims the request, and the call is rebuilt
  over the whole argument list. Verified: eleven unit tests in
  `ReuseFunctionSpecializationTests.ash` (the candidate scan, the label scheme, `doubleAll__reuse`
  generated with `DropReuse`/`AllocReusing` for the shape stage 0 specializes, the same call under
  a tail-recursive driver, a second call site taking `doubleAll__reuse$1`, a heap-element list and
  a two-parameter candidate — both of which stage 0 specializes too, confirmed against its own
  `--explain reuse` output — and the two declines, a threaded rather than fresh argument and a
  reader); all four self-hosted suites and the whole-program IR parity fixtures green; and the
  self-hosted CLI compiling and running the same programs to stage 0's answers, with peak RSS
  matching its own `--debug-disable-reuse` build to within the reused cells. A specialization is now
  generated once per concrete instantiation (2026-09-09), stage 0's own cache:
  `reuseSpecializationCacheKey` keys `reuseSpecializations` on
  the callee and its resolved function type, and a later call at the same type takes a closure over
  the label already emitted (`lowerCachedReuseSpecializedCall`) instead of lowering the candidate's
  body again — the closure goes in a local and the ordinary call path applies the arguments to it,
  so nothing about argument ownership is re-implemented. The cached path marks the callee in
  progress exactly as generation does; without that the rebuilt call qualifies again, hits the same
  cache entry, and recurses until the stack overflows. Measured on a program calling one
  specializable function from two sites: the emitted binary drops from 28,784 to 24,688 bytes and
  its answer is unchanged. The reset-safety verdict now
  has its consumers (2026-09-09): `recordFullyReusingSpecialization` scans the generated body that
  bound the linear parameter (`specializingReuseLabel`, stage 0's own) with `reuseResetSafety` and,
  when it allocates nothing that could escape the watermark and the accumulator's layout is fully
  persistent — a list only over a copy-type element, stricter than the routing gate —
  records the callee in `fullyReusingCallees`; `backEdgeArgumentIsInPlaceReuse` then lets a loop
  argument that is a call to such a callee over that loop's own parameter count as surviving a
  plain reset (stage 0's `stableAccArg`), recognized structurally rather than by remembering the
  call node, which has no identity here. The `Tree` loop's back edge consequently reclaims its
  arena on every iteration, pinned by `testLoopBackEdgeReclaimsItsArena`. It costs that particular
  program nothing measurable — once the specialization rewrites every cell in place there is
  nothing above the watermark left to reclaim — which is the expected shape of the win, not its
  absence.
  The direct-unique call path over a loop accumulator is ported (2026-09-09), together with the
  named-ADT accumulator shape it is the only route to: stage 0's fresh-result path rejects any
  accumulator that is not a `TList` outright
  (`QualifyFreshResultReuseSpecialization`'s `NthCurriedArgType ... is not TypeRef.TList`, confirmed
  by running a `Tree -> Tree` rewriter through stage 0's `--explain reuse`, which answers
  `fresh accumulator layout unsupported`), so `specializationAccumulatorIsUnique` now applies that
  list requirement to the fresh path alone and lets a loop accumulator through on the strength of
  its entry copy instead. `collectSpecializableCallArgs` (`ReuseFunctionSpecialization.ash`, stage
  0's own walk) finds every loop parameter handed straight to a single-parameter candidate — stage
  0's `_freshCompositionOnlySpecializable` excludes multi-parameter ones from this scan and so does
  this — `CoreTcoLoop.specializationAccumulators` carries them to `scanSpecializationAccumulators`,
  which admits one whose slot is not provisionally runtime-managed and whose type the entry copier
  can clone, records it in `linearSpecializationAccumulators`, and adds the entry-copy candidate
  the existing `finalizeDirectReuse` machinery emits or elides by move safety. That "worth its
  cost" gate now governs only the direct-in-place candidates: a specialization's reuse fires in the
  generated function, which the loop body's own instructions never contain, so a specialization
  candidate keeps its copy either way (stage 0's `LowerLambdaCoreSpliceReuseCopies` makes the same
  exemption). The named-ADT accumulator is admitted in the shape needing no materialization at all —
  every constructor field the accumulator itself or a copy type
  (`namedAccumulatorFieldsPersistent`) — and the copier-synthesis gate is deliberately not
  `arenaDeepCopySupported`, whose cycle guard reports every recursive ADT as not deep-copyable:
  that flag answers whether an inline walk terminates, while `StructuralCopiers.ash`'s
  `emitCopierField` synthesizes a recursive copier by construction, which is the question stage 0's
  `TrySynthesizeAdtCopier` asks. Measured on a 4095-node tree rewritten 200 times: 5.6 MB peak
  resident against 34.3 MB for the same program built `--debug-disable-reuse` and 4.1 MB through
  stage 0, output identical.
  To-space materialization of a rebuilt cell's leaf fields is emitted (2026-09-09), stage 0's
  `MaterializeSpecializationField` called from `LowerConstructorApplication`: a constructor field
  built from one of the specialization's fresh inputs points into the arena scratch the loop's reset
  reclaims, so it is relocated into to-space before the store. Fresh-input tracking is stage 0's
  `_specFreshInputNames` — `specializationFreshInputs` holds the specialization's own parameter names
  while its body is lowered, and `extendedSpecializationFreshInputs` propagates the status through an
  inlined helper whose argument names a fresh input or is any expression the arm computed (a value in
  per-iteration scratch); a field argument that is a bare name the specialization did NOT bring in was
  matched out of the accumulator, is already persistent, and passes through. `materializeSpecializationField`
  covers the two leaf shapes needing no copier synthesis: a string or bytes value (a length-prefixed
  blob copy) and a tuple of copy types (a fixed-size cell copy). The materialization runs after the
  cell is claimed and before the field stores, stage 0's own order, so the reused cell's old field is
  still readable: an unbound field slot takes the in-place `CopyStringIntoOrFresh`/`CopyFixedIntoOrFresh`
  update path, which bounds blob growth to the largest value a cell ever held, and every other field
  the conservative fresh `CopyOutArenaToSpace`. That is narrower than stage 0's
  `ReuseTokenFieldIsDead`, which also clears a field whose bound name has had every arm reference
  lowered — it keeps a per-binding reference tally this lowering does not; the difference costs a blob
  per rewrite and never correctness. `ReuseResetSafety.ash`'s named-ADT gate widens to match
  (`reuseMaterializableFieldType`), so an accumulator carrying a string, bytes or copy-tuple leaf is
  now fully persistent and its loop's back edge reclaims its arena. Verified: five new tests in
  `ReuseFunctionSpecializationTests.ash` (a `Tree` with a `Str` label generates its specialization,
  overwrites the dead blob in place, and reclaims its arena on the back edge; a still-bound label takes
  the fresh cell; an ordinary constructor outside a specialization emits neither), all four self-hosted
  suites plus both parity runners green, and stage 0's own `--explain reuse`/`--emit-ir` on the same
  `Tree` program confirming it accepts reset safety and emits `CopyStringIntoOrFresh` there too.
  The two field shapes needing a synthesized copier followed (2026-09-09), closing OPT-42:
  `ToSpaceCopiers.ash` is the to-space analogue of `StructuralCopiers.ash`, carrying stage 0's
  `SynthesizeListToSpaceCopier` and `TrySynthesizeAdtToSpaceCopier`. Neither can allocate directly
  in to-space — `AllocAdtToSpace` is reserved for in-place reuse's own fixed-shape node arena, and
  interleaving unrelated cells into it would corrupt that mechanism — so both follow stage 0's
  shape: build the cell as ordinary arena scratch with its fields already relocated, then flat-copy
  the finished cell into the blob with `CopyOutArenaToSpace`'s fixed-size branch, the scratch cell's
  own later reclaim being harmless once it has been copied. The label is registered before the body
  is emitted, so a recursive or mutually recursive type finds its own label instead of recursing
  forever, and the shared copier-label cache keys a to-space copier apart from the arena copier of
  the same type by prefix. `toSpaceCopySafeType` is stage 0's `IsToSpaceCopySafeType` with the same
  path guard: it decides which field shapes the walk relocates completely, so
  `namedAccumulatorFieldsPersistent` can decline the rest rather than leave half a graph behind — it
  takes that predicate as a parameter, since the answer needs the type environment the analysis
  module has no access to. `materializeSpecializationField` routes a `SemList` and a non-self
  `SemNamed` field through `synthesizeToSpaceCopy`, always rebuilding fresh rather than over an
  update's dead cell (no in-place primitive exists for either shape, stage 0's own reason). A field
  of the accumulator's own type is still excluded by symbol identity, stage 0's
  `isAccumulatorSelfType`. Verified: three more tests in `ReuseFunctionSpecializationTests.ash` (a
  `List(Str)` field relocates through `__tospacecopy_list_`, a foreign `Label` ADT field through
  `__tospacecopy_adt_`, and the `Tree` accumulator's own recursive child through neither); all four
  self-hosted suites and both parity runners green; the self-hosted CLI compiling all three probe
  programs to binaries that run natively and print stage 0's own answers; and, on a 4095-node tree
  with a string label rewritten 200 times, 9.7 MB peak resident against stage 0's 8.2 MB and
  32.8 MB for the same program built `--debug-disable-reuse`. The last placement the mechanism
  needed followed (2026-09-09), closing OPT-24's remaining `AllocAdtToSpace` note with it: a cell
  the rebuild could not take from a reuse token is fresh, and inside a specialization
  `emitFreshConstructorCell` puts it in to-space rather than the arena, stage 0's
  `EmitFreshConstructorCell` `_inSpecialization` branch. Pinned by two more tests, one that the fresh
  `Tag("z")` cell of a specialized body is an `AllocAdtToSpace` and one that an ordinary constructor
  outside a specialization is not.
  Stage 0's own reference-counted-spine fix for a non-tail recursive producer followed
  (2026-09-10): a self-recursive call built its callee closure before its own body's
  runtime-managed verdict existed, so every self-closure carried the returns bit clear and its
  call site always took the arena copy-out branch — one copy of the whole result per recursion
  level. `backfillSelfClosureResultOwnership` writes the verdict back into the not-yet-frozen
  instruction buffer once known, mirroring stage 0's own backfill; `isRecursiveProducerTail`
  requests the reference-counted heap for a cons cell whose tail calls a member of the enclosing
  recursive group, so the copy is unnecessary rather than merely cheaper, deferring to a live
  arena list-cell reuse token when one exists (`consumeListCellReuseToken` already declines a
  token under a runtime-managed request, so forcing one blindly here would have silently defeated
  the reuse OPT-42 exists for). `makeList(20000) |> sumList`: 6.3 GB to 10.4 MB, matching stage
  0's own 11.6 MB. Still open: when the same producer's result feeds an OPT-42 specialization
  (`doubleAll(makeList(20000))`), the specialization's own self-call is generated through this
  lowering's general recursive-body finishing path — the same one an ordinary call uses — where
  stage 0 has a dedicated specialization-call lowering that never opens a window around it at
  all. Backfilling that self-call too (the current behavior) still measures far better than not
  (28.1 GB baseline to 9.4 GB) but neither matches stage 0's 11.6 MB for this combined shape;
  porting stage 0's window-free specialization-call bypass is the next step. Pinned by
  `CallWindowLoweringTests.ash`'s `expectNonTailSelfCallReadsReturnsBit`; three whole-program IR
  parity fixtures refreshed to match stage 0's own now-current output
  (`non_tail_self_call_list_result`, `self_call_operand_string_result`,
  `pattern_head_read_under_operator` — stale since stage 0's own fix landed, exposed only once
  this lowering started producing the matching shape).
  Recursive producer provenance also follows frame-local aliases and all-proven joins
  (`RecursiveProducerResult.ash`); shadowed names and mixed joins stay conservative, and a cons
  retains an already-owned recursive tail (`RecursiveProducerResultTests.ash`).
- [x] **OPT-46** Admit every `Str` loop parameter to the reference-counted heap as stage 0's
  `IsRcEligibleScalarTupleOrAdtType` does. The self-hosted admission (`runtimeManagedStrOrdinals`
  and the affine-append walk) declines a string parameter the successor reads more than once
  (`widen(n - 1)(text + text)`), so such a loop keeps its strings in the arena and grows per
  iteration where stage 0 plateaus. Widen the admission, keep the affine in-place append for the
  single-read shape, and pin the two-read shape with a plateau fixture through both compilers.
  Done: measured, the premise did not hold. The self-hosted walk already admits a parameter its
  successor reads twice (`text + text` is the parameter's own append; only the in-place tip
  reservation stays single-read), its loop plateaus at 5.6 MB, and the growth was stage 0's: a
  guard-plus-double successor (`if byteLength(text) >= 4096 then "ab" else text + text`) grew to
  211 MB, and a successor cut out of the parameter by the standard library's `substring` to 408 MB.
  Three stage-0 defects, each fixed in both compilers where the rule exists: a control-flow join of
  a literal and a fresh string was an arena join whose later copy-out orphaned the fresh branch's
  reference-counted value, so a reconcilable join (`IsReconcilableFreshStringJoin`, the match
  arms' rule extended to `if`) now asks its branches for a runtime-managed string and copies the
  literal branches inside their branch (`WithReconcilableFreshStringJoinRequest`,
  `TryLowerStaticStringNormalizedBranch`; self-hosted `withReconcilableFreshStringJoinRequest`,
  `lowerStaticStringNormalizedBody`); the post-hoc promotion of a concatenation reached from a
  runtime-managed parameter is limited to values that flow to a parameter store or the result and
  outside a mixed join (`TempsFlowingToLoopParameters`, `MixedJoinSources`), and the concat fold
  accepts arena inner links under a reference-counted root; and a fresh reference-counted builtin
  result reaches none of its arguments in the ownership summaries (both reach walks), which made
  `substring`'s result fresh, so the entry-helper inliner took it and now releases a fresh
  reference-counted argument the inlined result cannot keep (`ReleaseInlinedFreshArguments`). The
  self-hosted backend's `BytesSubText` also allocated a reference-counted cell for an arena request
  (a 4 KB leak per slice); it now places the string where the instruction asked. Pinned by
  `tests/tco_runtime_managed_str_parameter_read_twice_plateau.ash` (both compilers, 200000
  iterations, 8.2 and 5.9 MB) and
  `Linux_backend_llvm_runtime_rc_string_parameter_read_twice_memory_should_plateau`; the explain
  parity fixtures record the now-fresh results. With those results no longer poisoning their
  callers, stage 0 counts a call whose result reaches no parameter as a moved argument, so the
  self-hosted ownership inference now applies the same result-alias rule (`resultAliasMove` over
  the program's reach summaries) and the parity reports agree on which parameters are unique.
- [x] **OPT-47** Retain a whole ADT loop parameter consed into a sibling list accumulator
  (`collect(n - 1)(State(...))(s :: acc)`) in the self-hosted lowering, stage 0's rule since the
  sibling-accumulator UAF fix. The shape is reachable only once a list over records is admitted
  as a runtime-managed list parameter alongside the record parameter it stores; confirm the
  admission after the record-list accumulator work, add the retain at the cons, and pin the
  shape with a fixture whose output would print the released record otherwise.
  Done: confirmed rather than added. The record-list accumulator work admits both parameters
  (the record slot is copied out of the arena at entry and at every back edge, the list slot is
  normalized at entry), and its `retainListElement` marker already puts the `RcDup` of the
  record parameter on the cons cell before the back edge releases the old record; the
  self-hosted loop body matches stage 0's instruction for instruction at the cons and the back
  edge, differing only in temp numbering, the always-emitted list exit-transfer check, and
  stage 0's dead constant-guarded retain of the successor's scalar field.
  `tests/tco_runtime_managed_record_param_consed_into_sibling_accumulator.ash` pins the shape
  through both compilers: 20000 records consed behind their successor, then read in both
  orders, so a released record would print reused memory. The self-hosted binary's higher peak
  for a retained list of records (about 290 bytes per record against stage 0's 200) is the
  libc `malloc` allocator recorded under CG-4, not a lowering difference: the same probe
  without the string field shows a gap of the same kind while the instruction streams agree,
  and both binaries scale linearly in the record count.
- [x] **OPT-48** Port stage 0's entry-helper inlining: a call to a stdlib or user function whose
  body is a `let recursive go ... in go(seed)(argument)` entry (`List.length`, `List.reverse`, the
  fold family) is lowered as the loop closure built in the caller and applied directly, so the
  argument passes without the accepts-bit retain of a general call. The churn fixture's loop
  (`tests/generic_append_map_churn_plateau.ash`) differs from stage 0 only at its
  `list.length(entries)` call for this reason; the parity fixtures and the churn loop diff are
  the oracle.
  Done: `HelperInlining.ash` carries the structural shapes (`exprHasCallOrAggregate`, the
  nested-recursive-return shape stage 0 specializes instead of inlining), and every
  non-recursive let-bound lambda whose innermost body allocates or calls is registered as an
  inlinable helper when its `let` is lowered, stitched stdlib functions included. A saturated
  call to such a helper is spliced into its site (`tryInlineHelperCall`, before the general
  call's arena window opens) while a reuse token is live, or under a loop's back edge when the
  helper's result reach is fresh; the helper must not be inlining already, its name must still
  bind a function at the site, and every name its body reads past its parameters must resolve
  there lexically, as a constructor, or as an inlinable helper in turn (a top-level function the
  caller did not capture resolves by label since OPT-33, see below). The arguments
  are lowered under a plain request into fresh locals, the parameters bound to those locals, the
  body lowered under the call's own request, and a fresh reference-counted argument the result
  cannot keep is released after the body (`releaseInlinedFreshArguments`). The churn loop now
  lowers instruction for instruction as stage 0 does, and
  `inlined_entry_helper_under_back_edge` pins a user helper spliced under a back edge as a
  whole-program parity fixture.
- [x] **OPT-49** Both compilers: honor a poisoned result reach when releasing a consumed fresh
  reference-counted argument. Every shape named below is measured and fixed, and the work split
  out of this item — OPT-49a, OPT-49b, OPT-49c, OPT-50, and OPT-51 — has all landed; the
  self-hosted mirror of the two stage-0 halves continues under OPT-52. Only the deep-copied list
  case consults the callee's poison
  (`ConsumedDeepCopiedListStaysWithCallee` / `consumedDeepCopiedListStaysWithCallee`); the
  remaining poison sources (a lambda, `await`, a handler, a result pipe, an unresolved callee)
  can still release an argument the callee's arena-placed result embeds. Model each shape
  precisely rather than forcing retains (which would leak), mirror the rule in
  `CoreLowering.ash`, and pin each source with a parity fixture and an execution test.
  Measured (2026-09-07): no shape over-releases. A fresh string consumed by a callee whose
  result reach is poisoned reaches the caller either copied (the arena-result request, bit 1 of
  the ownership word, and the conditional copy-out on an unknown returns bit both copy the
  result before the consumed argument is released) or adopted, so the probes print correctly
  and the defects are leaks: the unresolved callee (`apply f s = f(s)` applied to `identity`)
  leaked the returned string in both compilers, fixed by the returns bit of a returned
  normalized parameter (see the arena-result boundary above); a result pipe (`|?>`) and an
  awaited task (`await echo(fresh)` inside an async body) both plateau at 8.2 MB; a handler arm
  (`handle Tag.tag(s) with | Tag.tag(text) -> resume(text)`) leaked the arm's
  reference-counted copy of `text`, which the perform site took for an arena value and the
  handle copied at its scope boundary (about 615 bytes per call in stage 0), until OPT-49a made
  the perform site adopt it and the handle release it (8.2 MB plateau); a record holding a
  closure that captures the normalized parameter (`Box(reader = given (u) -> s)`) leaked the
  owned string through an arena environment nothing released, fixed in stage 0 by OPT-49b
  below (the closure owns the capture on the reference-counted heap and the record releases
  the closure as a child). `tests/consumed_argument_*` pin the correct output of every shape
  through stage 0. The remaining work is split below.
- [x] **OPT-49a** Stage 0: the perform site adopts a handler arm's reference-counted result.
  Read the arm closure's returns bit at the call, take the value as newly produced on that
  branch and copy it out otherwise, so the handle's scope-boundary copy no longer duplicates a
  value nothing releases; the arm closure built by `LowerHandleLowerArmClosures` must carry the
  returns bit its lowered body earned.
  Done (2026-09-07): `EmitPerform` reads the arm closure's returns bit at the saturating call
  and adopts the result as newly produced on that branch, normalizing an arena result into a
  reference-counted copy otherwise (`PlanPerformResultOwnership`: a resolved string, bytes,
  list, or shallow-copyable ADT result adopts; an unresolved layout, or a function that may
  execute inside a coroutine, requests the arena form through bit 1 of the ownership word;
  copy types, closures, and resources are unchanged). A closure's returns bit is now the
  compiled body's own fact rather than gated on the creating function's placement context
  (`LowerLambdaCoreMakeClosure` and the two label-reconstructed `MakeClosure` sites), so the arm
  closure reports the result its entry normalization earns. A return arm over a body value the
  handle owns applies as a continuation under the ordinary closure ownership contract
  (`EmitOwnedContinuationCall`: retain the argument when the closure adopts arguments, adopt or
  normalize the result by its returns bit, release the original once the result cannot reach
  it), and the posts fold applies the same contract per post, so the scope-boundary copy is
  gone and the caller releases the value. `tests/consumed_argument_through_handler.ash`
  through stage 0: 28.7 MB peak at 40000 iterations and 110.6 MB at 200000 before, 8.2 MB at
  both after, output unchanged; `tests/handler_arm_reference_counted_result_plateau.ash` pins
  the 200000-iteration output, and the one-shot post, non-identity return arm, `Int` return
  arm, and let-forwarded body shapes all plateau at 8.2 MB. Two neighbouring shapes still leak
  and are split out as OPT-50 and OPT-51.
- [x] **OPT-49b** Stage 0: a closure capturing an entry-normalized parameter owns the capture.
  The environment holding the owned string (or record) is placed on the reference-counted heap
  with its dropper, and the record storing that closure counts it as a fresh owned child, so the
  caller's copy-out of the record retains rather than duplicates the value.
  Done (2026-09-07): a closure literal whose captures are inline values beside the
  entry-normalized string parameter is a fresh owned child of the record storing it
  (`IsRuntimeRcOwningClosureExpression`, consulted by both fresh-child predicates and by the
  field request, which lowers the lambda under the `Closure` representation). The capture
  moves the owned string into a reference-counted environment, the closure carries the
  `__rc_cdrop` dropper releasing it, and the record is placed on the RC heap with a `Closure`
  child drop kind (`OrdinaryHeapChildDropKind.Closure`; records and owned-child ADTs admit a
  function field, `CanDropAdtGraph` counts it droppable, and the runtime-managed deep copy
  copies it with `CopyOutClosure RuntimeManaged`). The caller adopts the record through the
  compiled-body fact and its structural release drops the closure as a child. The backend's
  closure release is now count-aware: `RcDrop Function` runs the dropper on the last reference
  only, then releases the environment and the cell; the packed closure word carries a
  runtime-managed bit (bit 61) so `CleanupResource` on a borrowed reference-counted closure is a
  no-op, and an arena `CopyOutClosure` of a reference-counted closure carries no dropper.
  Measured on `tests/consumed_argument_captured_by_lambda.ash`: 40,972 KB at 40000 iterations
  and 159,756 KB at 200000 before; 8,204 KB and 4,108 KB after, output unchanged.
  `tests/consumed_argument_captured_by_lambda_plateau.ash` pins the 200000-iteration output;
  `closure_record_parameter_entry_normalized.ash` and `closure_record_accumulator_loop.ash` pin
  a closure-bearing record through entry normalization and as a loop accumulator. The
  self-hosted backend mirror of the closure-word bit and the count-aware release landed under
  OPT-52 (2026-09-08).
- [x] **OPT-49c** Self-hosted: the whole-program lowering takes capability declarations, `|?>`
  lowers as a core expression, and a record may hold a function-typed field, so the handler,
  result-pipe, and closure-capture shapes compile through the self-hosted compiler and OPT-49a
  and OPT-49b can be mirrored. `async`/`await` as core expressions
  (`UnknownLoweringBinding("async")` today) is milestone 4's CG-12/OPT-43, not this item; the
  await shape stays stage-0-only until then.
  Done (2026-09-07), function-typed fields: `typeExprToSemanticType` resolves a pure arrow
  (`TypeArrow` with no capabilities and no tail) to `SemFunction`, the arity and implicit-type-
  parameter walks descend into arrows, and the rejection message names the arrow among the
  supported field types; nothing downstream needed changing — `HeapLayoutClassification.ash`
  already classified a function child as `UnsupportedChildDrop`/`NoStructuralCopy` (stage 0's
  rule before OPT-49b: the closure child is never dropped or copied structurally, see OPT-52), the
  single-constructor record is tagless, and the closure word is stored and loaded like any field.
  `tests/consumed_argument_captured_by_lambda.ash` prints `41337792` through the built
  self-hosted compiler (peak RSS 38.9 MB against stage 0's 41.0 MB). Its lowered IR differs from
  stage 0's only in the loop back edge, where stage 0 emits a `CleanupResource TypeName=Function`
  for the pattern-bound `reader` closure that the self-hosted placement does not (a codegen no-op
  for a closure, see `IrCodegen.ash`), and in the trait-evidence header, so no parity fixture was
  pinned. `FunctionFieldLoweringTests.ash` covers the record, positional, and type-parameter
  arrow fields and the capability-row rejection.
  Done (2026-09-07), `|?>`: `lowerResultPipe` (`CoreLowering.ash`) unifies the left operand with
  `Result(e, s)`, the mapper with `s -> r`, decides the flat-map case from the resolved `r`, and
  `CoreResultPipeLowering.ash` emits stage 0's `EmitResultPipeBranches` shape (tag test against
  `Ok`, `GetAdtField`, `CallClosure`, `Ok` rewrap or plain store, `result_error_N`/`result_end_N`
  join through one local); neither operand inherits the context's request. `|!>` is not ported.
  `tests/consumed_argument_through_result_pipe.ash` prints `41337792` through the built
  self-hosted compiler (peak RSS 5.6 MB against stage 0's 8.2 MB plateau). The pipe's own IR is
  byte-identical to stage 0's; the surrounding difference is the known OPT-33 divergence
  (`stamp` reaches `check` by label where stage 0 captures it), so no parity fixture was pinned.
  `ResultPipeLoweringTests.ash` covers the rewrap, the flat map, and a mapper mismatch.
  Done (2026-09-07), capability declarations: `registerProgramCapabilities` registers every
  top-level `capability` ahead of the value chain (declaration order gives the evidence global,
  operations are numbered within the frame, reserved and duplicate names and duplicate
  operations are rejected — `ReservedCapabilityName`, `DuplicateCapabilityName`,
  `DuplicateCapabilityOperationName`), `capabilityHandlerGlobals` is stage 0's `count + 2`, the
  implicit call form `Cap.op(x)` routes to `lowerPerform` (`isCapabilityOperationCall`), the
  perform site types its result from the operation's recorded signature (`performResultType`)
  instead of `Unit`, the unhandled-operation panic message is interned as a real string literal,
  and the backend defines one `__ashes_capability_handler_<i>` global per slot and lowers
  `LoadCapabilityHandler`/`StoreCapabilityHandler` over them. Stitching already renamed the
  declaration and every `Cap.op` reference consistently (`AshesPrivateType_<module>_Cap`).
  `tests/consumed_argument_through_handler.ash` prints `41337792` through the built self-hosted
  compiler, at a peak RSS of 60.7 MB against stage 0's 28.7 MB: the handler shape leaks in both
  compilers until OPT-49a lands, and the self-hosted arm closure leaks more because it carries
  neither stage 0's argument normalization nor its returns-bit epilogue (CAP-10). The perform
  site's evidence save/restore order and the post-register labels (`capability_post_skip_N`,
  `capability_posts_loop_N`/`capability_posts_done_N` against stage 0's `capability_no_post_N`
  and `posts_fold_N`) also differ, so no parity fixture was pinned.
  `CapabilityProgramLoweringTests.ash` covers the registration, numbering, global count, and the
  three rejections.
- [x] **OPT-50** Stage 0: a function that may execute under a live handler post ignored an
  unknown callee's returns bit. `apply (f: Str -> Str) (s: Str) = handle f(s) with ...` applied
  to `identity` (whose entry normalizes and returns its parameter) leaked the returned string:
  the caller's placement context turned off `needsResultOwnership`, so the result was copied at
  `ArenaCallBoundary` and the callee's reference-counted original was never released (measured
  2026-09-07: 28.7 MB peak at 40000 iterations, 110.6 MB at 200000, about 520 bytes per call).
  `needsResultOwnership`'s gate on `AllowsOrdinaryRcPlacement` was the only difference from
  `PlanPerformResultOwnership`'s parallel condition (OPT-49a), which never checks it: adopting a
  value the callee already reports as freshly reference-counted needs no scope-based placement
  decision by the caller, so it stays safe under a live handler post the same way the perform
  site's adoption already does, even though this call site is an ordinary application rather than
  a `perform`. Dropping that one condition re-enables the existing adopt/normalize machinery
  (`ResolveCallResultOwnershipFlag`/`EmitClosureReturnsRuntimeManagedFlag`) unchanged; the result
  is unrelated to `RequestsArenaResult`, which was already computed unconditionally and correctly
  requests the arena form for an unresolved result layout regardless of handler-post status.
  `tests/handler_post_unknown_callee_result_plateau.ash` plateaus at 8.2 MB for both iteration
  counts after (was 28.7 MB / 110.6 MB before), output unchanged.
- [x] **OPT-51** Stage 0: an arm result whose type has no complete copy-out layout kept the
  arm's reference-counted child alive. With `capability Tag = | tag : Str -> Wrapped` and
  `Tag.tag(text) -> resume(Just(text))` the arm built the constructor in the arena over its
  entry-normalized copy of `text`; the perform site left such a result unchanged
  (`PerformResultOwnership.Unchanged`) and the copy leaked at the same rate as OPT-50 (measured
  2026-09-07: 28.7 MB / 110.6 MB at 40000/200000 iterations, same as OPT-50's baseline). Two
  coordinated fixes, both needed:
  1. Placement, scoped narrowly: `CanRuntimeManageFreshHeapChildAdtConstructorApplication`'s `Str`
     field case gained an alternative — `IsNormalizedAlwaysReturnedStringParameterRead`, the same
     fact `LowerLambdaCoreLowerBody`'s own entry preamble already uses to decide whether a
     parameter is unconditionally RC after normalization — so `Just(text)` is recognized as a fresh
     owned child when `text` is exactly the arm's own always-returned parameter. Reaching this
     branch at all also needed the arm closure's OWN escaping-result gate relaxed past
     `AllowsOrdinaryRcPlacement`, but ONLY for the arm's own body: a new ambient flag,
     `_loweringHandlerArmOwnResult`, set for the duration of `LowerHandleLowerArmClosures`' own
     `LowerExpr(armLambda, ...)` call and ORed into `LowerEscapingResult`'s/
     `LowerLambdaCoreLowerBody`'s existing `AllowsOrdinaryRcPlacement` check — never a blanket
     removal of that check. A first attempt that dropped `AllowsOrdinaryRcPlacement` for every
     function reachable from a live handler post (not just the arm's own body) broke
     `Helper_reachable_from_a_handle_stays_on_the_guarded_arena_path`/
     `Higher_order_target_reachable_from_a_handle_falls_back_to_the_safe_arena_path`: an ordinary
     function merely CALLED from inside an arm has no adopt/release net at its own result the way
     the arm's own perform site now does, so it must stay conservative. A second attempt reused the
     positional single-constructor accumulator shape (`CanRuntimeManageTcoOwnedChildAdt`,
     `IsRuntimeManagedConstructorCandidate`'s own branch) instead, but that shape's `List` field
     case accepts ANY `Var` argument (not just a fresh construction or an enclosing TCO loop's own
     parameter) — safe in the narrower contexts that already reach it, unsafe once
     `IsFreshRuntimeManageableAdtExpressionCore` could reach it unconditionally too; broke
     `Directly_escaping_adt_with_borrowed_list_child_remains_arena_managed`
     (`Payload(values)` over a plain outer-scope `let values = [40, 2]`, which nothing retains).
  2. Perform site: even with the arm genuinely placing `Just(text)` on the RC heap,
     `PlanPerformResultOwnership`'s `CopyOutKind.None` branch only requested the arena form when
     `RequestsArenaResult` (a generic/unresolved-layout check) applied — never for a concretely
     resolved, pointer-bearing type like `Wrapped`. Widened to also request the arena form whenever
     `AllowsAsyncIndependentRcPlacement` holds, the same condition already gating `Adopt` for a
     `Shallow`/`List` result: the arm's own epilogue
     (`LowerLambdaCoreNormalizeRequestedArenaResult`) is a no-op whenever the result was never RC to
     begin with (every closure/resource result already routed through this branch included), and a
     real deep-copy-and-release otherwise, so requesting it unconditionally under that placement
     flag is safe regardless of what a specific arm produces. `Adopt` itself was not extended to
     `CopyOutKind.None`, since there is no defined "copy this shape out of the arena" fallback for
     `EmitPerformAdoptResult` to fall back to. With this fix, `decorate`'s own compiled result stays
     arena (the arm's RC construction is deep-copied out and released before the perform site ever
     returns), so no caller-side change was needed for a plain external `match decorate(x) with ...`.
  `tests/handler_arm_constructed_reference_counted_result_plateau.ash` plateaus at 8.2 MB for both
  iteration counts after (was 28.7 MB / 110.6 MB before), output unchanged. Full C# suite
  (2553/2553), LSP suite (72/72), and e2e suite (748/0/55 skipped) all green.
- [x] **OPT-54** Measured with per-phase timers before rewriting, and the entry function was not
  the cost. The self-hosted semantics test program's 72,000-instruction entry is about 5 s of
  its compile (3.8 s of lifetime placement plus part of entry inference), not the majority:
  `ASHES_TIMING`'s `lower.*` and `optimize.*` sub-phases put the 44 s of lowering at 14 s for
  the inferred-trait discovery pass, 8 s for move analysis, 10 s for body lowering, 5 s for
  placement, and 2 s for entry inference, and the 12 s of optimizing at 7 s for the closure
  passes, with a profile showing 29% of the main thread waiting on the garbage collector and
  11% in runtime type checks. The fixes were allocation and rescanning, not a global-slot
  rewrite: the discovery pass shares trait implementation lambdas like the real pass; the closure
  passes cache def-use facts by instruction-list identity and skip functions without an indirect
  call; the deferred TCO reset splice swaps the entry's instruction list and ownership facts
  instead of copying them per function; free-variable analysis binds and unbinds in place
  instead of copying the bound set per binder; match exhaustiveness buckets patterns by
  constructor name once instead of filtering every pattern per constructor; the environment
  free-variable scan remembers each monomorphic binding's variable nodes; lambda entry reseeds
  the top-level scope's intrinsic bindings from a list filtered once per scope; and the CLI runs
  the server garbage collector. Lowering went from 36 s to 26 s, optimizing from 12 s to 7 s, and
  the whole compile of the semantics test program from 68 s to 52 s wall; the stage-1 CLI package
  from 65 s to 50 s; peak memory unchanged at about 8 GB. The entry keeps its `optnone`
  treatment; a global-slot rewrite is not worth its risk while the entry is 5 s.
- [x] **OPT-55** Self-hosted mirror of OPT-54's two shapes that stage 1 shares.
  `IrOptimizer.ash`'s known-returned fixpoint now computes every function's single-definition
  facts once (`withSingleDefinitions`) ahead of every round instead of per function per round,
  and both per-function devirtualizations return a function without a `CallClosure` untouched
  before counting anything (`containsCallClosure`). `TypeSchemes.ash`'s `generalize` computes
  the candidate variables first and never scans the environment when there are none, and the
  lowering's `generalizeResolvedType` skips resolving every outer binding's scheme for a
  variable-free type. Stage 1's deferred TCO resets already splice per function, and its
  free-variable walks use persistent lists, so those two needed nothing. Not measurable on the
  one program shape stage 1 compiles today: the stage-1 CLI takes a single file, and the largest
  single-file test (`tests/text_json_parser_smoke.ash`, 22 KB) compiled in 1.45 s of user time
  before and after, with 17.5 GB of peak RSS and as much system time as user time: that cost was
  lifetime placement's per-owner dominator sets (OPT-57), not these passes; re-measure once
  stage 1 compiles a package (CLI-4). Stage 1's `countDefinitions`, `countUses`, and
  `collectSingleDefiningInstructions` remain association lists with a linear lookup per temp,
  quadratic per function; `Ashes.Collection.Map` (unused in the semantics package so far) is the
  replacement when that shows up.
- [x] **OPT-57** Stage-1 memory under compilation. The stage-1 compiler peaked at 17.5 GB on the
  22 KB JSON parser test, growing roughly with the cube of an if-chain's depth once its arms held
  a `match`. Narrowed by reading the process RSS at phase boundaries under gdb (perf callchains
  and mmap backtraces on these binaries pointed at the linker and the formatter, both wrong):
  lowering proper ended at 73 MB, and lifetime placement took it to 4 GB across twelve owners of
  one function, about 370 MB each, all in `computeDominators`, called once per owner and keeping
  one sorted-list set per block that every fixpoint round rebuilt and nothing reclaimed, plus a
  second, independent O(blocks) `predecessorsOf` filter-scan run once per block (so O(blocks²)
  overall) inside every one of those per-owner `buildCfgBlocks` calls. Stage 0 computes dominators once per function and
  reuses them across owners, since an owner's edits move instruction indices but never change the
  block graph. Stage 1 now does the same through `PlacedInstructions.placedDominators`, and holds
  dominance as the immediate dominator of each block (`computeImmediateDominators`: a reverse
  postorder of the reachable blocks, then the Cooper-Harvey-Kennedy refinement, with `dominates`
  walking the tree), one `Int` per block instead of a set; block predecessors are now built once
  per `buildCfgBlocks` call from a flat edge list (`edgesOf`/`predecessorsFromEdges`) instead of a
  filter over every block's successors for every other block. JSON parser test: 17.5 GB to 2.8 GB
  (0.44 s), with the residual now flat per owner (about 33 MB, independent of the owner's index
  within its function) rather than growing — proportional to program size, not owner count
  squared. The IR parity suite pins the placement output unchanged.

  Found and worked around a stage-0 codegen bug on the way: a `List((Int, Int))` (tuple list)
  accumulator threaded through a self-recursive function that calls a SECOND self-recursive
  function to extend that accumulator before its own next tail call segfaults at runtime — mininal
  repro is two five-line functions, reproduces from stage 0 directly (`ashes run`), unrelated to
  self-hosting. Bisected with a size-3 input list (`[(1,[2,3]),(2,[3]),(3,[1])]` through
  `edgesOf`/`edgesFrom`); the emitted IR shows a spurious deep-copy-then-drop of the tuple list on
  the "borrowed argument" exit path (`__deepcopy_list_N` immediately followed by an `RcDrop` of
  the same temp it just copied from). Plain `List(Int)` accumulators through the identical shape
  do not trigger it. Worked around here by flattening the two functions into one self-recursive
  walk over both the remaining groups and the current group's remaining targets (`edgesOf`'s
  current shape) rather than fixing the underlying stage-0 bug, which needs its own narrowing pass
  in `Lowering.Ownership.cs`/`IrOptimizer`'s deep-copy insertion for a borrowed `List` of a
  runtime-managed aggregate element type.
- [x] **OPT-68** The self-hosted semantics suite was red on main (found 2026-09-13 while landing
  LNK-2's qualified-reference port, reproduced on a baseline build of main itself):
  `CallWindowLoweringTests.ash`'s "generic list result deep copy program entry" check differed
  from stage 0's `generic_list_result_deep_copy.ir` fixture by exactly five label numbers
  (`rc_normalize_list_13` versus `_18` and every label after it) with every instruction
  otherwise identical. Not an emission drift in the entry: label numbers are allocated
  program-wide, and `mapAll`'s closure helper is lowered with tail-modulo-constructor by the
  self-hosted compiler and without it by stage 0 (OPT-63's cross-compiler coverage gap), so its
  extra labels (two `tmc_close_*`, three `rc_call_argument_not_retained_*` since #1002) shift
  the entry's numbering. The check now compares labels renumbered by their order within the
  entry (`withFunctionLocalLabels` in `LoweredIrFixtures.ash`); the helper's own divergence
  stays OPT-63's. The suite's second red check was a real regression from #962's port: the
  transformed `stamp` (`bang(head) :: stamp(tail)`) lost its `ReturnsRuntimeManaged` bit and
  result-ownership epilogue, so a caller wanting an arena result copied the reference-counted
  spine out and reclaimed only the arena, stranding every cell. Two facts stage 0 keeps were
  missing: the back-edge dummy is a reference-counted value (`LowerCallTcoBackEdgeDummy`'s
  `MarkRuntimeManagedTemp`, so a join of reachable reference-counted arms and the back-edge arm
  stays reference-counted), and the closed spine carries the body value's representation
  (`RecordControlFlowJoinTemp` at `LowerLambdaCoreCloseTmcChain`); both ported, and the check
  rewritten to the transformed shape (`expectConstructorRecursiveProducerKeepsRuntimeManagedSpine`).
  That program now matches stage 0's `non_tail_self_call_list_result` fixture byte for byte, the
  exit-transfer selection slot stage 0 reserves for any loop with a reference-counted result
  included (`reserveTcoExitTransferSlot`). `ResultReachTests.ash`'s three entry-normalization
  expectations had also drifted, since #996 located every instruction a binding's finalization
  emits at the binding's declaration; regenerated from the (main-identical) output. The suite
  now runs green in both modes; its last red check, `TcoLoopLoweringTests.ash`'s `countLeft`,
  closed with OPT-65's self-call port.
- [x] **OPT-69** `selfhost/tests/ir-program-parity` stopped at its first mismatch, so a red
  fixture hid every fixture after it. The runner now checks every listed fixture and prints one
  report per red fixture (line number, expected and actual line) before failing, and names the
  fixtures it leaves out. The two fixtures found red behind it on 2026-09-13
  (`tco_consumed_list_parameter_borrowed_head`, `pattern_head_read_under_operator`) turned out to
  be OPT-70's oracle gap, not lowering bugs: the self-hosted dump of both equals the stage-0
  CLI's own `--emit-ir lowered` dump function for function. What was a genuine gap is the
  active-flag slot of a loop parameter whose type is still a variable at the loop entry: stage 0
  admits a parameter to the reference-counted heap only once its layout is resolved
  (`ResolvedLayoutEligible`), so it allocates the flag where a back edge or the post-body
  refresh resolves it, after the locals allocated in between; the self-hosted entry allocated
  every shape candidate's flag ahead of the body. `allocateListActiveSlots` now skips an
  unresolved candidate and `allocateListActiveSlotsAt` allocates at the back edge (from the
  argument types, ahead of the pattern-binding transfer) and at the finalize (every remaining
  candidate, retired if the resolved types keep it in the arena), pinned by the new
  `tco_list_parameter_resolved_by_back_edge` fixture (an operator-free loop, so both oracles
  agree on it).
- [x] **OPT-72** The self-hosted lowering's memory and time on a whole stitched project, first
  measured 2026-09-13 with BOOT-2's phase probe (the stitched CLI package exhausted 24 GiB in
  six seconds where stage 0 compiles it in about 9 GiB). Profiled with a `--debug -O2` build of
  a lowering-only driver over generated programs of n independent top-level functions
  (`perf record` plus `addr2line`, the binary loads at 0x400000): time and memory were both
  quadratic in n. Every dominator found is fixed, in the self-hosted lowering unless noted: the
  flat top-level chain nested one bracket close per declaration (a continuation per `let`, a
  stack frame per declaration, and a stack overflow at 2,000 declarations on the default
  stack), now walked iteratively with the pending closes on a list
  (`openArenaBracketedTopLevelLet`, `closePendingTopLevelItems`); the whole remaining chain
  was walked again per declaration for two facts (`escapingChainBody`,
  `isTailForwardedBindingResult`), now precomputed once in `TopLevelLoweringAnalysis`; the
  direct-callee analysis walked the rest of the program per binding (`programNonCalleeUses`,
  one walk); the result-provenance SCC (`OwnershipProvenance`) used association lists for
  every lookup and recursed with a `(visited, ...)` pair per visit whose returned map was
  cloned, now balanced maps and explicit-stack walks; `lookupRuntimeTemp`,
  `bodyRuntimeManagedByLabel`, `topLevelNames`, `checkTopLevelNames`, the reach registry's
  value names, and the RC-eligibility table were lists searched per query, now maps;
  `dedupeValuePlacements` compared origin records pairwise, now keyed by label and temp;
  `SourceContext.findLine` scanned the line starts, now a balanced tree; and two placement
  walks tail-called through a pipe into a lambda (`ownerSlots`, `collectArenaAdtCells`), which
  stage 0 does not compile as a loop (OPT-75). Two stage-0 findings came out of it: a list
  builder that conses a string or list borrowed from a record around its recursive call has
  its partial list copied out at every return (OPT-74; the affected builders now accumulate and
  reverse once), and a match arm that speculatively reused a dead nullary cell for its result
  (`| Empty -> None`) was later reverted to a fresh arena allocation while the arm's scope had
  already reset the arena under the result on the strength of the reuse, so the `None` came
  back in a reclaimed chunk whenever that allocation opened one (a use after unmap the stage-1
  compiler hit in `Ashes.Collection.Map.getStr`; a nullary reuse no longer registers as a
  reuse result, pinned by `RevertedNullaryReuseArmResetTests`, the explain fixtures the parity
  test listed without files are generated). Measured on the same driver, lowering only:
  8,000 independent functions went from 12.4 s and 7.3 GiB to 0.62 s and 1.7 GiB, memory now
  doubling with n, and 8,000 declarations lower on the default 8 MB stack. The remaining
  superlinear cost is the shape where every function calls an earlier one: 1,000 such
  functions take 1.8 s and 10 GiB, 2,000 take 11 s and 40 GiB, because
  `ensureResultRcEligibility` recomputes the whole-program provenance fixpoint at the first
  call site after every recorded lambda (OPT-73). The standard library's `append` is still not
  tail-recursive (a stack-safe `append` belongs to OPT-73's arc, since the stitched CLI's
  instruction lists are the first place it will matter).
- [x] **OPT-73** The result-provenance fixpoint is computed once per program, keyed by function
  identity, as stage 0's `ComputeFunctionResultProvenanceFixpoint` over `_maFuncs` is. The
  self-hosted `ensureResultRcEligibility` recomputed the nodes and the SCC fixpoint over every
  lambda recorded so far whenever a lambda was recorded since the last call site, so a program
  of n calling functions paid n fixpoints of size n (10 GiB per 1,000 such functions), and it
  resolved forward targets by name against the latest recorded lambda of that name, so a nested
  helper shared by several functions (a `go`) resolved to whichever was lowered last. The nodes
  are now built from the reach registry's functions (`ReachFunction`'s key, registered
  parameters and body, and lexical scope from names to keys; `CallResultProvenance` extends that
  scope through nested bindings the way stage 0's `ExtendFuncScope` does), solved once right
  after the type declarations are registered (stage 0 registers every type declaration before
  it lowers a value, so the item walk now does the same, `registerProgramTypes`), and a call site
  resolves its callee by `name@identity` through the summaries' indexes (`calleeSummaryOf`),
  never eligible outside the registry, as stage 0's memo default. The let-lambda tables a call
  site consulted per call (`letLambdas`, `letLambdaLabels`, `letLambdaIdentities`) and the reach
  summaries (`reachSummaryFor`, `reachSummaryNamed`) were lists walked per call site and are
  indexed by maps. The whole-program reach fixpoint itself re-summarized every function per
  sweep until nothing changed; it is now stage 0's worklist (`solveReach`): a function is
  re-summarized only when a summary it depends on grew, the dependents coming from a syntactic
  walk of each body over its scope (`dependencyKeys`, a superset of what the reach walk
  consults). Measured on the lowering-only driver: 4,000 functions each calling the previous
  one went from 74 s and an out-of-memory kill at 58 GiB to 1.5 s and 1.2 GiB; 8,000
  independent functions stay at 0.7 s and 1.9 GiB. The BOOT-2 probe now lowers the whole
  stitched CLI package (10 s, 15 GiB, against stage 0's 9 GiB) and stops at the next blocker,
  MOD-13. Found on the way, and filed as OPT-76 with its reproduction: a stage-0 use after
  free that the accumulate-and-reverse shape of a record builder triggers, which is why
  `buildComponents` keeps its recursive shape.
- [x] **OPT-74** Stage 0 copies the partial result of a list builder out at every return when
  the element consed around the recursive call is a string or list borrowed from a record
  (`| Node { functionName = name } :: tail -> name :: getNodeNames(tail)`), so a builder over n
  records costs n^2 memory: measured 2026-09-13, 4,000 records cost 1.1 GiB and 0.19 s where
  the same builder consing a fresh string, a tuple, or the record itself, and the same walk as
  an accumulator loop reversed once, cost 8 MiB. The builder's result is arena-placed (a
  borrowed element keeps it off the reference-counted heap) and each return crosses the
  callee's bracket with a `CopyOutList HeadCopy=String`; the copy should retain the borrowed
  element or place the spine on the reference-counted heap instead of copying the prefix at
  every level. Until then the self-hosted compiler's builders over record fields accumulate
  and reverse (`getNodeNamesInto`, `nodesOf`, `resolveNodeProvenancesInto`,
  `dedupeValuePlacementsInto`). This shape is what now dominates the BOOT-2 probe's memory
  (2026-09-13, sampled with gdb at every heap-growing `mmap` past 8 GiB): the two constructor
  name builders `constructorLayoutNames` and `constructorNamesOf` in `CoreLowering.ash`,
  called per parameter and per pattern name over every constructor of the stitched program,
  account for most of the samples, then `refineProgramParameterOwnership` (one list per
  fixpoint pass over every function) and the `ResultReachSummaries` path builders. Fixing the
  stage-0 copy is the leveraged fix; the selfhost can also stop building name lists for
  membership tests (a constructor name set in the lowering state). Fixed in both compilers
  (2026-09-13): the head admission was the gap. A cons whose tail is a call back into the
  producer already asks for a reference-counted cell, but a string or list bound out of a
  record or list pattern is a borrowed read of a value the pattern's root owns, never a
  runtime-managed element, so the cell stayed in the arena. Under that request the head now
  takes an owned reference-counted copy once per cell (`NormalizePatternOwnerElement`, a
  `CopyOutArena` for a string and a `CopyOutList` for a list of scalars or strings), the
  pattern-owner marker is not stacked on it, and the tail-modulo-constructor path no longer
  marks such a head moved, so the owner still releases its own reference. The 4,000-record
  builder went from 509 MiB to 8 MiB, its tuple and list-head variants likewise, and the
  BOOT-2 probe from exhausting 45 GiB to reaching MOD-14 in 25 s and 28 GiB. Tests:
  `NonTailRecursiveProducerTests` (string and list heads), the rewritten
  `List_rebuilt_by_consing_a_borrowed_head_onto_a_recursive_call_result_is_reference_counted`,
  and the parity fixture `producer_conses_record_string_head`. A head of any other type (a
  record, a list of records, an unresolved element type in a generic producer) still keeps the
  arena cell and the per-level copy.
- [x] **OPT-79** Stage 0 released a `let`-bound reference-counted value that a callee's result
  kept: `let methodName = nameOf(t) in add(t)([Method(name = methodName, ...)])(env)` stored the
  string into an arena record inside a list literal passed to `add`, whose result embeds the
  argument, and the caller borrowed the string into the record (an arena aggregate retains
  nothing) then dropped the binding at scope exit, so every seeded standard trait
  implementation of `standardTraitEnvironment` carried a dangling method name (`"default"`,
  then whatever reused the memory). The fresh-argument transfer of #743 covered only a
  reference-counted argument itself, not the owned bindings inside an arena aggregate argument.
  Fixed (2026-09-14): a call argument the callee's result may reach is lowered under the
  children transfer (`TransfersRuntimeManagedChildren`, the tail self-call argument's rule) in
  stage 0's `LowerCallArgumentValue` and the self-hosted `lowerCoreCallTyped`, so the stored
  binding is retained into the aggregate. Regression
  `tests/rc_child_of_call_argument_kept_by_callee_result.ash`, oracle fixture of the same
  name (the retain and release sequence of `addImpl` matches exactly in both compilers).
- [x] **OPT-81** FIXED 2026-09-15 by arming the loop exit's hand-over guard for a result that can be a bare read of a parameter the loop owns. The self-hosted pipeline now runs to completion for `Scope`, `Symbols` and `ExprMentions`, and `DerivingExpansion` reaches CG-18 in the backend. The investigation below is kept because its reduction and its two corrections are what found the fix.
  The self-hosted lowering segfaulted on a medium compiler input before any of the
  later blockers is reached (found 2026-09-15, and it is not a regression: it reproduces at
  `3274dc10`, before the OPT-80j slice). A scratch project whose entry is one `import
  AshesCompiler.Semantics.<Module>` plus a `print` crashes in `lowerCoreProgramWithSource` for
  `TypeInference`, `DerivingExpansion` and `ModuleSemanticStitching`, and compiles cleanly for
  `Types` and `TypeSchemes`, so the trigger is size or shape rather than a particular module.
  The fault is in `OwnershipInference.borrowReadHandOff` at the `match ownership with` of a
  recursive step. It is an early release, and it reduces to thirty lines with no flags:

  ```ash
  type Own =
      | Borrowed
      | Consumed

  let recursive pick (name: Str) (table: List((Str, List((Str, Own))))) (found: Maybe(List((Str, Own)))) =
      match table with
          | [] -> found
          | (candidate, values) :: rest ->
              if candidate != name
              then pick(name)(rest)(found)
              else
                  match found with
                      | Some(_) -> None
                      | None -> pick(name)(rest)(Some(values))
  ```

  Built over nine entries whose sub-lists are `[("runes", Borrowed), ("text" <> n, Consumed)]` and
  asked for `fn3`, this prints `runes=b text3=b`: the second element's tag reads `Borrowed`. Binding
  the table to a `let` and reading it again after the call segfaults instead. Three ingredients are
  each necessary. The sub-list's element must be a tuple carrying an ADT — the same loop over
  `List(Str)`, `List(Int)` or `List((Str, Int))` is correct. The body must inspect the accumulator
  before rebuilding it — replacing the inner `match found with` by a direct `Some(values)` is
  correct, and so is replacing the `None` arm by `found`. And the loop must carry the result across
  further iterations — an immediate `Some(ownership)` return is correct.

  Two earlier readings of this entry were wrong and are corrected here. It is not the hand-spliced
  curried environment chain. And the bad word is not stored: at the fault, the cell the walk
  dereferences is a live arena allocation whose first word is the length `5` and whose bytes are
  `runes`, a parameter name from `lib/Ashes/Text.ash`, so the block was recycled into a string.
  Under the release-poison knob the reduction stops printing a wrong tag and segfaults instead,
  which is the second independent confirmation that the value is read after release.

  Where it goes wrong: the loop exit releases the runtime-managed `found` accumulator, and on the
  `| [] -> found` arm the loop's result IS that accumulator. The hand-over guard that exists for
  exactly this (`rc_tco_exit_transfer_not_selected`) is emitted only when the result join is
  runtime-managed, and this join is mixed — `found` is reference-counted while the bare `None`
  beside it is an arena `AllocAdt`. That is the same mechanism as the returned-loop-parameter fix,
  one level up in the type system, where the join is an ADT rather than a string.

  Two attempted fixes were measured and do not work, so do not repeat them. Teaching
  `IsRuntimeManagedStringMatchArm` to recurse into a nested `match` the way it recurses into an
  `if`, and adding `IsRuntimeManagedLoopParameterTerminal` to `IsProvenFreshCallFunnelArm`, leave
  the reduction unchanged: at the point both predicates run, the accumulator's placement is not yet
  decided, so the loop-parameter terminal reports false. A trace of the ADT terminals also shows the
  correct `List(Str)` loop and the broken `List((Str, Own))` loop classifying identically (`found`
  not a funnel, `None` not fresh) and both dropping the accumulator through `__rcdrop_4` at the
  exit, so the freshness machinery is not the differentiator either. What separates them is further
  down, in what the structural release of the table reaches.

  The probe is a stage-0-compiled driver (so it carries symbols and DWARF) that runs `loadProject`,
  `stitchProject`, `lowerCoreProgramWithSource`, `optimizeIrProgram` and `codegenProgram` in turn,
  printing a marker after each; build it with `--debug` and run it under `gdb -batch -ex run -ex
  bt`. Its manifest needs `AshesCompiler.Frontend` and `AshesCompiler.Semantics` listed as
  devDependencies beside the path override, or project loading fails with a null reference. The
  stage-1 CLI crashes on the same input in the same place, without symbols.

  Measured further (2026-09-15). The fault needs at least two elements in the sub-list: the same
  program with `[("x", Borrowed)]` is correct, and with two or three elements it segfaults outright,
  so a literal two-entry table is enough and no `buildTable` loop is needed. A watchpoint on the
  returned sub-list's reference count shows it go `3 -> 2 -> 1 -> freed`, every transition inside the
  single arm that builds `Some(values)`, where the correct count after that arm is 2: one reference
  from the table the loop owns and one from the `Some` cell. One decrement too many. The `Consumed`
  cell of the second element is released twice, once from the `candidate != name` arm of the next
  iteration and once from the loop exit. The control that removes only the accumulator inspection
  emits the same reference-counting shape in that arm — dup the pattern owner, release it
  structurally, dup the tail — so the divergence is not in what that arm emits.
  The gdb recipe: break on the generated `__rcdrop_structural_*` and `__rcdrop_*` droppers, where the
  dropped value arrives in `rsi`; the reference count is the word at payload minus 16 and the
  allocation size the word at payload minus 8. Print both, then arm a watchpoint on the count word of
  the value the first structural drop reports.

  Confirmed at the release site (2026-09-15). A single-entry table is enough, so the loop need not
  carry the result past another entry. Reading only the label of each element, never the ADT beside
  it, still faults, so the whole returned list is gone rather than just its ADT children. And the
  last release before the fault is the ADT dropper called from the function header line, which is
  where the loop exit's drops are attributed: the exit releases the accumulator, the accumulator is
  what the `| [] -> found` arm returns, and the dropper walks into the list and frees its elements.
  That is the mechanism named above, now observed rather than inferred.
- [x] **OPT-77** Stage 0 deep-copies a loop's accumulator at entry for an in-place reuse
  specialization that never runs, and for one that does but cannot pay for the copy. A loop
  parameter passed to a specializable function is scanned as a specialization candidate and,
  when the whole-program move analysis cannot prove it unique, deep-copied once at loop entry
  so an `f$reuse` clone may rewrite it in place. Two gaps found in the BOOT-2 probe
  (2026-09-13): the copy is kept even when every such call was qualified away (`getStr` on a
  map is a pure reader, "result does not rebuild accumulator", so no clone is generated and the
  copy only duplicates the map), and when the clone is `Map.setStr`, a helper loop such as
  `enqueueDependents` re-entered from `solveReach` once per changed summary copies the whole
  queued-set map (about 30,000 nodes) on every entry to save an O(log n) path rebuild, because
  the argument is the result of an over-applied nested-`go` call (`setStr(k)(v)(map)`) that
  `OverApplicationReach` poisons, so `ResultAliasUnsafe` rejects the elision. The first gap is
  fixed on a branch (a `NoSpecializedCall` entry-copy outcome that omits the copy unless a call
  was routed to a clone for that accumulator; `SpecializationCandidate_OmitsEntryCopyWhenNoCallIsSpecialized`).
  The second needs either an over-application reach for the nested-`go` shape so the
  elision can prove the argument moved, or a cost gate that declines the copy when the
  clone's rebuild is a path rather than the whole accumulator; measured with the copies
  disabled, the probe's lowering still needs 43 GiB (OPT-74 dominates), so this is not the
  first blocker. Fixed in both compilers (2026-09-13) with the cost gate: a specialization
  candidate whose accumulator is unproven takes no entry copy when the callee's recursive body
  (its nested `go` or its own innermost body) makes at most one self call on any path over a
  type whose constructors hold two or more children of their own type
  (`SpecializationRebuildsOnlyAPath`, reported as `path rebuild not amortized`), and none
  either when no call was routed to a clone for it (`no specialized call`); the copier is
  synthesized only once the copy is wanted, so a declined copy leaves no dead copier. A
  whole-structure rebuild (`update` with two self calls, a list map) keeps the gamble. The
  self-hosted lowering mirrors both on the single-parameter candidates it scans, with a
  path-sensitive `maxPathOccurrences`. Tests: `ReuseDecisionTests` (reader over a chain, path
  rebuild versus whole rebuild) and the parity fixture `reuse_path_rebuild_declines_copy`.
  The BOOT-2 probe went from 28 GiB to 12.9 GiB and 21 s, reaching MOD-14; the map copies
  under `enqueueDependents` and `shadowNames` that dominated the gdb sampling are gone.


### LLVM code generation and runtime integration

- [x] **CG-10** Every fixed-size runtime-helper scratch `alloca` (syscall scratch such as `timespec`/
  `pollfd`/`termios`/`stat` buffers, the read-line and subprocess pipe buffers, `spawn`'s argv
  vector, print/format buffers, arena copy-out/reclaim slots, and the branch-merging result slots
  the `phi`-free codegen uses) goes through `Llvm.ash`'s `buildEntryAlloca`, which places it in the
  current function's **entry block** and restores the builder to the block it was emitting — the
  self-hosted backend emits at optimization level none, where a fixed-size alloca in any other
  block is a runtime stack-pointer adjustment that only function return reclaims, so one inside a
  jump-based loop body leaks native stack every iteration. The helper derives the function from
  the builder's current block rather than from a threaded `function_`, so a runtime helper
  synthesized into its own function hoists into that function's entry. The genuine `AllocStack`/
  `MakeClosureStack` path (`emitStackAlloc`, managed by its own save/restore bracket) keeps its
  loop-body alloca on purpose. Guarded by the backend suite's hand-built `Label`/`Jump` loop around
  `MonotonicMillis` (two million iterations, a stack overflow before the hoist); every loop the
  self-hosted lowering produces today is a `musttail` self call, so a source-level program cannot
  yet observe the leak.
- [x] **CG-16** Lower the builtins the self-hosted compiler still rejects with
  `UnknownLoweringBinding`: `Ashes.Text.fromBigInt`, `Ashes.Text.formatFloat`,
  `Ashes.Rune.isAsciiLetter`, and `Ashes.Internal.deepCopy`, each through the semantic builtin
  table and the backend emitter, placed by the runtime-managed flag like the other fresh-value
  builtins. They are what keeps `tco_fixed_watermark_whole_value_accumulators`,
  `tco_list_of_adt_accumulator`, `runtime_rc_branch_late_tco_promotion`,
  `runtime_rc_multi_bigint_tco`, and `runtime_rc_whole_string_pattern_recursion` out of the loop
  sweep and `stdlib_string` out of the compile-time timing set. `Ashes.Trait.Show.show` and
  `Ashes.Trait.Hash.hash` (the remaining sweep and timing failures) are trait dictionary lowering
  and stay under TRT-13..TRT-15. Done (2026-09-06): the rejections came from the builtin type
  table (`standardBuiltinLayouts`), which lacked the schemes although the kinds and their
  emission arms existed, so `Text.fromFloat`, `Text.formatFloat`, `Text.asciiUpper`,
  `Text.asciiLower`, `Rune.isAsciiLetter`, `Rune.isAsciiDigit`, `Rune.isAsciiWhiteSpace`, and
  `Internal.deepCopy` now carry theirs (`deepCopy` under a fifth reserved type variable), the
  ASCII case builtins honor the runtime request, and `deepCopy` lowers through the structural
  copier synthesis (`lowerInternalDeepCopy`, an argument whose type is still a variable passing
  through and recorded for the body's second lowering, as stage 0 defers it). The backend gained
  the float text emitters (`IrCodegen.FloatText.ash`: `TextFromFloat` and `TextFormatFloat`
  written forward into one stack buffer, sign, integer digits, fraction with half-up rounding
  and carry, trailing-zero trim for `fromFloat`, the `e+N` form past the signed 64-bit range,
  the decimal count clamped to 0..18) and the ASCII case mapper (`IrCodegen.AsciiCase.ash`),
  both placed by the flag; `tco_list_of_adt_accumulator` joins the sweep and the backend suite
  pins float text, case mapping, the rune class, and the deep copy. Done (2026-09-06): the
  BigInt runtime, the milestone 6 slice of CG-11 pulled forward because `Text.fromBigInt` and
  the arithmetic the three BigInt fixtures compute with need it. `IrCodegen.BigInt.ash` ports
  stage 0's `LlvmCodegenBuiltins.BigInt.cs`: the internal helper functions
  (`bi_normalize`/`bi_cmp_mag`/`bi_add_mag`/`bi_sub_mag`, `bignum_from_i64`/`_cmp`/`_add`/
  `_sub`/`_mul`/`_divmod`/`_to_decimal`/`_from_decimal`) emitted once per program whose IR
  carries a BigInt instruction, division as Knuth's Algorithm D over 32-bit digits, and the
  call sites (`BigIntFromInt`/`ToString`/`ToInt`/`FromString`/`Binary`/`Compare`) that read the
  operand limb counts and place the result and scratch buffers where the instruction asks (a
  reference-counted cell released after the call for a scratch, an arena value or block
  otherwise). The `Ashes.Number.BigInt` members and `Text.fromBigInt`/`parseBigInt` carry their
  schemes, so `tco_fixed_watermark_whole_value_accumulators`, `runtime_rc_branch_late_tco_promotion`,
  and `runtime_rc_multi_bigint_tco` join the sweep (51 ok); a 33-case probe (sums past a limb,
  the most negative machine value, forty-digit quotients and remainders of both signs, products,
  comparisons, the machine conversions at both ends of the range, and parses of valid, empty,
  sign-only, and malformed text) matches stage 0 byte for byte, and the backend suite pins a
  representative slice. Found and fixed on the way, not a BigInt defect: the self-hosted
  lowering overflowed its 8 MB native stack on a top-level owned `let` followed by a trailing
  pipe chain of some thirty stages, because placement's dominator computation
  (`IrControlFlowGraph.ash`) walked the blocks with a cons recursion per block and intersected
  dominator sets with one per element, and a deep block's dominator set holds every block above
  it; both walks now gather through an accumulator, and `CoreProgramLoweringTests` lowers a
  forty-stage chain under such a `let`. `runtime_rc_whole_string_pattern_recursion` now stops at
  SEM-18's comparison default instead.
- [x] **CG-18** FIXED 2026-09-15: the deferred call-result copy-out found its reload by position,
  and a loop frame's entry splice moved it. A call whose result type is still unresolved when the
  call closes its arena window stores the result into a local and reads it back, and
  `resolvePendingTcoResets` walks the finished body to put the copy-out block between that store
  and that reload. The walk matched the reload by an instruction index recorded when the copy-out
  was registered, but `spliceTcoEntryNormalization` and the direct-reuse entry copies insert their
  instructions at the loop entry once the body is lowered, so every index recorded inside that
  body is short by what they inserted. In `DerivingExpansion.variablePatterns` the index then
  named the conditional argument retain's reload, three instructions earlier: the walk removed
  that reload and renamed the real one's temp, leaving the self call reading a temp nothing
  defines, which the backend reported as `codegen: unknown index 27 bound=26`. The fix drops the
  index and finds the reload by the instruction itself, the one `LoadLocal(reloadTemp,
  resultSlot)` — which is how stage 0's `SpliceDeferredPlaceholders` finds it, through a
  `CallResultCopyOutPending` placeholder instruction, so stage 0 was never affected. Fifteen lines
  reproduce it: a non-tail cons producer over a self-referential ADT whose element layout is
  unresolved at the self call. Regression:
  `selfhost/tests/semantics/DeferredCallCopyOutTests.ash`, which lowers that producer and asserts
  the self call's argument temp is defined before the call reads it.


### Object parsing and executable linking

- [x] **LNK-5** Diagnostic slices landed alongside the builtins, each with stage 0's exact message text:
  reserved built-in runtime type names rejected in top-level `type` declarations, explicit
  lambda-parameter type annotations enforced (previously silently discarded), and a `perform`
  whose target is not a capability operation rejected.

### Optimization, ownership, and reuse

Completed work of **OPT-25**, whose open tail is in the [plan](SELF_HOSTING.md). Inserting Perceus
duplication and drop operations, and deterministic resource cleanup, across ordinary, exceptional,
handler and coroutine control flow.

  Done: arena save/restore/reclaim
  brackets around every flat top-level `let`, nested `let` chain binding (closing LIFO after the
  innermost body), `match` arm on the linear dispatch path (save before the pattern test;
  restore/reclaim on both exits, the arm's own `match_arm_cleanup_N` block jumping on to the real
  fail target), and general call spine (opened before the callee, closed after the last
  application), each closed under stage 0's scope rule: reset when the result's resolved type
  survives a reset (scalars and zero-cost wrappers of them; an operator-defaulted variable counts
  as `Int`), otherwise left open (copy-out is not ported). The rule is type-directed, so the
  context's expected type is threaded through the lowering state as stage 0's
  `LoweredValueRequest.ExpectedType`: a `let`, recursive binding, lambda, `if`, `match`, `handle`,
  call, list literal, and cons forward it to the parts stage 0 forwards it to (the else branch
  expects the then branch's type, a call argument its parameter type, a list element its element
  type, a lambda pins its parameter type from it), every other expression is unified with it
  afterwards, and a general call constrains its spine's result with it before any argument is
  lowered — so a sibling call inside a recursive-group member, whose result type is unresolved on
  its own, still resets its window. A constructor allocates in the arena unless its consumer
  requests an RC cell (`runtimeAdtRequested`, consumed at instantiation), the dead top-level `let`
  path being the one requester. Reads of owned bindings emit stage 0's `Borrow` alias; a bracketed
  `let` that owns its binding spills the body result to a slot across the closing restore
  (`closeOwnedLetBracket`) and anchors the release at the scope exit; the ported
  `PerceusLifetimePlacement` (over `IrControlFlowGraph`) re-inserts each drop at the control-flow
  precise last use per block, with compensating `RcDup`s for borrowed closure arguments and
  record-field stores. Both compilers follow the owner through the same aliases: `Borrow`, a slot
  holding an alias (only for loads a store of the alias can reach: a load earlier in the store's
  own block reads the previous value, the loop parameter a back edge releases before storing its
  successor), an arena cell that embeds an alias without a reference of its own (a list
  literal's cons cell, a tuple, a closure environment), a closure made over such an environment,
  and the result of a call that receives an alias as its argument, closure, or environment (the
  callee may carry the pointer out, and the copy-out past the call window reads it). Before the
  cell and call-result rules the drop of a `let`-owned string embedded in a list literal landed
  right after the store, before the consuming call or the copy-out of its result — silent for a
  recycled small string, a segfault for an OS-backed one of 4 KiB or more (`Text.join` over a
  built line); regression `tests/list_literal_keeps_built_string_alive_across_call.ash`. A join
  block that some predecessor reaches without the owner (a path that already released it) no
  longer takes the drop at its entry, which ran twice on that path; the live branch's edge gets
  its own block (`<function>_rc_edge_<slot>_<block>`: drop, then jump to the join) — the
  `pattern_match` fixture's second arm cleanup now shows that block, and the widened aliasing
  had otherwise double-freed a runtime-managed string in the CLI test suite's tree rendering.
  Tagged constructor patterns on the linear path guard the tag test with
  `ptr != 0` in stage 0's temp order; the tag-group path binds fields under the switch without
  either. Closures carry stage 0's origins (`SourceFunction from <let name>`, `ClosureHelper` with
  the `lambda:<start>:<length>:<param>` discriminator, anonymous helpers), a scalar-capture
  environment normalizer (`<label>$env_normalize`), and a let-bound lambda used only as a direct
  callee is a `MakeClosureStack`. Runtime-managed strings follow stage 0's
  `LoweredValueRequest`: a consumer that keeps a fresh string alive (a direct binding result, an
  immediate `Text.length`/`byteLength`/`IO.print` use) asks the fresh-string builtins
  (`fromInt`/`fromFloat`/`formatFloat`/`fromBigInt`/`toHex`/ASCII case/`Rune.toText`/
  `Bytes.subText`) for an RC result (`RuntimeManaged=true`), the consumed operand of a
  print/write/`byteLength`/concat is released right after the use (`RcDrop ... RuntimeManaged`
  on a newly produced temp), a `let` adopts its RC value as an owner released at scope exit
  unless its body tail-forwards the binding (the read then transfers ownership without a
  `Borrow`), a lambda whose body produces an RC value is a `MakeClosure ReturnsRuntimeManaged`
  and a single-argument call to it marks its result newly produced, and a scope that owned and
  released a binding closes with stage 0's `PopOwnershipScope` copy-out: a heap result that
  cannot survive the reset but has a copy-out kind (a string or `Bytes`, a list over scalars, a
  same-arity scalar-field ADT) is copied past the reset as an RC-normalized
  `CopyOutArena`/`CopyOutList`, the ADT's static size counting its tag word only when the type
  is not tagless. Every `match` arm is bracketed on every dispatch path: the tag-group
  (`SwitchTag`) path brackets each linearly tested group case with its own `match_arm_cleanup_N`
  block (the `match_group_next_N` label allocated first) and a trivial single-case group on its
  success path only, and the capability-operation arms bracket like linear arms. An arm's
  pattern bindings are stage 0's `TrackOwnedBindingsInPattern` owners (a resource, or any
  heap-typed binding by its owned type name), released at the arm exit after the result store
  (`RcDrop ... OwnerSlot=N`, moved to the last use by the placement pass), and an arm that owned
  a live binding closes with the same `PopOwnershipScope` copy-out as an owned `let`, the copy
  replacing the result in the match slot; a record field receiver is loaded without the
  owned-read `Borrow`, as stage 0's `TryLowerRecordFieldLoad` does. The `let_bindings`,
  `nested_let_scopes`, `scalar_match`, `ownerless_match`, `pattern_match`, `closure_capture`,
  `heap_result_builtin`, `heap_result_let`, `heap_result_list`, `record_pattern`,
  `tag_group_arm_brackets`, and `match_arm_copy_out` fixtures match stage 0 byte-for-byte,
  source locations included (`MatchArmScopeTests.ash` covers the list and tagged-ADT arm
  copy-outs, the lambda arm's pattern-owner release, and the operation-arm brackets). A self-recursive tail call is still a `CallClosure`; the backend fuses it
  into a `musttail` when the instruction past the call's own window close stores or returns its
  result. A function whose parameter always reaches its result (`ResultReach.ash`, stage 0's
  `ResultAlwaysReachesVariable` over the parsed tree, following saturated calls into the
  let-bound callees the lowering already records) normalizes a string or ADT argument at entry:
  the `rc_arg_normalize_copy`/`rc_arg_normalize_done` block reads the hidden ownership flag
  (`LoadArgumentOwnership`), copies a borrowed argument into an owned value (an RC-normalized
  `CopyOutArena` for a string or a same-arity scalar-field ADT, a `CopyOutList` for a list over
  copyable heads, the per-child deep copy for a tuple and a runtime-managed ADT, single- and
  multi-constructor), stores it back into the argument slot ahead of the body, and the closure
  carrying the function is a `MakeClosure`/`MakeClosureStack AcceptsRuntimeManagedArgument`;
  the normalized functions of the `parameter_reaches_result_string`,
  `parameter_reaches_result_record`, and `parameter_reaches_result_record_update` fixtures
  match stage 0's text (`ResultReachTests.ash`). A general call closes its window under stage 0's
  `LowerCallRestoreArena`: a scalar result, or a result the callee is known to place on the RC
  heap (a single application of a let-bound function whose lowered body produced an RC result
  of a runtime-manageable type, or of a heap type without any copy-out), resets the window; any
  other result whose type has a call copy-out (the scope kinds plus lists of strings and of
  scalar lists, `GetCallCopyOutKind`) reads the callee's `ReturnsRuntimeManaged` bit before the
  call and crosses the reset through the conditional `call_copy_arena_result` /
  `call_reclaim_owned_result` block, the reloaded slot value being the RC result; a
  self-recursive callee keeps the plain scope rule so the backend's tail fusion still finds the
  call adjacent to its return. On the argument side (`LowerAppliedClosureCall`), an RC argument
  (a fresh runtime temp, or a binding that owns one) to a parameter the callee does not borrow
  reads the callee's `AcceptsRuntimeManagedArgument` bit: a fresh argument the callee's result
  keeps, or that an entry-normalizing callee adopts (`runtimeNormalizedArgumentLabels`), passes
  as is; a named binding the result may keep is retained unconditionally; any other is retained
  under the bit through the `rc_call_argument_not_retained` slot. The flag rides on the
  `CallClosure`, and a fresh string, `Bytes`, `BigInt`, closure, or childless-ADT argument the
  callee did not take is released after the call. `CallOwnership.ash` holds the pure rules
  (copy-out kind, callee borrow and reach facts), and the reach analysis poisons a call through
  a qualified or computed callee as stage 0 does. The `call_result_copy_out` and
  `call_argument_retain` fixtures join the byte-identical set. A saturated application of a
  curried let-bound function follows stage 0's returned-closure chain
  (`functionReturnedClosureLabels`, recorded from the last closure instruction producing a
  body temp) to the innermost stage's recorded placement, the known-result decision is gated by
  the RC-eligibility provenance (`CallResultProvenance.ash` classifies each let-bound function's
  terminal arms as stage 0's `BuildProvenanceFunctionNode` does and `OwnershipProvenance.ash`
  solves the forwarding fixpoint; a body-RC but non-eligible callee still reads the returns
  bit), a `ConcatStr` carries `RuntimeManaged=true` when its consumer asked for a runtime string
  (its operands lowered without the request, its result newly produced, a newly produced
  operand released after the use), and a consumed list, tuple, or ADT argument is released
  through stage 0's inline `rcdrop_list`/`rc_drop_tuple_shared`/`rc_drop_shared` walks (the
  spine-only and shallow releases when the callee's arena result may keep the parts, the
  constructor-switching dropper for a recursive or owned-child ADT); the
  `consumed_list_argument` fixture joins the byte-identical set, while
  `curried_known_call_result` and `concat_runtime_result` match stage 0 up to the trait-evidence
  header and the curried inner lambda's locations (`CallWindowLoweringTests.ash`). A fresh runtime-managed scrutinee
  matched directly (a call result or a nested match result that is a string, `Bytes`, or a list
  over scalars) is owned by the arm that matched it, stage 0's `$match_rc_N`: after the pattern
  test and guard the arm stores it into an owner slot of its own, releases it at the arm exit
  (`RcDrop ... OwnerSlot=N RuntimeManaged=true`, moved to the store by the placement pass; a
  list owner walks its spine inline through the `rcdrop_list_N` loop, stage 0's
  `EmitRuntimeManagedListDrop`), and closes with the owned-scope copy-out; an arm whose pattern
  binds the whole scrutinee or a heap value out of it takes no owner. The match result carries
  stage 0's `MarkRuntimeManagedMatchResult` status: runtime-managed when every arm stored a
  runtime-managed value (the empty list literal of a list-typed join counts), newly produced only
  when every arm's was, so a lambda whose body is such a match is a `ReturnsRuntimeManaged`
  closure and a known call to it resets its window. Beside a fresh-string arm, a literal string
  arm of a guard-free match is normalized to an RC-normalized `CopyOutArena` of the constant
  (`ShouldNormalizeStaticStringMatchArms`; stage 0 applies no such rule to `if`). With a
  capability in the program, an arm's closing reset and its cleanup block's reset run under the
  live-posts guard (`live_posts_skip_N`, the counter one past the pending-post register). The
  pure rules live in `MatchArmOwnership.ash`; the `match_rc_scrutinee` and
  `match_list_scrutinee_drop` fixtures join the byte-identical set, and `MatchArmScopeTests.ash`
  covers the guarded arm resets under a `handle` at the expression level (the
  `handle_match_arm_reset` fixture stays out of the runner: the single-file lowering takes no
  capability declarations). The TCO loop of a self-recursive
  function is lowered as stage 0's loop rather than a call the backend fuses: the chain
  parameters the loop body captures move into local slots at entry (`LoadEnv`, `StoreLocal`), an
  unread or shadowed parameter gets a zeroed synthetic slot, the fixed loop-entry watermark, the
  compaction-size slot, and a reservation slot pair per affine accumulator
  (`TcoAffineAppend.ash`, stage 0's `ComputeAffineSelfAppendOrdinals`) precede the
  `lambda_N_body` label, the per-iteration watermark and `SaveStackPointer` follow it, and the
  back edge evaluates every argument into a temp under the children transfer, loads the old
  parameters, stores the new ones, releases the iteration-local runtime owners, resets the
  per-iteration watermark when every argument's resolved type survives a reset (both resolved
  through a `TcoResetPending` placeholder once the function body is lowered, the pre-restore
  slot and release temps allocated after the body's own), restores the stack pointer, and jumps;
  the backend emits `SaveStackPointer`/`RestoreStackPointer` through `llvm.stacksave`/
  `llvm.stackrestore`. The `tco_scalar_loop`, `tco_scalar_owned_let`, and
  `tco_unused_chain_parameter` fixtures join the byte-identical set; `TcoLoopLoweringTests.ash`
  holds the loop-function comparisons of `tco_list_walk` (identical loop function; the program
  lacks the list capture's closure normalizer and dropper) and
  `tco_non_tail_self_call_in_operator_operand` (the scalar loops match except for the window
  reset of the non-tail self-call under the operator, whose result type stage 0 infers before
  lowering). Runtime-managed loop parameters (stage 0's `RuntimeManagedSlotsInOrder`) are now
  ported for the `Str` shape: `TcoRuntimeManagedParams.ash`'s `runtimeManagedStrOrdinals` decides,
  from the loop's raw body before lowering, which parameters are rebuilt only through `+` — read
  through a plain-variable alias first — or passed straight through at every tail self-call, and
  `CoreLowering.ash` splices the entry normalization at the recorded loop-entry point once the
  whole body's types resolve (the function's own direct argument reads the caller's hidden
  ownership flag via `LoadArgumentOwnership`, stage 0's `EmitRuntimeManagedTcoArgumentNormalization`;
  a captured chain parameter is always copied, stage 0's `EmitRuntimeManagedTcoParamCopy`) and
  sets the slot's active flag; the tail self-call's own `+` chain rooted at the parameter is
  armed (`affineAppendContextFor`, stage 0's `LowerCallTcoArmAffineStringArg`) so the
  concatenation is the reservation-growing `ConcatStrTip` over the loop's reservation slot pair,
  promoted to `RuntimeManaged=true` once the parameter's placement is final
  (`promoteAffineAppends`, stage 0's `PromoteRuntimeManagedStringConcats`); the back edge takes
  the runtime-managed reset for a `Str`-only loop as well (the in-place append consumed the
  predecessor's reference, so nothing is released; any other successor is copied out to the
  reference-counted heap and the predecessor released under the active flag; the fixed
  loop-entry watermark is restored and the successor stored with its flag set), and the exit
  transfer-checks the slot against the loop's result under the flag, the copy-ADT slots' own
  check, with the loop's closure advertising a runtime-managed result. The backend grows the
  reservation in place when the left operand is the recorded reservation and the bytes fit,
  and otherwise concatenates into a fresh doubling-headroom allocation placed by the flag,
  releasing the old reference-counted accumulator and recording the new bounds
  (`emitConcatStrTip`, stage 0's `EmitConcatStrTip`). The accumulator's loop function of
  `tests/tco_runtime_managed_str_accumulator_plateau.ash` matches stage 0's text; run through
  the backend suite next to a 200000-element `List(Str)` consumed through its own pattern-owned
  tail, the fixture went from 19.8 GB of RSS and 1.1 s (every append copied the whole
  accumulator into the arena, no back-edge reset) to 47 MB and 0.01 s. A single-parameter loop
  (`let recursive loop n = ...`) now runs the same placement finalization as a curried one (its
  body is entered straight from the recursive binding, which used to finish without it, leaving
  every active flag unretired and unnormalized). Verified (2026-09-09): the `let`-bound append
  form (`let acc2 = acc + rhs in loop(n - 1)(acc2)`) already takes the in-place `ConcatStrTip`
  path, not the arena back-edge copy-out this note once flagged as open — stale by the time it was
  read again, per `TcoRuntimeManagedParams.ash`'s own header (a later port, `inlinePlainVarAliases`
  over `TcoAffineAppend.ash`'s existing walk). `tests/tco_affine_string_append_let_bound.ash`
  (`// expect: 200000 09876543210987654321 30`) already pins exactly this shape and passes
  byte-for-byte through the self-hosted compiler; a hand-built 200000-iteration probe of the same
  shape plateaus at 6.2 MB through the self-hosted CLI (stage 0: 8.4 MB), confirming the affine
  reservation, not a growing arena copy, is what runs. Still open, root-caused (2026-09-09): an
  append whose operand type is still a raw type variable at the point it is lowered speculates
  `AddInt` and records the target temp in `pendingOperatorDefaults`
  (`emitDeferredCoreAdd`/`deferredCoreOperatorEmitter`, `CoreLowering.ash`); once the whole
  program's substitution is final, `resolveDeferredOperator` seals a `Str`-resolved deferred add
  unconditionally to a copying `ConcatStr`, never consulting `state.affineAppendReservation` the
  way the immediate path's `emitCoreConcat` does — so a self-append that would otherwise qualify
  for the in-place `ConcatStrTip` reservation loses the optimization if its operand type happened
  to still be a variable at lowering time, even though `binaryAffineReservation` already computed
  the reservation before the operand type check ran. A minimal reproduction was attempted and did
  not succeed: a self-recursive loop's own accumulator append needs a trait witness for `+`
  resolved at loop-definition time, so an accumulator used nowhere else in the loop's own body is
  an ambiguous `Ashes.Trait.Add(a)` (`ASH010`) in stage 0 itself, not a deferred-then-resolved
  case; the shape the deferred path actually exists for (`Text.join`'s `pairwiseGo`, cited by the
  code's own comment) accumulates a `List` of merged strings rather than growing one string in
  place, so it was never an affine-reservation candidate regardless of timing. The natural
  candidate for a genuinely late-resolved self-append — a mutually recursive accumulator loop,
  where a sibling member's body fixes the shared element type — depends on the mutual-recursion
  loop merge this note's own list below still has open (milestone 5's OPT-19), so this gap's
  practical exposure is unconfirmed rather than ruled out. A `List`-typed
  parameter is placed the same way in two self-call shapes (`TcoRuntimeManagedParams.ash`'s
  `tcoSelfCallShapes`, stage 0's `TcoSelfCallArgumentShape` walk over the loop body's `if`
  branches, `match` arms, and `let` bodies): grown by one cons cell per iteration onto the
  parameter's own value (every back edge's cell allocated on the reference-counted heap, its head
  a fresh producer or a retained owner) or consumed through its own pattern-bound tail over heap
  heads whose arm keeps them borrowed (`namesBorrowedOnly`: an operator operand, a scrutinee, a
  condition, a field read, or an argument of a borrowing builtin — a head passed to a self-call
  or user function, consed, stored, returned, or captured keeps the list in the arena, since no
  pattern-owner protective duplicate is ported). The resolved element must support the
  runtime-managed accumulator layout and a spine copy (`listHeadCopyKindOf`), and a sibling that
  permanently blocks the frame's reclaim (stage 0's `IsPermanentlyBlockingTcoParam`) demotes every
  list candidate. Each candidate's active flag is allocated at loop entry; an admitted slot is
  normalized at entry (`CopyOutList` under the ownership flag for the direct argument, unconditional
  for a captured chain parameter) with its flag set, every back edge with a runtime-managed list
  takes stage 0's runtime-managed reset (`TcoBackEdgeTryEmitRuntimeManagedReset`: a consumed
  tail's successor is retained null-tolerantly and the old root released under the flag, the
  iteration owners released, the fixed loop-entry watermark restored, the successors stored with
  their flags set, the chunks reclaimed), and the exit releases each slot under its flag through
  the shared-cell `rcdrop_list` walk, transfer-checked against a list-typed result whose direct
  read of a slot also marks the function's result runtime-managed. Verified at 200000 iterations
  by `tests/tco_runtime_managed_list_accumulator_plateau.ash` through the backend suite and by
  `TcoOwnershipRulesTests.ash`. A consumed list whose heads outlive their arm (a head forwarded
  to another parameter) is admitted too, its heads protected by their pattern owners' retains
  (string-like heads first, aggregate heads once the promoted release named the structural
  dropper, see OPT-25's note); the active flag a list-shaped parameter gets
  at the loop entry is retired from the function's slots when the resolved types keep the list
  in the arena, so the numbering stays stage 0's. A single-constructor copy ADT parameter (every
  field a scalar, stage 0's `CanCopyOutAdt`) is placed on the reference-counted heap too: its
  cell is copied out under the caller's ownership flag at entry, every back edge copies the fresh
  arena successor out (`CopyOutArena` by the cell's size) and releases the predecessor under the
  active flag before the loop-entry watermark is restored, and the exit transfers the value to
  the caller when it is the body's result or releases it; a loop that rebuilt a record every
  iteration went from 50.7 MB to 5.6 MB of RSS at three million iterations
  (`tests/tco_runtime_managed_record_accumulator_plateau.ash`). A tuple of scalars takes the same
  placement under the type name `Tuple`
  (`tests/tco_runtime_managed_tuple_accumulator_plateau.ash`, 50.7 MB to 5.6 MB). A record with
  owned children (a `Str` field) copies its children with the cell at entry and at every back
  edge, releases the dying arena successor's own child references after the copy, and releases
  the predecessor and the exit value through the cell's inline structural walk (`RcIsUnique`,
  the children, `rc_drop_shared`, the cell), as stage 0's
  `EmitRuntimeManagedTcoConstructorDeepCopy` and `EmitRuntimeManagedAdtDrop` do
  (`tests/tco_runtime_managed_owned_child_record_accumulator_plateau.ash`, 145 MB to 5.6 MB). A
  record of records takes the same path once the classification admits it (see OPT-23's fix),
  with the nested cells copied recursively
  (`tests/tco_runtime_managed_param_field_read_into_successor.ash`, 30 MB to 5.6 MB). FIXED in
  both compilers (2026-09-05): the back edge released every owned child of the dying successor as
  a reference-counted cell, but a child the construction built as a fresh literal
  (`Pair(previous = State(...), ...)`) is an arena cell with no header — stage 0 decremented
  whatever word preceded it and the self-hosted backend handed the pointer to `free`. The successor's
  argument expression now reaches the back edge (`PendingTcoReset.ArgExpressions`,
  `CoreTcoReset.argumentExpressions`), and a literal child releases only the references it holds,
  recursively through nested constructor, record, tuple, list and cons literals
  (`TryEmitRuntimeManagedTcoLiteralChildrenRelease`, `emitLiteralChildrenRelease`). A list
  accumulator over record heads (`collect(n - 1)(State(...))(s :: acc)`) is admitted with the
  record parameter it conses: the entry normalizes the borrowed list through stage 0's
  `rc_normalize_list` walk (`ListDeepArgumentCopy`, each head deep-copied into a fresh
  reference-counted cell), a loop-parameter read consed into a sibling accumulator or into the
  exit arm's cell takes the retain marker the constructor path already took (`retainListElement`,
  `retainConsTail`; a parameter's own read inside the argument that rebuilds it is the reference
  the back edge moves, `backEdgeArgumentSlot`), and a cons cell around an admitted parameter's
  read is placed on the reference-counted heap as it is lowered (`loopParameterIsRuntimeManaged`,
  the frame's later demotion never reclaims the arena at the back edge, so an early placement
  never dangles). The loop's lowered IR matches stage 0's
  (`tests/tco_runtime_managed_record_list_accumulator.ash`). A `Str` parameter the affine
  analysis declines (read a second time outside its own successor, or rebuilt from a producer
  not rooted at it) is placed by its type through the same ADT-slot machinery as stage 0's
  `IsRcEligibleScalarTupleOrAdtType` places it: its cell plan is the one-piece string copy, its
  fresh successor is asked for a reference-counted string (`tailSelfCallStringSuccessor`) so
  the back edge stores it directly, the old value is released under the active flag, and the
  exit transfers or releases it; the affine in-place append keeps its own path
  (`tests/tco_runtime_managed_str_param_non_affine_plateau.ash`, and
  `tests/tco_runtime_managed_param_consed_into_sibling_accumulator.ash` now takes the
  reference-counted path). Tuple literal elements take the loop-parameter retain marker too
  (stage 0's `RetainRuntimeManagedTupleChildren`). A list parameter rebuilt as a fresh literal
  at every back edge (`step(n - 1)([fromInt(n), head])`, stage 0's `FreshListRebuild` fact) has
  its own self-call shape (`TcoFreshListShape`: a list literal, or a cons chain ending in one)
  and takes the ADT-slot placement under the type name `List`: the entry normalization and the
  back-edge copy are the list's own copy plan (`CopyOutList` over string or scalar heads, the
  cell-by-cell walk over record heads), the old value releases through the list walk, and a
  pattern owner extracted from such a parameter is placed by the ADT-slot entries as well
  (`patternSiteRootManaged`). A divergence fixed on the way: the self-hosted list-literal
  lowering took a pattern-owner duplicate at every element, which stage 0 takes only at tuple
  elements, constructor arguments, cons parts and tail-call arguments, so a matched head consed
  into a copied-out literal leaked one reference per iteration
  (`tests/tco_runtime_managed_fresh_list_rebuild_plateau.ash`, 22 MB to 5.6 MB). A
  body result that reloads an
  `if`/`match` join every branch stored a runtime-managed slot's read into marks the function's
  result runtime-managed, as a direct read did, through nested joins as well (an `if` whose
  branches both take the back edge inside a `match` arm). A consumed list whose matched
  aggregate heads escape their arm is admitted like one over strings: the head's promoted
  pattern-owner release names the record's structural dropper (`synthesizeStructuralDropperLabel`
  in `promotePatternOwnerMarkers`, stage 0's `SynthesizeStructuralOwnerDropper`), so the release
  reaches the record's string child, and the escaping-head guard and its borrow walk are gone
  (`tests/tco_runtime_managed_consumed_record_heads_escape.ash`). Three gaps on the caller's side
  surfaced with it and are closed: a recursive binding is now a known callee (recorded as a
  let-lambda with its label, its body placement, and its returned-closure chain, so a call
  through the name reads the loop's `ReturnsRuntimeManaged` bit statically, as stage 0's
  `TryGetCompiledFunctionResultRuntimeManaged` does); a fresh runtime-managed tuple, record, or
  heap-element list matched directly is owned by the arm (stage 0's
  `TrackRuntimeManagedMatchScrutineeOwner`, a plain variable pattern owning it through its own
  slot and handing its reference on when returned, `TryTrackWholeRuntimeManagedMatchBinding` and
  `TransferVariableRuntimeManagedMatchResult`); and the iteration-local owners released at a back
  edge walk their owned children inline (stage 0's `EmitOwnedValueDrop`) instead of dropping the
  cell alone. A matched head returned from an `if` branch inside its arm takes the pattern-owner
  duplicate as the branch's result (stage 0's `TransferDirectRuntimeManagedBranchResult`, a
  marker until the loop's finalize places the owner), the join every reaching branch stores such
  a result into crosses the arm's reset without a copy (`patternOwnerResultTemps`), and the
  literal arms beside a retained owner are normalized so the loop's result is uniformly
  runtime-managed: a string literal arm is copied to the reference-counted heap
  (`shouldNormalizeStaticStringArms` sees through an `if` and counts a retained owner as a fresh
  arm and a tail self-call as a runtime-managed one; stage 0's `IsRuntimeManagedStringMatchArm`
  does the same), and a fresh constructor arm requests the reference-counted cell
  (`retainedPatternOwnerTerminal` is a funnel of the escaping-result reconciliation, stage 0's
  `IsRetainedPatternOwnerTerminal` in `IsProvenFreshCallFunnelArm`). FIXED in stage 0
  (2026-09-05) with the same rule: the string-head search leaked the retained head and, through
  the caller's child-preserving release of a result it took for arena-placed, the list's heads as
  well, 127 MB over three hundred thousand searches
  (`tests/tco_runtime_managed_find_string_head_plateau.ash`,
  `Linux_backend_llvm_find_loop_returning_string_head_memory_should_plateau`). A static
  constructor arm (every argument a literal, or another such application: `Item(name = "none",
  weight = 0)`) is a value that owns nothing, which placement deliberately keeps in the arena
  (`Directly_escaping_adt_with_literal_string_child_remains_arena_managed`), so beside a
  retained head it is built in the arena as usual and deep-copied to the reference-counted heap
  the way a literal string arm is copied (`staticConstructorArm` in `lowerMatchArmBody`, stage
  0's `IsStaticConstructorArm` in `LowerMatchArmExpression` through
  `EmitRuntimeManagedTcoDeepCopy`); the record-head search runs at 5.6 MB through both compilers
  (250 MB and 33 MB before, `tests/tco_runtime_managed_find_record_head_plateau.ash`,
  `Linux_backend_llvm_find_loop_returning_record_head_memory_should_plateau`). A loop parameter
  the reference-counted placement does not admit (a multi-constructor variant, a big integer, a
  tuple or ADT with heap children) stays in the arena and takes stage 0's fixed-watermark
  compaction at the back edge (`emitArenaTcoReset`, stage 0's `EmitTcoBackEdgeArenaBlock` past
  the runtime-managed reset): the per-iteration watermark is restored when every argument
  survives a reset on its own (a scalar, a resource handle, or the loop's own unchanged value,
  `TcoBackEdgeTryEmitPlainReset`), and otherwise, when every heap argument has a whole-value copy
  (a string or big integer by size, a same-arity scalar-field ADT by its cell, a deep-copyable
  ADT or tuple through its clone; a list keeps the arena), the compaction runs under stage 0's
  threshold (`emitCompactionCheck`: the arena grew past twice the live size recorded in the
  compaction-size slot plus 4096 bytes, or the cursor left the watermark's chunk): every heap
  argument is copied up above the cursor, a deep clone twice (`emitCompactionUpCopies`), the
  arena is reset to the fixed loop-entry watermark with an arena string reservation's slots
  zeroed, the copies are copied down onto the watermark (an affine arena string re-reserving
  through an empty in-place append) and stored (`emitCompactionDownCopies`), the chunks above
  are reclaimed, and the live size is recorded, or the watermark rebased to the new chunk's
  allocation start read from the chunk footer when the copy crossed a chunk
  (`emitCompactionRecord`). The deep clone is `StructuralCopiers.ash`'s `synthesizeDeepCopy`
  (stage 0's `EmitDeepCopy`): a string or bytes value copies by its length, a tuple is rebuilt
  element by element, a list over scalar, string or scalar-list heads copies through the
  cons-chain copy, a list over deep-copyable heads clones cell by cell through a synthesized
  `__deepcopy_list_N` copier (stage 0's `EmitListDeepCopierBody`), and a named type calls its
  synthesized `__deepcopy_N` copier (`AdtDeepCopier`, cached per pretty type beside the
  droppers) through a closure whose environment holds the closure itself, the copier switching
  on the constructor tag, allocating a fresh cell of the same constructor, and copying every
  field, a field of the type itself through the self-closure.
  `tests/tco_arena_variant_accumulator_plateau.ash` (a
  `Empty | Filled(Int, Int)` parameter over three million iterations) went from 75 MB to 5.6 MB
  through the self-hosted compiler (stage 0: 4.1 MB), its back edge matching stage 0's text up
  to the reuse-specialization and dead retention-flag blocks stage 0 still emits around it.
Completed work of **OPT-80**, whose open tail is in the [plan](SELF_HOSTING.md). The stage-1 compile
of the semantics package alone outgrows the address space the probe can give it.

  Once MOD-19 closed (2026-09-14) the compile of
  `selfhost/packages/semantics/ashes.json` under the stage-1 CLI died with `failed to allocate
  heap memory from OS` after 15 s at 22.7 GiB resident under a 50 GiB cap (the arena reserves
  roughly twice what it touches, so the cap is the address space, not one request: a gdb
  catchpoint on any `mmap` over 4 GiB never fired, and the cap-bound runs pass once the cap is
  raised). Per-phase resident sizes read at breakpoints on the `--debug` build name the
  self-hosted IR optimizer, not the lowering: a scratch entry importing `Types` costs
  114 MiB before lowering, 594 MiB before optimizing, and 2913 MiB before code generation;
  `TypeResolution` 135, 1143, and 6809 MiB; a private copy of `TypeInference` with its four
  dependencies truncated to 2265 lines 194, 3799, and 22887 MiB. Every program-level optimizer
  stage retains what it allocates (the per-function pipeline 1.4 GiB, the captured-closure
  devirtualization 1.1 GiB, currying-stage inlining 0.9 GiB, and so on for `TypeResolution`),
  and nothing is corrupt. The cause is a stage-0 memory-model gap, reproduced by thirty-line
  programs: a function returning a list whose element carries a heap field (`Add(Int, Int) |
  Name(Str, Int)`, a record with a string field, and every self-hosted IR type) built its
  result in the arena, because a list is a runtime-manageable result only over copy-type
  elements, a constructor head under the runtime-list request never asked for the runtime
  ADT representation, and a string child read out of a pattern owner did not count as owned.
  Such a result has no arena copy-out strategy either, so the call window was never restored
  (a map over 200 lists of 2000 elements grew 16 MiB per extra pass with the same live result)
  and the handed-over argument was never released (one whole list leaked per round of a
  producer loop, 100 bytes per element per pass), where the same program over `(Str, Int)`
  tuples stays flat. The architecture's contract (an escaping ordinary graph is normalized to
  reference counting) was not met for these types. Slice a, done (2026-09-14): the result
  predicate admits lists over runtime-manageable elements, a fresh constructor or record head
  under the runtime-list request is built reference-counted, and a pattern-owner string is an
  owned child of the aggregate storing it (its aggregate duplicate is a real retain whatever
  the root's placement); mirrored in `CoreLowering.ash`, twelve lowered-IR parity fixtures
  regenerated and matched by the self-hosted lowering, plateau tests
  `Linux_backend_llvm_string_adt_list_producer_pipeline_memory_should_plateau` and
  `tests/rc_string_adt_list_producer_pipeline.ash`. The non-tail producer, the let chain, and
  the loop over such lists are now flat; the probe is unchanged (the self-hosted IR types

Completed slices of **MOD-17**, whose open tail is in the [plan](SELF_HOSTING.md):

  - [x] **MOD-17a** Concrete dispatch on a program implementation. The core lowering expands
    `deriving` into implementation items and registers every `implement` of the program (its
    head types with parameters, its requirements, its method bodies) in a trait environment
    seeded from `standardTraitEnvironment`; `==`/`!=` first unify the operands, then take the
    primitive comparison, else resolve `Eq(T)` evidence and, for an implementation with no
    requirement or supertrait dictionaries, lower the selected method once as a capture-free
    closure helper (cached by label like stage 0's TRT-16 instance cache) and call it on the two
    operand temps; an unsupplied `notEqual` negates `equal`. `Eq.equal(a)(b)` calls, which the
    derived bodies use per field, lower as `a == b`. Done (2026-09-13).
  - [x] **MOD-17b** Structural standard implementations with requirement dictionaries. Done
    (2026-09-14): the stitchers load `Ashes.Trait` into every program as a dependency of the
    entry (stage 0's implicit prelude), and a stitched implementation replaces the seeded
    placeholder of the same trait and head shape (`Ashes_Trait_Eq` normalizes to `Eq`). A
    method of an implementation with requirements is lowered once per implementation head as a
    generic closure taking one hidden parameter per method of each required trait (in
    requirement order, methods by name), the body pinned to the head with fresh variables for
    its type parameters; a site applies that closure to the requirement plans' method closures
    (recursively for nested evidence) and calls the result. Inside the body, `==` on the
    parameter type dispatches through the active evidence (stage 0's
    `TryLowerActiveTraitMethod`): a comparison of two still-variable operands covered by active
    evidence skips the speculative integer compare, a recursive let's declared type is read
    against the head's parameters (`equalLists : List(a) -> List(a) -> Bool`), a lambda hands
    its expected result type on to its body so a curried chain's inner parameters pin before
    the body is lowered, and nested lambdas capture the evidence parameters (dead captures are
    pruned). Derived implementations of parameterized types take the same path. Runtime-checked
    against stage 0 on lists, nested lists, `Maybe`, tuples, and `Box(a)` over unit, record,
    and integer elements; `tests/reuse_specialization_declines_unreachable_helper.ash` compiles
    and prints `2` under the stage-1 CLI. Found and fixed OPT-79 on the way (the seeded method
    names were dangling).

Completed slices of **OPT-80**, whose open tail is in the [plan](SELF_HOSTING.md):

  - [x] **OPT-80b** A fresh reference-counted call result handed straight to a callee that
    borrows it (`bump(bumpTimes(p - 1)(insts))`) was never released. The root cause was wider
    than the self call: a plain function returning its parameter on one arm and a fresh list on
    another (`if flag == 0 then insts else bump(insts)`, the shape of every self-hosted
    optimizer pass) joined a borrowed value with a reference-counted one, so its result was
    never reference-counted, no caller released it, and every intermediate of a pass chain
    stayed allocated (the pass-shaped experiment grew to 962 MiB at 16 rounds, the map-shaped
    one to 536 MiB). Done (2026-09-14): a plain function whose terminal arms are all either the
    bare parameter or a freshly built value (a constructor application, literal, cons, a call
    to a function compiled reference-counted, or a self call) copies the parameter into an
    owned graph on the arm returning it (`Lowering.ParameterPassthrough.cs`,
    `NormalizeParameterPassthroughBranch` at the `if` and `match` joins), so the join and the
    function's result are reference-counted; lists over runtime-manageable elements, owned
    tuples, and copyable or runtime-managed named types qualify, strings stay with their affine
    placement. Such a function's result is promised reference-counted before its body is
    lowered (`PredictRuntimeManagedResult`, kept at the return by a copy when the body's own
    result is not), and a curry stage records its returned-closure link as soon as the inner
    lambda is entered, so a recursive call inside the body resolves statically and hands its
    result to the next callee as an owned value. Mirrored in `ParameterPassthrough.ash` and
    `CoreLowering.ash`. Both experiments are flat (86 MiB at every round count); plateau test
    `Linux_backend_llvm_passthrough_or_fresh_result_pipeline_memory_should_plateau`,
    `tests/rc_passthrough_or_fresh_result_pipeline.ash`, parity fixture
    `passthrough_or_fresh_result`.
  - [x] **OPT-80c** The layout classifiers (`Lowering.LayoutCapability.cs`
    `IsRuntimeRecordAdtLayout`, `IsRuntimeOwnedChildAdtLayout`, and the `TList` arms that
    admitted only copy-type elements everywhere) rejected a variant child inside a record, a
    generic payload (`Maybe(Str)`, any type with parameters, any builtin variant), and a list
    over variants, so `IrInstruction { instruction: IrInst, location: Maybe(SourceLocation) }`
    and `List(IrFunction)` never qualified, while the runtime dropper and copier already
    walked those shapes. Done (2026-09-14): one owned-field rule (`IsRuntimeOwnedFieldLayout`)
    admits a scalar, string, bytes, or big integer, a list or tuple of owned fields, a record,
    variant, positional single-constructor type (`IsRuntimePositionalAdtLayout`, a new
    capability kept out of the record and outer-cell reuse paths), or closure, rejecting a
    type that reaches itself (the inline runtime-managed copy of a recursive graph does not
    terminate); the drop-graph walks recurse through list elements; a fresh constructor
    application, a pattern-owner aggregate read, and a list over owned elements are accepted
    as owned children, and a child that is not yet reference-counted is cloned into an owned
    graph before the reference-counted parent stores it (`RequiresRuntimeManagedChildCopy`).
    A user type named `Function` is now released through the reference-counted path, where the
    backend took the `Function` tag for a closure and jumped through its fourth word: release
    and cleanup instructions tag such a type `Function_` (`RuntimeManagedAdtTypeName`,
    mirrored). Nested and string-list element experiments are flat; plateau test
    `Linux_backend_llvm_nested_variant_list_producer_pipeline_memory_should_plateau`,
    `tests/rc_nested_variant_list_producer_pipeline.ash`, parity fixtures
    `producer_conses_nested_variant_head` and `user_type_named_function_release`. The builtin
    variants stay out (OPT-80f).
  - [x] **OPT-80f** `Maybe` and `Result` did not qualify as owned-child variants
    (`IsRuntimeOwnedChildAdtLayout` rejected builtin symbols), and the stage 0 that admitted
    them miscompiled the self-hosted parity runner (the runner checking `heap_result_list`
    died with `failed to allocate heap memory` inside the IR text formatter). Done
    (2026-09-14): the crash was a stage-0 loop-parameter bug the admission exposed rather than
    anything about the two variants. Bisected by admitting one payload family at a time (a
    temporary environment probe in the classifier) to `Maybe<Str>` together with
    `Maybe<IrSourceLocation>`, then watched with hardware watchpoints on the corrupted cell:
    once `OwnerAnchor { anchorTypeName: Str, anchorStructuralDropper: Maybe(Str),
    anchorLocation: Maybe(IrSourceLocation) }` became runtime-manageable, the lifetime
    placement loop `collectInsertions` copied its `anchor` parameter into an owned value at
    entry and released it at loop exit, while `placedDrop(anchor)(slot)(owner)`, whose
    summary borrows `anchor` and whose result aliases it, returned arena instructions that
    borrowed the anchor's type-name string into the loop's accumulator: a borrowed parameter
    whose parts the callee's result keeps was never retained when the argument was a
    runtime-managed loop parameter, since `CalleeResultMayReachParameter` only recognized
    fresh result temps and `PrepareRuntimeManagedCallArgument` returns before any retain for a
    borrowing callee. Now a loop parameter (or a pattern binding of one) handed to a callee
    whose result may reach it is retained for the result, unconditionally once the loop admits
    the parameter and under the parameter's pending flag before that
    (`RetainBorrowedLoopArgumentForCalleeResult`, `IsTcoParameterArgument`; mirrored in
    `CoreLowering.ash` through `argumentRootSlotOf` and a `borrowedReach` hand-off fact); the
    retained reference travels with the arena result exactly as a let-bound owner's does. Both
    builtin variants are admitted in both compilers. The stage-1 binaries built by that
    compiler then crashed in `QualifiedShippedReferences`, and a standalone copy of its scan
    narrowed the crash to a second pre-existing loop-parameter bug the admission exposed: a
    tail self-call whose successor is a record update of the loop's own runtime-managed
    parameter (`scanTokens(known)(rest)((state with scanAfterDot = true))`) built the arena
    successor with raw reads of the unchanged fields, and the back edge released those fields
    once as the dying successor's references and again through the old parameter's structural
    walk (a program with a plain `Str` field crashed on the compiler before this slice too).
    An unchanged heap-typed field of a record update whose target reads a loop parameter is
    now retained like a field read stored into an aggregate, through the same marker the loop's
    finalize pass promotes (`RetainUnchangedRecordUpdateField` over the shared
    `DuplicateTcoParameterReadForAggregate`; mirrored by `retainUnchangedRecordField`, which
    threads the target's `loopParameterReadSlot` through `lowerRecordUpdateFields`). The
    self-hosted projects suite then reported a dependency namespace equal to the dependency
    name: a third latent loop-parameter bug, this one visible on the compiler before the slice
    as well. `validateDependencyModules` hands its `namespace` parameter (a `Str` whose
    admission is still pending while the body is lowered) to `validateDependencyModulePath`,
    whose `Error(ProjectDependencyModuleOutsideNamespace(...))` keeps it; the retain guard was
    the callee's accepts bit rather than the forced flag, since
    `CalleeResultMayReachOrKeepPatternBinding` only recognized fresh temps and pattern
    bindings, so the loop's exit released its own copy under the error. A pending loop
    parameter the callee's result may reach now retains under the forced flag like a pattern
    binding, and the retained reference is handed over with an adoption flag that reads true
    when the callee accepted it or when finalization zeroed the forced flag
    (`EmitPendingRetainAdoptionFlag`), so the caller releases it where the result was copied
    out; the borrowed-parameter retain admits strings too. A coroutine loop
    (`LowerHelperCoroutineTaskEmitLoopBody`) never resolved the flags registered under its
    slots, which left such a forced flag at one for a parameter the coroutine boundary keeps in
    the arena; it now resolves them itself. Mirrored by `pendingRetainAdoption` and the
    `mayReach` hand-off fact covering the parameter itself. The optional-string experiment is
    flat (20 MiB at every round count); plateau test
    `Linux_backend_llvm_optional_string_variant_list_producer_pipeline_memory_should_plateau`,
    `tests/rc_optional_string_variant_list_producer_pipeline.ash`,
    `tests/rc_loop_parameter_parts_kept_by_callee_result.ash`,
    `tests/rc_loop_parameter_record_update_successor.ash`,
    `tests/rc_loop_parameter_kept_by_callee_error_result.ash`, parity fixtures
    `tco_parameter_kept_by_borrowing_callee_result`,
    `record_update_successor_of_loop_parameter`, and
    `tco_string_parameter_kept_by_callee_error_result`.
  - [x] **OPT-80d** The accumulate-and-reverse producer (`bumpInto(tail)(x :: acc)` then the
    generic `reverse`) still leaked a whole list per round (827 MiB at 160 rounds of 50000).
    Done (2026-09-14): the loop's runtime-managed accumulator handed to `reverse`, whose result
    reaches it, was retained for that result and handed over under the callee's adoption bit,
    and `reverse` (a generic list function whose closure never accepts a runtime-managed
    argument) neither adopted the reference nor let the caller release it: the release guard
    (`ResolveHandedOverReleaseGuard`) only recognized the shallow and list copy-outs as
    severing the result from the argument, while this call's `List(Inst)` result has no
    copy-out kind and is normalized by the generic list deep copy instead
    (`LowerCallDeepCopyOutListResult`), which rebuilds every element and its owned parts. The
    deep copy now counts as severing on the arena branch of the result flag
    (`DeepCopiedResultSevers`; mirrored by `deepCopiedResultSevers` in `CoreLowering.ash`), so
    the handed-over accumulator is released where the copy ran: flat at 20 MiB for 20, 40,
    and 160 rounds. Plateau test
    `Linux_backend_llvm_accumulate_and_reverse_producer_pipeline_memory_should_plateau`,
    `tests/rc_accumulate_and_reverse_producer_pipeline.ash`, parity fixture
    `accumulate_and_reverse_producer`.
  - [x] **OPT-80e** Re-measure the semantics package and the CLI package with the phase
    probe after each slice; the BOOT-2 probe resumes when the semantics package compiles
    under the 24 GiB cap. Measured (2026-09-14, after slices a, b, c, d, and f, stage-1 CLI
    built by the merged stage 0, resident size at the start of lowering, optimizing, and
    code generation): `Types` 266, 799, and 3127 MiB (peak 3.2 GiB, 2.5 s); `TypeResolution`
    864, 2028, and 7712 MiB (peak 7.9 GiB, 10.8 s); the semantics package still dies at the
    24 GiB cap after 5 s. The optimizer stages retain exactly what they did before the slices
    (`TypeResolution`: the map of `optimizeIrFunctionWithEvaluable` over the functions
    +1.5 GiB, `devirtualizeCapturedClosureCalls` +1.1 GiB, `inlineCurryingStages` +0.95 GiB,
    `scalarizeSingleCaptureStackClosures` +0.5 GiB, the two `computeKnownReturnedClosureLabels`
    +0.3 and +0.4 GiB), so the slices closed shapes the optimizer does not hit. The shape it
    does hit is reproduced by `opt80/exp/map_record_fn.ash` (a record `Fn { label: Str, insts:
    List(Inst) }` rebuilt by a producer, `optFn(fn) :: mapFns(rest)`, 200 records of 2000
    instructions per round): 69 MiB retained per round on the merged compiler and on the
    compiler before the slices alike, with or without let-bound intermediate lists. The record
    head `Fn(label = label, insts = bump(insts))` is allocated in the arena (`AllocAdt` without
    the runtime-managed bit) and the producer's cons cell too, so the list of records is an
    arena value whose call windows are never restored; see OPT-80h.
  - [x] **OPT-80h** A record head carrying a list of heap-bearing variants (the self-hosted
    `IrFunction { instructions: List(IrInstruction), ... }`) rebuilt by a producer stayed in
    the arena together with the producer's cons cells: `optFn(fn) :: mapFns(rest)` over `Fn {
    label: Str, insts: List(Inst) }` retained 69 MiB per round of 200 records of 2000
    instructions, the exact shape of the optimizer's per-function map. Done (2026-09-14): the
    escaping-result record decision walks the body's terminal arms like the variant case
    (`ProducesFreshRuntimeManageableRecord`, so the literal inside the match arm is seen), and
    a borrowed string field read out of a local binding, whole or by field, a name the arm
    binds later included, is copied into an owned string at the head
    (`IsMaterializableStringChildRead`; a literal keeps the parent in the arena, as the
    directly-escaping-variant tests require); the cons and the consumed record's release
    follow from OPT-80a: flat at 155 MiB for 1 and 16 rounds. Making the lexer's result
    reference-counted exposed a stage-0 gap present on main: a field read out of a
    runtime-managed `let` (`lexed.tokens`) stored into an arena tuple, and that tuple passed
    by name to a callee whose result keeps it, retained nothing, so the owner's scope-exit
    release freed the token strings the syntax tree still borrowed (the parity runner's stack
    was overwritten with IR text). Fixed by retaining a heap-typed field read like the whole
    owner (`DuplicateRuntimeManagedOwnedValueForTransfer`) and by recording the owners an
    arena `let` aggregate borrows (`OwnershipInfo.BorrowedRuntimeOwners`, collected from the
    literal's children) so a transfer of the binding retains them
    (`RetainBorrowedRuntimeOwnersOfAlias`, at a keeping call's plain argument too). Mirrored in
    `CoreLowering.ash` (`producesFreshRuntimeManageableRecord`,
    `isMaterializableStringChildRead`, `materializeStringChildArgument`, the field arm of
    `retainTransferredChild`, `borrowedOwners` and `pendingBorrowedOwners` with
    `borrowedOwnersOfExpression` and `retainAliasBorrowedOwners`, and `emitChildDeepCopy`
    reserving the temp stage 0's inline copy reserves ahead of a nested list child). Plateau
    test `Linux_backend_llvm_record_head_list_producer_pipeline_memory_should_plateau`,
    `tests/rc_record_head_list_producer_pipeline.ash`,
    `tests/rc_let_bound_aggregate_borrowing_owner_kept_by_callee.ash`, compared parity
    fixtures `record_head_list_producer` and `aggregate_borrowing_owner_kept_by_callee`. The
    stage-1 probe is unchanged: the self-hosted `IrFunction` carries fields the record
    classifier rejects (OPT-80i).
  - [x] **OPT-80j** A record reassembled out of a callee result's fields leaked the references it
    retained: `let lexed = tokenize(n) in ... Program(items = items, diagnostics =
    lexed.diagnostics)` leaked 235 KiB per round of a lexer result rebuilt into a program
    record. `lexed.diagnostics` and a `match`-bound `diagnostics` reach the child identically
    and stage 0's `DuplicateRuntimeManagedOwnedValueForTransfer` retains both alike, but only
    the pattern binding counted as a child an aggregate can own, so
    `CanRuntimeManageFreshOwnedChildExpression` rejected the field read, the record stayed
    arena-placed, and the references retained into it were never released. Fixed (2026-09-15):
    a field read of a local binding is admitted as an owned child beside the `Var` case it
    spells (`IsLocalRecordFieldRead` in stage 0, `isLocalRecordFieldRead` in the self-hosted
    `canRuntimeManageFreshOwnedChild`), so the record is reference-counted and dups its
    children on store. The self-hosted mirror needed two more pieces stage 0 already had:
    `retainAggregateChildTemp` retains a heap-typed field taken out of a live owner the way
    `RetainRuntimeManagedAggregateChild` does, and a `let` value now starts from the consumer
    request it inherits (`inheritedLetValueRequest`, stage 0's `PushSequentialLet`) instead of
    an empty one. Measured with `/usr/bin/time -f %M`: 235 KiB per round before, flat at 8.2 MB
    across both 80 and 160 rounds after. Regression
    `tests/rc_callee_result_field_reads_reassembled_into_record.ash`, plateau test
    `Linux_backend_llvm_callee_result_field_reads_reassembled_into_record_memory_should_plateau`,
    and the regenerated `aggregate_borrowing_owner_kept_by_callee` parity fixture, which both
    compilers reproduce instruction for instruction along with the other 53.

Completed work of **OPT-80i**, a measured negative result. The approach is refuted; the live memory
task is OPT-85 in the [plan](SELF_HOSTING.md).

  The record layout classifier rejects every self-hosted IR record, because `IrFunction` carries a
  type that reaches itself, so the optimizer's per-function map builds its results in the arena. The
  attempt was to admit such records to the reference-counted heap by copying and releasing their
  fields through generated per-type copier and dropper functions, so the map could leave the arena.
  Measured on `feature/opt80i-recursive-record-fields` at `2390ec1d`, unmerged and never proposed: a
  stage-1 CLI built by the admitting compiler against one built by the same compiler with the
  admission restricted to nothing, same source, same probe.

  | module probe | admission off | admission on |
  |---|---|---|
  | `Types` | 0.70 s / 3.17 GB | 1.60 s / 4.64 GB |
  | `TypeSchemes` | 0.95 s / 3.86 GB | 2.17 s / 5.86 GB |
  | `Unification` | 2.13 s / 7.65 GB | 6.84 s / 22.3 GB |
  | `TypeResolution` | 2.82 s / 7.91 GB | died at a 30 GB cap in 10 s |

  The off column reproduces the pre-admission numbers exactly, so the delta is the admission and
  nothing else, and it grows with module size. Why it fails: making a self-reaching type
  reference-counted does not stop the arena accumulating. It replaces a shared arena pointer with a
  deep copy of the whole type graph at every boundary that used to share one.

  The attempt exposed seven soundness bugs, and none of them is independently landable: each is
  reachable only through the admission, and every regression it produced passes on a plain build both
  with and without its fix. They are the price of the design rather than latent defects.

## Language and standard-library prerequisites

The capability audit that preceded the port: what the language, compiler and standard library had to
provide before a compiler could be written in Ashes. Every row is complete, which is why it lives
here rather than in the plan. An area tag names the eventual consumer, not the project that had to
implement the prerequisite, and each row links to the normative documentation for the shipped
surface. If a later audit finds one of these incomplete, add the work to the plan and link the row
to it.

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
