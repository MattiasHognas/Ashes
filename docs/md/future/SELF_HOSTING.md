# Self-Hosting: Building the Ashes Compiler in Ashes

Status as of 2026-08-25. This document contains both the capability audit of what Ashes-the-language,
its compiler/runtime, and its standard library must provide before a compiler can be written in Ashes,
and the implementation handoff for the active self-hosted toolchain migration. See
[FUTURE_FEATURES.md](FUTURE_FEATURES.md) for how self-hosting fits the broader roadmap and the
[self-hosted toolchain README](https://github.com/MattiasHognas/Ashes/blob/main/selfhost/README.md)
for package boundaries and commands.

## Migration state

The new implementation lives entirely under `selfhost/`. It is pure Ashes: Python, shell, C#, and
Node.js helpers are not part of its implementation or test path. The existing .NET toolchain remains
in the repository permanently as a buildable, tested stage-0 and behavioral reference after the
self-hosted compiler becomes the default. The Node.js VS Code extension also remains in the repository.
Neither implementation may be removed or changed merely to make the self-hosted port easier.

| Area | Ported surface | State |
|---|---|---|
| Frontend | Tokens, UTF-8 source spans, lexer, typed syntax model, leading import-header separation, inline-module lifting and validation, expressions, patterns, types, and whole-program parsing for all current declaration forms | Implemented and covered by pure-Ashes tests; token streams also have shared stage-0/self-hosted parity fixtures |
| Formatter | Canonical formatting for complete programs, declarations, expressions, patterns, and types, including precedence and idempotence coverage | Implemented and covered by pure-Ashes tests |
| Semantics foundations | Stable symbols/scopes, semantic types, substitution, unordered open-row unification, constrained schemes, and source type resolution | Implemented and covered by pure-Ashes tests |
| Expression/program inference | Core and structural expressions, operators, records, guarded matches, Result pipelines, `let?`, annotations, constructors, recursive groups, aliases, zero-cost types, sequential top-level inference, and package-aware inference of dependency-ordered stitched modules | Implemented for the listed surface |
| Capabilities | Declaration and operation schemes, effect propagation, handlers and `resume`, provider registration, exact concrete provider satisfaction, abstract requirement preservation, and provider/handler ambiguity rejection | Implemented for inference; lowering and code generation remain |
| Traits | Operator constraints; trait declaration/method registration; forward supertrait validation; cycle rejection; qualified method schemes; default-body type checking; ordinary implementation registration with rigid heads, requirements, optional defaults, and type-checked supplied methods; deterministic duplicate/structural-overlap rejection; package orphan ownership for traits and nominal head types; decreasing conditional requirements; selected-default dependency validation; canonical constraints with transitive supertrait elimination; written binding-requirement boundary validation; recursive concrete instance evidence resolution; canonical failure traces; deterministic hidden-dictionary ABI shape planning; ABI-ordered call-site evidence argument planning; constrained-function application/partial-capture planning; active evidence forwarding with deterministic supertrait paths; active trait-method slot planning; concrete dictionary-construction input planning with supplied/default method selection; dependency-aware selected-method construction order; evidence transport destinations for direct functions, closures, aggregates, and async frames; constrained-value rewriting with hidden parameters, dictionary destructuring, and unambiguous method binding; constrained-reference rewriting with exact or inherited active evidence; concrete dictionary-value rewriting with selected method bindings and nested supertrait values; the shipped standard trait ABI plus primitive/structural implementation heads bound to rewritten `Ashes.Trait` source bodies; and deterministic, declaration-aware `deriving` expansion for ordinary and zero-cost nominal types | Declaration, ordinary implementation, coherence, termination, default-cycle, constraint-canonicalization, written `requires` validation, evidence-plan resolution, structured resolution failures, dictionary ABI layouts, call-site evidence arguments, constrained-function application plans, recursive/sibling evidence-forwarding plans, active method-access plans, concrete construction inputs, selected-method build order, value-transport plans, constrained-value/reference rewriting, concrete dictionary-value rewriting, standard implementation evidence/source binding, syntax-level deriving expansion, and semantic deriving eligibility validation implemented; physical IR lowering remains |
| IR, optimizer, ownership, backend, linker | Complete IR model/text form plus core lowering for constants, lexical locals, calls, closures, captures, control flow, recursion, structural values and patterns, operators, BigInt literals, and the shipped non-async/non-FFI builtin operations | In progress; external/evidence/async lowering, optimization, ownership, backend, and linking remain |
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
- [x] Document every production module's responsibility and load-bearing invariants, preserving
  behaviorally relevant stage-0 contracts without copying host-language API boilerplate.
- [~] Add cross-implementation parity fixtures as each self-hosted phase gains a stable serialized
  public result. Versioned token-stream fixtures now compare every public token field between stage 0
  and the pure-Ashes lexer; syntax, formatted source, diagnostics, inferred schemes, IR, and executable
  parity formats remain.
- [x] Make every self-hosted package buildable from a restored source-only dependency graph without
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
- [x] Perform complete match exhaustiveness, redundancy, large-ADT hardening, and source-compatible
  diagnostic reporting. Pure Ashes now checks every typed match once its arms are inferred
  (`matchCoverageError`): constructor patterns from two ADTs, unreachable arms (after a catch-all, a
  repeated literal, constructor, or composite pattern), missing constructors of an ADT scrutinee
  (with the `Result` wording, the five-constructor listing limit, and the "... and N more"
  truncation), a list scrutinee missing `[]` or `x :: xs`, half-covered bools, and the per-field
  missing-pattern search over lists, tuples, constructors, bools, and literals, reported with stage
  0's message text as `NonExhaustiveMatch`, `UnreachableMatchArm`, and
  `ConstructorPatternsFromDifferentAdts` inference errors; or-alternatives, as-patterns, and record
  patterns are expanded to plain positional patterns first, exactly as stage 0's diagnostic
  pre-pass does. Stable `ASH###` codes for these messages remain uncoded in stage 0 as well.
- [ ] Enforce resource move/borrow/consume rules, deterministic cleanup constraints, and use-after-move
  diagnostics at the semantic boundary.
- [~] Seed the shipped standard trait/type identities and primitive/structural implementation heads so
  ordinary evidence resolution no longer depends only on focused test declarations. The remaining
  builtin and standard-library value/type environment must be populated through module stitching.
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
  for concrete dictionaries. Plan dictionary transport destinations for direct function parameters,
  closure captures, nested aggregate locations, and async frames. Rewrite constrained values with
  hidden dictionary parameters, deterministic dictionary destructuring, and unambiguous qualified
  method bindings. Rewrite constrained references with ABI-ordered exact or inherited active evidence.
  Physically thread dictionaries through the corresponding lowered representations.
- [~] Rewrite concrete dictionary construction into dependency-ordered selected method bindings,
  ABI-ordered fields, and recursively constructed inherited evidence. Lower those values, default
  dispatch, method selection, and safe concrete specialization into IR without changing unoptimized
  behavior.
- [~] Register the shipped standard trait ABI and primitive/structural implementation heads, including
  recursive evidence requirements and stable compiler-private implementation references. The stitching
  phase now binds those references to rewritten `Ashes.Trait` source bodies by alpha-normalized
  implementation-head structure; physical dictionary lowering remains.
- [~] Expand `deriving {Eq, Ord, Show, Hash}` into ordinary implementations before coherence checking.
  Ordinary and zero-cost nominal declarations now expand in written order, retain only payload-relevant
  type-parameter requirements, generate deterministic method bodies, and participate in ordinary
  coherence and evidence resolution. Function, pointer, task, unbound-variable, and non-regular
  recursive fields are rejected. A stitched-program declaration context also rejects builtin and
  declared resources, opaque external types, capabilities, and transparent aliases to unsupported
  fields independently of declaration or module order. Physical dictionary and method lowering remains.

#### Modules, projects, externals, and whole-program semantics

- [x] Separate and validate leading import headers while preserving their written forms, aliases,
  selectors, source lines, and imports-stripped UTF-8 body offsets for formatting and diagnostics;
  retain uppercase-final paths for the resolver to disambiguate as modules or type selectors.
- [x] Resolve whole-module, aliased, value-selector, and type-selector imports using typed module
  interfaces, longest-module-path ambiguity rules, export validation, and post-resolution collision
  checks. Map module names to source paths and select project, include, dependency, or shipped-library
  sources with ambiguity and reserved-namespace checks. Construct deterministic reachable-module plans
  in dependency-first order, reject cycles, and enumerate filesystem-backed sources deterministically.
- [x] Validate explicit exports and build value/type/constructor/submodule interfaces from parsed
  programs without exporting externals, trailing bodies, private declarations, or imported modules
  implicitly.
- [x] Enforce sequential visibility, qualification, reserved namespaces, module cycles, and stable
  compiler-private names across stitched modules. Dependency planning, cycle rejection, and
  dependency-ordered semantic scopes assign deterministic definition identities, record ordinary
  versus recursive visibility boundaries, realize resolved selectors and whole-module imports, validate
  full/short qualifiers and collisions, and assign stable public and compiler-private names. Syntax-tree
  rewriting preserves lexical shadows and source spans while replacing declaration, value, constructor,
  type, trait, and capability references with those compiler names.
- [x] Parse and validate typed `ashes.json` manifests, including entry extensions, package versions,
  defaults, source roots, includes, output settings, registry/path dependencies, dev dependencies,
  root-level local `overrides`, and forward-compatible unknown fields. Filesystem path resolution and
  entry existence checks belong to project discovery.
- [x] Discover projects upward, honor explicit project selection, load manifests, resolve project
  paths, validate entry existence, and deterministically plan reachable modules from source-only
  packages.
- [~] Resolve path and registry package graphs, lock files, package identities, one-version-per-package
  coherence, and program-global providers/implementations. Recursive path dependency resolution,
  dev-dependency propagation, cycle and namespace validation, diamond deduplication, and compilation
  planning across dependency source roots are complete. The typed versioned lock-file model, strict
  parser, selected-manifest lock-path mapping, content-addressed cache-path mapping, consumption of
  restored locked packages as validated dependency source roots, and root-only local override
  substitution with exact locked namespace/version checks are also complete; dependency-declared
  overrides are ignored. Registry resolution, cache materialization, and hash verification remain.
  Stitched-project inference now accumulates providers and implementations in one program-global
  environment.
- [~] Stitch the complete project while preserving original file/module spans, definition identities,
  package provenance, and source-function origins. Semantic definition plans now retain source spans,
  source paths, module names, package identities, qualified names, and compiler names; rewritten module
  syntax retains its `At` spans. Rewritten modules are now combined in dependency order, compile-time
  exports and non-entry bodies are removed, the single entry body is retained, and half-open module
  regions plus definition-to-item placements preserve deterministic source/package provenance.
  The combined declarations are inferred in that same order while switching package ownership at every
  module boundary; deriving output stays module-local while eligibility validation shares the stitched
  declaration context, trait orphan checks retain package identity, and implementation coherence is
  program-global. Retaining source-function origins through the future IR remains.
- [x] Lift and resolve inline modules, enforce their restricted declaration surface, and integrate them
  with cross-file imports, exports, aliases, and selector ambiguity rules. Pure-Ashes lifting covers
  header recognition, indentation and dedenting, nested name composition, child-before-parent order,
  same-scope qualifier rewriting, and restricted-body, reserved-name, and duplicate-name validation.
  Reachable compilation planning now publishes synthetic sources with stable provenance, orders nested
  children before parents, rejects reachable file/inline collisions, honors compatibility and explicit
  parent exports, and resolves cross-file whole-module, alias, value-selector, and uppercase type imports.
- [x] Type external functions, opaque/declared resource types, ownership modes, native strings, arrays,
  pointers, buffers, out parameters, symbols, libraries, and capability requirements. External opaque
  types are registered before function typing; source-call shapes omit compiler-owned out parameters,
  append their values to results, retain ABI syntax for validation, and keep direct-only contracts out
  of first-class bindings.
- [x] Validate external ABI combinations and produce the metadata required by lowering, code generation,
  linking, LSP, and package capability auditing. Pure-Ashes validation resolves transparent aliases and
  zero-cost representations, preserves ordered parameter/source shapes and native ownership, verifies
  resource and owned-string destructors, and publishes canonical function, resource, symbol/library,
  direct-call, and sorted runtime-authority metadata with the inferred program.
- [x] Match the current compiler's entry-expression rules, project diagnostics, and deterministic
  diagnostic ordering across files. Declaration-only entries infer Unit, non-entry trailing bodies
  are ignored, and reachable parse diagnostics retain their structured source/span data in stable
  discovery, span, and emission order.

#### IR model and lowering

- [x] Model the complete `IrProgram`, functions, registers, locals, literals, coroutine metadata,
  ownership instructions, and stable function-origin lineage. The pure model covers all 229 current
  instruction variants, source locations, task-frame ABI constants, external/trait metadata, and
  typed source/generated ownership without depending on emitted-label parsing.
- [x] Implement the canonical lowered/final IR text format and deterministic function selection used by
  `--emit-ir` and compiler reports. Pure Ashes now preserves stage-0 function order, source/generated
  selector behavior, trait-evidence annotations, source locations, operand omission and collection-count
  rules, label alignment, and the complete 229-instruction textual vocabulary.
- [x] Lower constants, locals, strict left-to-right evaluation, calls, closures, captures, partial
  applications, and lifted functions. The pure core lowerer threads inference variables and
  substitutions through curried applications, generalizes lexical lets, interns strings, assigns
  independent function temp/local spaces, uses deterministic first-free-use capture layouts, emits
  stack closures for immediate lambdas, and retains nested lifted-function generation order.
- [x] Lower control flow, conditions, matches, guards, recursion, mutual recursion, and tail calls.
  Conditions and scalar matches preserve strict source order, one-time scrutinee/guard evaluation,
  branch-result joins, and arm-local bindings. Recursive functions use environment-relative self
  closures instead of capturing an uninitialized slot; recursive groups predeclare monomorphic member
  types, retain member order, and reconstruct siblings through one shared environment. Tail-position
  recursive applications remain strict ordinary calls at this phase; the later optimization milestone
  owns the specified ordinary and mutual back-edge transformations and their ownership/reset rules.
- [x] Lower tuples, lists, strings, bytes, nominal/record/zero-cost ADTs, constructors, field access,
  patterns, and record updates. The pure core lowerer now emits stage-0-compatible tuple words,
  two-word list cells, interned string references, tagged constructor/record cells, and erased
  zero-cost wrappers. Constructor layouts carry stable tags, schemes, and declared field order;
  first-class and partial constructors use ordinary curried closures. Structural matches cover
  empty/cons lists, tuples, constructors, records, `as`, and `or` patterns, while field access and
  immutable record updates use declared indices and evaluate replacement fields once. `Bytes` has
  no source literal form; byte-producing and byte-consuming intrinsics remain with the builtin
  operation item immediately below.
- [x] Lower operators, BigInt, text/number conversions, program arguments, panic, standard I/O,
  filesystem, environment, process, networking, TLS/HTTP, regex, and other builtin operations.
- [x] Lower external calls, resources/destructors, native ownership conventions, library/resource
  references, and target ABI metadata.
- [x] Lower capability handlers/providers and trait evidence according to the completed semantic plans.
- [x] Retain source maps, definition/hover identities, diagnostic locations, function origins, and
  explanation metadata through generated helper functions. Pure Ashes source contexts resolve single-file
  and multi-file combined offsets to UTF-8 line/column coordinates and filter out internal runtime machinery.
  Function origins maintain structured provenance across entry, source functions, lambdas, specializations,
  wrappers, coroutines, normalizers, droppers, and copiers. Hover and public authority collectors index
  inferred types and capability requirements, and compilation decision snapshots capture function ownership,
  value placements, and external authority records.
- [x] Validate lowered IR invariants and compare normalized IR fixtures with the C# compiler. Pure Ashes
  IR validation enforces program-level invariants (entry label and non-closure contract, unique function
  and string literal labels, non-negative capability globals), function-level invariants (non-negative
  local and temp index bounds, intra-function label uniqueness, branch target resolution to defined
  labels, referenced string literals, non-negative coroutine metadata, and local debug metadata bounds).
  Shared normalized lowered IR fixtures in `selfhost/parity/semantics/lowered-ir/` compare stage-0 lowering
  and formatting with pure-Ashes output across arithmetic, let bindings, closures/captures, pattern matching,
  and mutual recursion.

#### Optimization, ownership, and reuse

- [x] Port compile-time evaluation and the current deterministic IR optimization pipeline, including
  constant simplification, dead-code cleanup, inlining/specialization, and metadata preservation.
  Pure Ashes compile-time evaluation evaluates pure constant-argument calls with bounded step (50,000)
  and depth (1,000) budgets, with full scalar call folding into constants. The deterministic optimization
  pipeline implements trivial ownership-copy elision (erased RcDup and single-use/copy-type Borrow remap),
  runtime RcDup sinking into branch diamonds, adjacent runtime RcDup/RcDrop fusion, known closure
  devirtualization (CallClosure -> CallKnown), constant propagation and folding with single-predecessor
  label flow, identity elimination and strength reduction, unreachable code elimination, dead code
  elimination, erased RcDrop marker cleanup, and interprocedural redundant arena bracket stripping.
- [x] Extend constant propagation to compute a true meet-over-paths at multi-predecessor labels (a fact
  survives only if every incoming edge agrees on it), not just single-predecessor label flow, and to
  local-slot state (StoreLocal/LoadLocal), not just raw temps. Pure Ashes now records one fact
  snapshot per predecessor edge (each `Jump`/`JumpIfFalse` target, each `SwitchTag` case and default)
  keyed by target label, intersects them with the fall-through state at the label once every counted
  edge has been observed, clears every fact at a label with an unobserved backward edge, tracks
  `StoreLocal`/`LoadLocal` slot facts that a store of an unknown value kills, and folds a load of a
  known slot into a literal; covered by `selfhost/tests/semantics/IrOptimizerTests.ash`, which this
  milestone also wires into the semantics test entry along with `IrValidationTests.ash`. The C# optimizer computes the meet by
  accumulating a state snapshot per predecessor edge (explicit branches, plus one edge per `SwitchTag`
  case/default, plus fall-through) and, once every edge into a label has been observed, intersecting
  them; a label with an edge not yet observed at that point in a forward scan (e.g. a loop back-edge)
  still conservatively clears all knowledge, matching the historical behavior for loop headers. Local-slot
  tracking is not a side detail: every `let`-bound value and if/match join result in Ashes IR is lowered
  through a mutable local slot (a StoreLocal in each producing arm, a LoadLocal at the point of use),
  never through direct temp reuse across a label — so temp-only meet-over-paths, alone, folds nothing in
  real compiled programs (verified: it only ever fires on synthetic hand-built IR with raw temps reused
  directly across a branch, a shape that doesn't occur in real lowered output). A slot holds at most one
  of Int/Float/Bool at a time; a store of an unknown or non-scalar value kills stale knowledge for that
  slot, since a slot is ordinary mutable storage, not single-assignment like a temp.
- [x] Fold a conditional branch whose condition is statically known (via the constant propagation above):
  rewrite `JumpIfFalse` to an unconditional jump when the condition is known false, or drop it entirely
  when known true, leaving execution to fall through to the surviving arm. Recompute predecessor edges
  fresh from the post-fold instruction list (not reused from before folding) when deciding whether a label
  still re-establishes reachability after a terminator, so a branch whose only remaining edge was just
  folded away is recognized as genuinely dead — including the label instruction itself and its body —
  rather than only losing its guarding jump while its now-unreachable body silently survives. Pure
  Ashes now folds a `JumpIfFalse` on a known condition (dropped when true, so no edge snapshot is
  recorded for the never-taken target; an unconditional `Jump` carrying its snapshot when false), folds
  a `SwitchTag` on a known tag to a `Jump` to the first case carrying that tag or the default, and
  rebuilds branch-reference counts inside unreachable-code elimination so a label with no remaining
  reference inside an unreachable region is dropped together with its body; covered by
  `selfhost/tests/semantics/IrOptimizerTests.ash`.
- [x] Re-run ownership-copy elision after identity elimination/strength reduction within the same
  optimization invocation: identity reduction (`x+0`, `0+x`, `x-0` -> `x`) rewrites the identity into a
  copy instruction rather than retargeting downstream uses directly, and — because a single-pass pipeline
  runs each stage once — that new copy is never revisited by the copy-elision stage that already ran
  earlier and would otherwise erase it (an erasable copy is one whose source is a constant producer, or
  whose target has exactly one remaining use). Ownership-copy elision must be a pure function of its
  input (recomputing its use-def facts fresh each call) for a second call to be safe and effective.
  Pure Ashes now runs `elideTrivialOwnershipCopies` a second time directly on the output of
  `reduceIdentitiesAndStrength` (its copy-type producers and use counts are recomputed on every call,
  so the second run needs no shared state), erasing the `Borrow` copies the identity rewrites
  introduce; covered by `selfhost/tests/semantics/IrOptimizerTests.ash`.
- [x] Extend closure devirtualization (`CallClosure -> CallKnown`) past a single `MakeClosure`
  definition: a curried call's second and later applications (`add(10)(32)`) never devirtualize
  today because the closure temp being called is defined by a `CallKnown` (the first application),
  not a `MakeClosure`, so the existing single-definition-count test never fires past the first
  argument. Compute, per function, via a whole-program least fixpoint (the same "repeat one pass
  until nothing changes" control structure the non-allocating-function summary already uses,
  generalized from a shrinking candidate set to a growing known-label map), whether every `Return`
  in a function's body is provably the same closure label — directly from a *heap* `MakeClosure`, or
  transitively through a `CallKnown` to another function already proven, earlier in the fixpoint, to
  return that same label. **Deliberately excludes a stack-allocated closure as a "known returned
  label" source, reasoned through before writing any code, not found by a wrong answer**: a stack
  closure's environment lives in its defining function's own native stack frame, gone the instant
  that function returns, so treating one as a function's known return value would let a later caller
  extract and dereference a dangling pointer once devirtualized. At a `CallClosure` site whose
  closure temp reaches such a `CallKnown`, rewrite to an explicit environment-field extraction
  (a plain read of the closure object's fixed env offset, matching the ordinary closure-call code
  generator's own field layout) immediately followed by a direct `CallKnown` — a plain field read
  neither consumes nor extends the closure object's lifetime, so it does not disturb whatever
  ownership/RC placement already exists for that temp. Iterate the rewrite per function to its own
  local fixed point so a curry deeper than two arguments fully resolves in one optimization pass, not
  just its first newly-direct hop. A two-to-four-label lambda-set-specialization dispatch (emitting a
  small direct-call-per-arm dispatch when a closure temp's reaching definitions disagree across a
  small closed set, rather than declining outright) was scoped as a stretch extension to this same
  capability and was not implemented in the C# compiler either — treat it as a separate, larger unit
  of work, not something this checklist item's own `[x]` should imply. Pure Ashes now computes the
  known-returned-label map as a whole-program least fixpoint over every function's `Return` sources
  (a single-defined heap `MakeClosure`, or a `CallKnown` to a function already in the map; a
  `MakeClosureStack` never qualifies), then rewrites each `CallClosure` whose closure temp is such a
  `CallKnown` result into a `LoadMemOffset` of the closure object's environment word at offset 8
  plus a direct `CallKnown`, iterating every function to its own local fixed point after the
  per-function pipeline and before arena-bracket elimination; covered by
  `selfhost/tests/semantics/IrOptimizerTests.ash` (direct, transitive, and stack-closure-declined).
- [x] Add a local common-subexpression elimination pass, scoped to a single straight-line block (reset
  at every label, never across control flow): forward a duplicate `GetAdtField` read or a duplicate
  `CallKnown` call to a function proven pure by the compile-time-evaluation purity oracle (reused, not
  reimplemented) to the first occurrence's result. Operands must be canonicalized through a
  LoadLocal/StoreLocal/Borrow/RcDup alias map before keying the cache — the same lesson meet-over-paths
  above already learned: real Ashes IR round-trips almost every value through a local slot, so
  raw-temp-identity-only keying folds nothing in real compiled programs (the ubiquitous `let x = p.x in
  let y = p.x` shape never matches without it). A function's own env/arg slots (0/1) additionally need
  a seeded identity: the backend's entry prologue populates them with a native store the IR-level
  optimizer never sees as an explicit `StoreLocal`, so without seeding, every read of a function's own
  argument looks like an unknown value. The cache must be invalidated on any instruction that could
  write through an aliased pointer (`SetAdtField`, any allocation/reuse variant, a non-pure call) but
  explicitly NOT on arena/stack bookkeeping (`SaveArenaState`/`RestoreArenaState`/`ReclaimArenaChunks`/
  `SaveStackPointer`/`RestoreStackPointer`) — those move an allocator cursor, never write through an
  existing pointer, and every `let` binding gets its own such bracket in practice, so treating them as
  aliasing would silence this pass almost everywhere. Separately, note that `CallKnown`-based merging is
  currently reachable only when the closure was already devirtualized from `CallClosure`, which itself
  requires the closure temp to trace directly to a `MakeClosure`/`MakeClosureStack` with no intervening
  local-slot round-trip — a condition essentially no `let`-bound function call satisfies today, a
  separate, pre-existing devirtualization gap this task did not attempt to fix. Pure Ashes now
  ports the block-local pass with the alias map, the seeded env/arg slot identities, the
  deny-by-default invalidation list with the arena/stack bookkeeping exemption, and the
  `computeEvaluableFunctions` oracle reused for known-call merging; the fresh-allocation
  store-to-load forwarding is the next item.
- [x] Extend the local common-subexpression pass above with store-to-load/projection forwarding:
  when a `SetAdtField` writes through a pointer proven fresh in the same block (an `AllocAdt`/
  `AllocAdtStack` target — nothing that existed before it could hold or derive a reference to memory
  that didn't exist yet), record the field cache entry directly from the write instead of only from a
  subsequent read, so an immediately-following `GetAdtField` of the same (pointer, field) forwards the
  stored value without round-tripping through memory (the `Point(p.y, p.x)`-style construct-then-
  destructure shape, matching Ashes' allocation-tier recognition for the same pattern). A write
  through a not-known-fresh pointer keeps the existing fully-conservative invalidate-everything
  behavior, since it could alias any entry already cached. **Sharp edge, found only by compiling and
  running real `.ash` source, not by hand-built raw-IR unit tests**: the cached value must be the
  write's raw, unresolved source temp, never its alias-canonicalized identity — canonicalization can
  resolve down to a synthetic, negative sentinel (the seeded identity for a function's own env/arg
  slot with no real defining instruction visible to this pass), and a sentinel is only ever safe as a
  cache *key* for matching two operands as the same value, never as a forwarded, *emitted* value —
  emitting one produces an out-of-range temp reference that crashes at codegen. This pattern (a fresh
  record's field set from a value that itself traces back to the enclosing function's own argument)
  is completely ordinary real code and was not exercised by any of this pass's own unit tests, only by
  compiling actual source and running the result. Pure Ashes now tracks `AllocAdt`/`AllocAdtStack`
  targets as fresh per block, populates the field cache from a `SetAdtField` through one with the
  write's raw source temp, and its test covers the sentinel edge directly (a fresh record's field
  set from the function's own argument, read back and added).
- [x] Add closure environment scalarization for a single scalar capture: when a stack-allocated
  closure's environment holds exactly one 8-byte value and its only use is already a devirtualized
  `CallKnown`, skip the environment allocation entirely and pass the captured value directly as the
  call's existing "env" argument, generating a scalar-reading callee variant (memoized per target
  label, original left untouched) rather than rewriting the callee in place. **Sharp edge, found only
  by compiling and running real `.ash` source, not by this pass's own hand-built raw-IR unit tests**:
  a real (non-coroutine) lowered closure reads a capture via the dedicated `LoadEnv(Target, Index)`
  instruction, which dereferences local slot 0 implicitly inside its own codegen — never via an
  explicit `LoadLocal(_, 0)` + `LoadMemOffset` pair, the shape a hand-built raw-IR test naturally
  produces and this pass was originally built around, and which essentially never occurs in real
  lowered output. Scope stays to exactly one capture: every Ashes-callable function shares one fixed
  3-word LLVM call signature so `CallClosure`'s indirect dispatch stays uniform regardless of capture
  count; an N-ary direct-call-only variant would need a new calling convention and a new IR
  call-instruction shape, out of scope here. Also excludes a coroutine callee (its state-machine
  transform rewrites `LoadEnv` into a `LoadMemOffset` against its own frame/state-struct temp instead
  — materially different and riskier) and a callee that reads the env slot as a raw value anywhere
  outside of `LoadEnv`. Removing the environment allocation lets the existing arena-bracket-stripping
  pass also strip the now-redundant bracket around the call as a free consequence, not something this
  pass touches directly — measured **1.51x faster at `-O0` and 2.65x faster at `-O2`** on a
  20,000,000-iteration hot loop building and calling a single-capture closure per iteration; unlike
  most passes in this pipeline, the `-O2` win is real (not subsumed by LLVM) because it comes from
  removing genuine arena-cursor runtime bookkeeping, not the allocation itself. Pure Ashes now
  ports the one-capture form: the caller-side gate (single-definition 8-byte `AllocStack`, one
  store at offset 0, two uses), the `LoadEnv`-only callee gate with the coroutine and raw-slot-0
  exclusions, the memoized `__scalarenvN` variant that reads slot 0 directly, and the original
  callee left untouched; the two-capture extension is the next item.
- [x] Extend closure environment scalarization to two scalar captures, and reach let-bound local
  helpers with it. A second capture travels in the call's ownership-flag word, which is free
  whenever the `CallKnown` passes no flag and the callee's body never reads one: the variant reads
  it through `LoadArgumentOwnership` (a raw read of that same parameter), the caller-side gate
  accepts a 16-byte `AllocStack` filled by exactly one store per 8-byte capture and used nowhere
  else, and three or more captures still keep their environment (the shared 3-word signature has
  no further free word). `LoadArgumentOwnership` counts as non-allocating so the scalarized call's
  arena bracket is stripped. Closure devirtualization resolves a call's closure temp through a local
  slot written by exactly one `StoreLocal` (lowering only reads a binding's slot inside the
  binding's own scope, after the store), removes the load it made dead, and removes the scope-exit
  `CleanupResource(Function)` of a stack closure that never received a dropper (no store to the
  closure object's dropper word at offset 24 — a runtime no-op), so the slot, the closure
  construction, and the environment die and a `let step = given x -> ...` helper scalarizes like an
  immediately-applied lambda. Measured **2.76x at `-O2` / 1.57x at `-O0`** on a 20,000,000-iteration
  loop building and calling a two-capture let-bound helper per iteration; the already-scalarized
  single-capture immediately-applied shape is unchanged. Pure Ashes now ports both halves: the
  16-byte site gate with the free-flag-word condition, the `LoadArgumentOwnership` variant read
  and its callee exclusion, `LoadArgumentOwnership` as non-allocating, and slot-resolved
  devirtualization with dead-load and dropper-free cleanup removal.
- [x] Prune a closure capture the lowered body never reads via `LoadEnv` (a lowering-stage change,
  not part of the `IrOptimizer` pipeline above, since a capture's environment is built at the
  creation site *before* the body is lowered and its used-set is known): record each capture's fill
  instruction range at construction time, then once the body's own instructions are available, delete
  the fills for indices with no corresponding `LoadEnv` read, renumber the survivors to a compact
  `0..k-1` range in both the fill offsets and the body's `LoadEnv` indices, and shrink the environment
  allocation's size to match — so every downstream consumer that recomputes its own offsets from the
  capture list's enumeration order (resource-capture tracking, the runtime-managed-closure dropper and
  normalizer) needs no separate patching. A capture that required retaining an owned outer value has
  that retain's accounting explicitly undone when its fill is deleted, not just the instruction
  removed. Declines a self-referential lambda (it reconstructs a closure over this same environment
  from inside its own body using a size recorded before pruning could run) and a mutual-recursion
  group (the environment is shared and filled once at the group site, not per member); a coroutine
  body is unaffected by construction, since it never goes through this capture/environment path at
  all. Composes with, but is a separate capability from, the single-scalar-capture environment
  scalarization above — a two-capture closure with one dead capture becomes eligible for that
  optimization only once this pass has pruned it down to one. **Measured**: pruning a mutual-recursion
  dispatch closure's env from two captures (16 bytes) to one (8 bytes) also unlocked the
  runtime-managed-closure normalizer above as a purely emergent side effect (that pass had been
  blocked only because the *other*, now-pruned capture wasn't itself runtime-normalizable). A
  200,000,000-iteration driver repeatedly entering the same mutual-recursion group ran **14% faster at
  the CLI's default `-O2`** — unlike most passes in this pipeline, this `-O2` win is real because it
  removes an allocation LLVM has no way to reconstruct once Ashes has already chosen to omit it.
  Pure Ashes now lowers a plain lambda's body before building its environment, so the pruning is a
  filter over the capture list plus a `LoadEnv` renumbering of the finished body rather than a
  deletion of already-emitted fills (`pruneDeadCaptures`, with the self-referential and group paths
  untouched); its free-variable analysis is shadowing-exact, so no current lowering path produces a
  dead capture and the mechanism is covered by direct tests over hand-built bodies.
- [x] Fold a left-nested chain of string-concatenation calls with single-use intermediates into one
  N-ary concatenation that allocates once for the sum of every part's length and copies each part
  directly into its final position, instead of paying one allocation and one growing copy per link
  (`n-1` allocations, `O(n^2)` bytes copied, for `n` parts). Run this as the very last step of the
  optimization pipeline, after every other pass, so no earlier pass needs to know about the new
  instruction shape — only code generation does. **A single-use/def-count safety check is necessary
  but not sufficient, found only by running the compiled output of a realistic chain, not by
  hand-built unit tests**: folding delays reading an *earlier* part's string until the new
  instruction's position, at the end of the chain: if a *later* part's own computation reclaims a
  bump-allocator cursor back past where the earlier part was allocated (e.g. each part is an inlined
  helper call, each with its own arena save/restore/reclaim bracket), the later part's own
  allocation can land at the same address the earlier part still needs to read from — invisible to a
  pure single-use analysis, since each temp genuinely is used exactly once; the hazard is *when* it's
  read relative to a reclaim, not how many times. Fix: before committing to a fold, scan every
  instruction from the innermost part's own definition through the fold point for any arena
  save/restore/reclaim bracket, stack-pointer save/restore, or branch/label instruction, and decline
  the whole chain if any appear — conservative on purpose (it does not attempt to prove a *specific*
  reclaim's range excludes a *specific* part's address). This materially narrows how often the fold
  fires: any part computed via a real function call typically carries its own arena bracket, so in
  practice this applies mainly to chains built from literals and other allocation-free intermediate
  values, not general "each part is an arbitrary expression" chains — a correctness-motivated
  narrowing, not a missed opportunity to relax later without more analysis work. **Measured**: a
  5-part literal chain (`"user " + "has " + "42 " + "items " + "today"`) inside a
  20,000,000-iteration loop ran **~2.25x faster at `-O0`** and **~15-17x faster at the CLI's default
  `-O2`** — unlike most passes in this pipeline, the `-O2` win dominates, since LLVM cannot invent
  away a real allocator call with observable side effects that the unfolded chain pays every
  iteration. Pure Ashes now carries `ConcatStrN` through its IR model, text dump, validation, and
  temp scans, and folds as the last program-level step with the same single-use chain walk, the
  runtime-managed flag agreement, and the bracket/branch decline over the innermost-part-to-root span.
- [ ] Widen the affine-accumulator in-place-append (`ConcatStrTip`) arming to the `let`-bound
  accumulator form `let acc2 = acc + rhs in loop(...)(acc2)`, as the C# compiler now does. Four
  coordinated pieces, three of them in the not-yet-ported move-analysis/ownership side (see the
  reuse/move-analysis section below): the affine-self-append analysis follows a *single-use* `let`
  alias of a candidate parameter (a fail-closed occurrence counter — any unrecognized expression
  shape counts as a second use — gates eligibility, because the whole transform is sound only for a
  binding consumed exactly once); the in-place append is armed while lowering that `let`'s value
  rather than at the tail-call argument; and loads of the armed binding carry the append result's
  producer fact so the tail-call back edge recognizes the argument as the in-place-grown accumulator
  (whose append already consumed the old parameter's reference) and skips the predecessor release —
  without that skip the accumulator is freed while live, a crash once it outgrows its first chunk.
  Porting note: the C# reset resolution replays each function's instructions with all per-temp facts
  cleared and re-derived from the instructions alone, so this fact must be recoverable from durable
  per-function state (keyed by function and local slot), not only stamped once at initial lowering.
- [x] Add a control-flow simplification pass: jump threading (redirect a branch through a chain of
  empty labels — a label immediately followed by nothing but an unconditional jump — straight to the
  chain's final destination), unreferenced-label removal, and elision of a Jump immediately followed
  by its own target label (a redundant fallthrough). Every rewrite is locally safe without reachability
  analysis: redirecting a branch aims it at the same eventual destination; dropping a label with zero
  remaining references removes only a marker, never the code around it; and a redundant fallthrough
  Jump is a pure no-op (nothing can jump directly to a Jump instruction itself, only to a label, so it's
  reached solely by fallthrough, which reaches the label just as well without it). **Requires iterating
  jump-chain redirection together with unreachable-code elimination to a true fixed point, not a single
  pass**: redirecting several distinct branches to the same final label, once the now-unreferenced
  labels that used to separate them are dropped, stacks multiple unconditional Jumps directly
  back-to-back — every one after the first is newly unreachable code, and removing it can in turn bring
  a surviving Jump directly adjacent to its own target label, exposing a further redundant-fallthrough
  opportunity. A single application of "simplify, then sweep unreachable code" is not enough to fully
  collapse a real multi-arm `match` cascade (verified: a real compiled 4-constructor match left one
  redundant Jump/Label pair per arm after one pass, cleared only once the pair is iterated to a fixed
  point — the instruction count strictly decreasing each iteration bounds the loop). Primarily valuable
  at `-O0`/`--debug` (LLVM's own `simplifycfg` already performs this at `-O1`+) and for
  `--emit-ir`/`--explain` output quality; measured no meaningful hot-loop speed change at either `-O0`
  or `-O2` on a representative match-heavy benchmark (within measurement noise at both), consistent with
  removing well-predicted branches rather than real work. Pure Ashes now ports the three rewrites
  (`simplifyControlFlow`) and iterates them with unreachable-code elimination until the instruction
  count stops decreasing (`simplifyControlFlowToFixedPoint`).
- [x] Extend match compilation to group arms by their outer constructor tag (not just emit one tag
  switch for an already-fully-trivial flat match): more than one arm may share a tag, sharing one tag
  test across all of them, with a group of more than one case (or a single non-trivial nested
  sub-pattern) falling back to linear per-case testing scoped to that group only — never reordering or
  duplicating an arm across leaves, so this stays a pure grouping decision layered on top of already-
  correct per-arm testing/binding/reuse-token logic rather than a new pattern-matching engine. Also
  eliminate a redundant top-level-constructor-tag-only "exhaustive" check for dead-arm elimination in
  favor of a fully recursive, per-field-position coverage query (`Ok(true) | Ok(false) | Error(_)` must
  be provably exhaustive without needing a trailing wildcard, unlike naive tag-set coverage, which
  wrongly treats it as exhaustive after just two arms and can silently drop a live arm). **Sharp edge,
  confirmed twice**: the scrutinee's type can still be an unresolved type variable at the point this
  decision must be made — not just for a recursive function's own parameter, but for any function's own
  parameter whose type is pinned down only by unifying it against this same match's own patterns, which
  has not happened yet this early. The fix is not to decline until some later point (arm emission itself
  needs the trimmed case list first) but to perform that unification explicitly, one pattern at a time,
  before deciding — unification is idempotent, so doing it slightly earlier than it would otherwise
  happen adds no new constraint a correct program would not already require. Measured on two
  representative shapes against a temporary pre-task baseline: a repeated-tag match with two genuinely
  divergent nested cases (the outer tag shared, but each case still needing its own test) ran **~1.06x
  faster at `-O0`, ~1.04x faster at `-O2`**; five distinct-tag arms where only one has a non-trivial
  nested sub-pattern (previously disqualifying the *entire* match from tag-switch dispatch, not just
  that one arm) ran **~1.46x faster at `-O0`, ~1.03x faster at `-O2`** — the more representative,
  larger win, since disqualifying an entire match over one nested arm was the dominant real-world cost.
  Not yet closed: a multi-case group's own cases still each re-test the already-proven-by-the-outer-
  switch tag via general pattern testing, rather than a tag-already-known field-extraction variant;
  column reordering and cross-arm frequency heuristics remain unexplored. **Third confirmed sharp
  edge**: a group's last case's failure target must be the group's own trailing default arm's label
  whenever one exists in the match, not the match's overall exhaustiveness-failure label — a case
  whose outer tag matches but whose nested sub-pattern then fails (`Some(('>', _))` failing its
  character literal test) is exactly the situation a trailing `_` arm exists to cover, and routing it
  to the failure path instead produces a null result (segfault) or, when the failure path itself
  falls through by construction, silently skips the case entirely. Confirmed by a real regression:
  `reverse-complement` printed only its first FASTA header on every input because this exact shape
  (`Some(('>', _)) -> ... | _ -> ...`) misrouted on every line after the first. Pure Ashes now ports
  the tag-group dispatch (`planTagGroups`/`lowerMatchArmsViaTagGroups`): first-seen groups, one
  trailing catch-all as the switch default and every group's fail target, trivial single-case groups
  binding their payload with no tag re-test, linear testing within any other group, stage 0's
  four-arm linear threshold for an all-trivial match, and a decline for guards, zero-cost
  constructors, or a second ADT. Dead-arm elimination and its recursive coverage query are carried
  by the next item, which the pure-Ashes lowering does not have yet. A bare `None`/`NoVal` arm is a
  variable pattern in the syntax tree (the parser cannot tell a binder from a nullary constructor);
  pure-Ashes inference, pattern-binding preparation, lowering, and the group planner now resolve such
  a name against the constructor table exactly as stage 0 does, so it is a tag test rather than a
  catch-all binder that silently absorbed every later arm.
- [ ] Gate the dead-arm trim above to pattern shapes the coverage engine analyzes exactly. The
  "Missing case" engine is a deliberate under-approximation of what is missing — correct for a
  diagnostic, which must never report a false "Missing case", and unsound as a proof that a trailing
  arm is unreachable: constructor argument positions are checked independently of one another
  (`(true, false) | (false, true)` covers both columns while `(false, false)` is unmatched), and a
  record sub-pattern contributes no field constraints at all, so `None | Some(Def { body = None })` is
  judged exhaustive and the live `Some(Def { body = Some(b) })` arm is deleted before lowering.
  **Found only by compiling and running the self-hosted semantics package itself**: that arm was
  `findInferenceTraitMethod`'s default-body case, so every default-method dependency check fell off
  the end of its match (a segfault at first; a silently wrong `error = None` once the default-arm
  routing fix above changed what a fallen-off match yields), and the same trim fired at four more
  sites. The prefix stops growing the moment a pattern enters it that is not a catch-all, a bool
  literal, the empty list, or a constructor/cons/tuple whose every child is a catch-all — for those,
  coverage reduces to the constructor set the engine enumerates completely; record patterns,
  int/string/rune literals, and any nested non-catch-all structure decline. The dead `_` after a
  complete set of catch-all-argument constructor arms is still trimmed.
- [x] Port ordinary and mutual tail-call optimization, stack-safety rules, and profitability/cost
  signals without changing strict evaluation order. Pure Ashes TCO analysis identifies tail positions
  across expressions and match arms, detects direct self-recursive tail calls for loop conversion, and
  decomposes mutual recursion groups into SCCs with tag-based dispatch trampoline plans. Profitability
  and cost signals analyze parameter RC-eligibility, allocation and borrow blockers, and threshold-based
  profitability verdicts without violating strict evaluation semantics. Fully validated with pure-Ashes
  test suite in `selfhost/tests/semantics/TcoTests.ash`.
- [ ] Upgrade LLVM's advisory `tail` marker to the hard-guarantee `musttail` marker for a non-loop tail
  call already proven `CanEmitNativeTailCall`-eligible (exact `CallKnown`-immediately-followed-by-
  matching-`Return` adjacency), gated by a whole-function scan for any native stack allocation
  (closure environments and capability/effect-handler frames both use the same stack-allocation
  mechanism, and a handler frame's pointer can outlive a tail call later in the same function via a
  dynamically-scoped global). Blocked on LLVM code generation existing in `selfhost/` first (see below).
- [ ] Widen mutual-recursion loop merging past same-arity/identical-parameter-type groups with the
  dispatch slot layout: one dispatch slot per parameter position where every member's (structurally
  compared) parameter type agrees, and one slot per distinct type where they differ or where only some
  members have that position at all, so members of differing arity merge too. A tail call or wrapper
  entry fills the callee's slots with its arguments and every other slot with that slot type's default
  literal (`0`, `false`, `0.0`, `""`, the zero rune, the zero fixed-width unsigned, `[]`); a non-shared
  slot whose type has no constructible default (a user-declared type, tuple, function, or unresolved
  type variable) declines the group, and so do members whose result types differ (every member body
  becomes one arm of the dispatch match). Groups that merged under the old gate must lower to the
  identical shared-slot layout.
- [ ] Resolve a member body's **non-tail** sibling references inside the merged dispatch: only tail
  in-group calls are rewritten into dispatch calls, so a call whose result the member inspects
  (`match findCycle(name)(decls)(path) with ...` before its own tail call), a partial application,
  or a reference from a nested lambda stays a plain reference to the sibling. The closure-lowered
  member bodies resolve those by symbol through the group context, but the dispatch lambda is a
  fresh synthesized function; lowering it with only the dispatch's own name in scope makes the
  reference fall through to the forward-reference diagnostic (`ASH014 Binding 'findCycle' is not
  yet declared at this point.`) on a well-formed program. Bind every member name to its
  already-emitted closure slot (monomorphic at this point — schemes are generalized only after the
  transform) while lowering the dispatch body, exactly as the continuation scope later binds the
  names to the wrapper slots. The gap predates the shape widening above, but that widening made the
  self-hosted `findSupertraitCycleInRequirements`/`findSupertraitCycle` group eligible for merging
  and broke the `selfhost/tests/semantics` build.
- [ ] Infer parameter/capture ownership, result reachability and freshness, moves, borrows, forwarding,
  and whole-program SCC provenance summaries.
- [ ] Prove open-world inspect-only parameters so in-place reuse borrowing survives a hand-off to
  another function: the same `BorrowInspectExpression`/`BorrowInspectOnly` walk that lets a TCO loop
  borrow its own tail parameter across match/head/tail uses and its own tail self-call is computed
  for every parameter of every registered function as a monotone least fixpoint (a hand-off to a
  callee already proven inspect-only in the previous pass is approved, so a chain of self-contained
  helpers converges while a genuine mutual cycle never does), and `BorrowInspectCall` consults that
  table for a call to a statically-resolved callee other than the function itself (a partial
  self-application is a separate question). Note that `FunctionOwnershipSummary.ParameterOwnership`
  cannot answer this — its walk is scoped to resource borrow-read builtins and classifies a plain
  inspecting helper's parameter as consumed. Every consumer of the consumed-tail gate
  (`ComputeTcoParamFacts`, `IsBorrowableInspectOnlyList`) sees through a proven callee unchanged, so
  a traversal that hands its tail to a read-only helper no longer takes the defensive
  `CopyOutArena` normalization path.
- [ ] Classify copy, RC-managed, resource, borrowed-view, region, and unsupported heap layouts with
  constructor-specific child/drop information.
- [ ] Lay out a single-constructor ADT (one non-nullary constructor; not a builtin, zero-cost newtype,
  resource, or resource-bearing type) without a tag word: payload at offset 0, one word smaller per
  cell, with every ADT instruction that allocates, reads or writes such a cell carrying its tagless
  flag so the backend never consults the type for an offset; skip the tag test in a match against
  such a type and load its constructor tag as a literal in synthesized droppers and copiers; and
  keep reuse tokens layout-exact (a tagless token never satisfies a tagged constructor of the same
  field count, nor the reverse). Build the classifier with this layout from the start rather than
  porting the uniform tagged layout and unboxing it afterwards.
- [ ] Insert Perceus duplication/drop operations and deterministic resource cleanup across ordinary,
  exceptional, handler, and coroutine control flow.
- [ ] Retain a runtime-managed owned binding that a tail self-call argument carries out of its scope:
  `let label = helper(...) in loop(n - 1)(Wrapped(instruction = Jump(label), ...) :: acc)` stores the
  let-bound RC call result into the constructor field, and the binding's own scope-exit release
  still fires at the back edge, so without a retain the next iteration's parameter holds a freed
  reference (silently wrong values when the callee returned a literal, a heap-allocation failure
  when it returned an RC string). A tail self-call argument escapes every binding scope of the
  iteration exactly like a function result escapes its callee, so it must request the same
  transfer treatment (`TransfersRuntimeManagedChildren`) and the constructor-argument path must
  honor that request even without an owning consumer of the aggregate itself, leaving the Perceus
  pattern-owner duplicate gated as before. **Found only by compiling and running the self-hosted
  optimizer's own switch-fold test**; the bug predates the recent optimizer arc. Regression:
  `tests/tco_let_call_result_in_accumulator_record.ash`.
- [ ] Decide a `let`'s runtime-RC ownership from what its value temp *is*, not how it is represented:
  only a fresh producer (call result, constructor, concatenation) or a transferred value confers a
  reference the binding may release at scope exit. A plain read of an RC-normalized slot — a TCO
  loop parameter that the back edge's flag-gated parameter releases already own, an env capture, a
  pattern field — is a borrowed read that never retained, so `let r = acc in … loop(n - 1)(r + h)`
  must not register `r` as a runtime-managed owner: with both the back-edge parameter drop and an
  owning scope-exit drop for `r`, every iteration releases the same reference twice (and the
  `then r` return path drops the value before the exit transfer hands it out). **Found only by
  compiling and running the self-hosted projects package**: the heterogeneous mutual-recursion
  merge synthesizes exactly this alias (`let result = __recgroup_arg2`) in every member arm, and the
  merged `pascalCaseCharacters`/`continuePascalCase` group crashed the `selfhost/tests/projects`
  binary with a bus error; a plain single-function alias loop double-dropped silently on every
  earlier compiler. Regression: `tests/tco_let_alias_of_rc_parameter.ash`.
- [ ] Supply the evidence for a trait requirement inside a constrained function from the
  requirement's own instantiated type, never by trait name alone: a function that carries an `Eq(a)`
  dictionary and, in its body, calls another `Eq`-constrained function at a *concrete* type
  (`lookup(fnLabel)(knownLabels)` with `fnLabel: Str` inside a function polymorphic in the temp it
  looked up first) must resolve `Eq(Str)` statically, not thread its own `Eq(a)` dictionary through
  — with the caller's `Eq(Int)` that is a pointer comparison of strings (right for two interned
  literals, silently `None` for any computed string), and with the roles reversed it compares
  integers as strings and faults. A syntax-only pre-pass that threads evidence by trait name can
  only be a hint; the call lowering must unify the real arguments first and keep the hint solely
  for a requirement that is still a bare type variable, and the active-dictionary fallback must
  never serve a concrete or structured requirement. **Found only by compiling and running the
  self-hosted optimizer's returned-closure devirtualization**, whose label lookup passed on
  hand-built fixtures (interned labels) and failed on collector-built ones. Regression:
  `tests/trait_concrete_requirement_inside_polymorphic_function.ash`.
- [ ] Keep every operator operand out of tail position: in a function that is a TCO loop because
  some branch ends in a genuine tail self-call, a self-call that is an operand of `+`, `-`, a
  comparison, a negation, or a pipe in another branch (`then 1 + count(tail) else count(tail)`)
  is an ordinary call whose result the operator consumes, never a back-edge jump. Tail position
  is a property the lowering must clear at every operand boundary, not only at call arguments,
  `let` values, and scrutinees; a function with no genuine tail call never exposes the gap, which
  is why a plain `n + sumTo(n - 1)` never caught it. **Found only by compiling and running the
  self-hosted optimizer's own tests**, whose `1 + count(tail)` / `count(tail)` list walk returned
  0. Regression: `tests/tco_non_tail_self_call_in_operator_operand.ash`.
- [ ] Retain every runtime-managed child an escaping or owning aggregate stores, whatever the
  aggregate: the ADT constructor path retains owned bindings and loop parameters it stores, and
  tuples, list literals, and cons cells must do exactly the same — a runtime-RC tuple owns its
  children, and an escaping arena tuple or cell carries them out of the scopes that own them (a
  `let`'s scope exit, a TCO loop's exit drop, which recognizes only a parameter that *is* the
  result). A loop parameter's placement is decided after its body is lowered, so the retain is a
  marker upgraded at finalization when the placement is runtime-RC, and never inside a tail
  self-call's arguments where the back edge moves the parameter into the successor. `(xs, ys)`
  from a two-accumulator loop, `(ys, zs)` of two let-bound lists, `[xs, ys]`, and
  `(left :: rights, absorbed)` all read back freed cells once later allocations reused them.
  **Found only by compiling and running the self-hosted optimizer's concatenation-chain walk.**
  Regression: `tests/aggregate_result_retains_runtime_managed_children.ash`.
- [ ] Release a TCO loop's aggregate result in its caller: a call to a loop whose result is a
  tuple or an ADT built from its accumulators (`walk(50)([])([])` returning `(xs, ys)` or
  `Pair(xs, ys)`) is never dropped by the consumer that destructures it, so every call leaks the
  result and its children (peak RSS grows linearly with the call count: 8 MB at 1,000 calls,
  630 MB at 200,000), while a plain list result of the same loop and a tuple result of a non-TCO
  function are released correctly. Pre-existing and independent of the retain above, which turned
  this shape from a use-after-free into a leak; measure with a fixed-N driver and `/usr/bin/time -v`.
- [ ] Release a plain runtime-RC value extracted by a match pattern and passed **by name** as a TCO
  back-edge argument (e.g. `match advance(k)(st) with | Continue(next, r) -> loop(k - 1)(next)`):
  argument evaluation retains `next` for the successor parameter, so the back-edge's drop bookkeeping
  must also release the pattern-bound owner's own reference once the successor is established — it is
  not a moved value and must not follow the moved-argument rule written for resources and
  closures-with-droppers, which excludes an argument from the back-edge drop set. Getting this wrong
  leaks one reference per iteration with no observable failure until the retained graph itself grows
  unboundedly (confirmed via `fannkuch-redux`: 2.4 GB peak RSS at N=10, 27 GB at N=11, both flat at
  8.2 MB once fixed) — a plateau-over-iterations regression test, not a single-shot correctness test,
  is the only kind that catches this class of bug.
- [ ] Release the RC-managed result of a call consumed only by a read-only builtin
  (`Ashes.Text.byteLength`, `print`, `write`) once nothing else owns it: a fresh, unowned,
  RC-normalized call result (from `CopyOutArena`/`CopyOutList` normalization, or an RC-returning
  callee on both branches of a conditional return) has no natural release site, because lifetime
  placement only moves drops anchored to an existing owner and never invents one for an anonymous
  temporary. Three propagation facts must stay consistent together, since dropping any one silently
  reopens the leak one layer up: (1) a read-only builtin argument release must fire whenever the
  argument was itself freshly produced (a general "newly produced" ownership fact, not builtin-specific
  analysis) and must decline for a borrowed binding or literal; (2) an if/match join keeps a merged
  value's "newly produced" fact only when *every* arm was itself freshly produced — one borrowed or
  owned arm (including a runtime-managed but caller-owned TCO parameter) poisons the merge, since
  releasing a merged owned value would free memory still in use elsewhere; (3) a let-scope's
  save/reload around its own exit drops must preserve the same "newly produced" fact across the reload,
  since the reloaded temp names the same fresh object the pre-reload analysis already classified.
  Measured: an unfixed loop consuming `byteLength(f(i))` from a closure call each iteration grew to
  163 MB over 5,000,000 iterations against an 8.2 MB floor, at both `-O0` and `-O2`; a regression test
  for this class needs a long-running plateau check (a single-shot output comparison cannot see a
  32-bytes-per-iteration leak).
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
- [ ] Emit every fixed-size runtime-helper scratch `alloca` (RC-block acquisition, free-list bin
  lookup, dynamic allocation, copy-out/reclaim, BigInt formatting, and any future helper with the same
  shape) positioned in the function's **entry block**, never at the current insertion point inside a
  loop body — LLVM's canonical rule is that a fixed-size alloca belongs in the entry block as one
  frame slot allocated once; a fixed-size alloca emitted elsewhere is lowered at `-O0` as a runtime
  stack-pointer adjustment that only function return or `llvm.stackrestore` reclaims, so one inside a
  TCO loop body leaks native stack every iteration. `-O2` hides this completely (`mem2reg`/SROA
  promotes the scratch alloca away), so does every `-O2`-compiled test — an `.ash` test cannot guard
  this at all, since the end-to-end runner defaults to `-O2`; only a C# test that compiles at `-O0`
  explicitly can. Confirmed with a TCO loop allocating one fresh heap cell per iteration: `rsp`
  dropped exactly 112 bytes per iteration and never recovered, segfaulting a 1,000,000-element build
  at `-O0`. The genuine `AllocStack` path (managed by its own `SaveStackPointer`/`RestoreStackPointer`
  bracket, not a fixed helper slot) is a different mechanism and correctly stays a loop-body alloca —
  do not route it through the same entry-block hoist.
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

#### Bootstrap, release, and default-toolchain gates

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
- [ ] Retire the .NET compiler and tooling servers from the default and release paths only after
  sustained bootstrap and release parity. Keep their sources buildable and tested in the repository as
  the permanent stage-0 and behavioral reference toolchain.
- [ ] After the self-hosted compiler becomes the default, make any source-tree reorganization a
  separate mechanical change. Prefer `toolchains/ashes/` for the primary implementation and
  `toolchains/dotnet/` for the preserved .NET implementation; keep shared documentation, libraries,
  tests, examples, and runtime payloads at the repository root.

### Continuation discipline

- Start behavior changes with a pure-Ashes failing test under `selfhost/tests/<package>/`; keep unit tests
  within the owning package and add cross-package tests only at real public boundaries.
- Keep test suites flat: compose small named checks through pipelines instead of sequencing them with
  deeply nested `let ... in` pyramids.
- Treat .NET stage-0 comments as audit input while porting. Preserve non-obvious observable behavior,
  ordering, ownership, diagnostics, and phase boundaries in a module header or a local standalone
  comment; do not copy XML documentation, C# API narration, or host-specific implementation details.
- Keep each milestone on a fresh `feature/...` branch and worktree. Copy `runtimes/` from the main
  checkout into a new worktree before backend-dependent validation; runtime payloads are intentionally
  not regenerated by the self-hosted work.
- Do not delete the existing .NET or Node.js implementations as part of or after the port. Keep the
  .NET implementation buildable and tested as the permanent stage-0 and behavioral reference; changing
  which implementation is shipped or launched by default is a separate decision.
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
