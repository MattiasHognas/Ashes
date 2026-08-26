# Self-hosted Ashes toolchain

This tree contains the Ashes implementation of the complete Ashes toolchain. It is separate from the
current C# and Node.js implementations while bootstrap equivalence is established.

## Package dependency graph

Packages follow the same strict direction as the current toolchain. A package may depend only on the
layers listed below; tests are separate projects and use the package under test as a
`devDependency`.

| Package | May depend on |
|---|---|
| `frontend` | nothing outside the shipped Ashes standard library |
| `semantics` | `frontend` |
| `backend` | `semantics` |
| `formatter` | `frontend` |
| `test-runner` | `backend` |
| `lsp` | `frontend`, `semantics`, `formatter` |
| `dap` | protocol and process packages only; never compiler implementation packages |
| `cli` | `frontend`, `semantics`, `backend`, `formatter`, `test-runner` |
| `fuzzing` | the public surfaces it exercises; fuzz execution remains outside compiler packages |

Production package manifests are versioned and express cross-package dependencies as registry
constraints. The repository manifests pair those portable contracts with committed locks and explicit
root-level path overrides for local development; publishing strips the overrides, and the source
archive includes the package entry even when it is outside `sourceRoots`. Test projects declare their
own overrides because dependency-provided overrides are intentionally ignored.

The eventual bootstrap stages, test infrastructure, CLI, LSP, DAP, TestRunner, and deterministic
fuzzing runner will be implemented in Ashes as well. Host-language helper programs are not part of
self-hosted implementation or its test path.

## Implementation status

| Area | Current self-hosted surface |
|---|---|
| Package contracts | Complete portable source archives for frontend, formatter, and semantics, with versioned registry dependencies, exact locks, and declared local overrides |
| Frontend | Source tokens, UTF-8 spans, lexical diagnostics, the complete lexer, the typed AST model, inline-module lifting, restricted-body validation, compatibility/explicit nested-module interfaces, and dependency planning, expression/pattern/type parsing, and whole-program parsing for every current declaration form and trailing bodies; versioned token-stream parity against stage 0 |
| Formatter | Canonical whole-program, declaration, expression, pattern, and type rendering with precedence preservation and idempotence coverage |
| Semantics | Stable symbols and lexical scopes; dependency-ordered module semantic scopes with sequential and recursive visibility boundaries, resolved import bindings, qualified access, deterministic definition identities, provenance, stable compiler names, span-preserving syntax-tree reference rewriting, and package-aware inference of the combined project with module-local deriving, program-global deriving eligibility context, and program-global trait coherence; source type resolution for primitives, parameters, transparent aliases, nominal and zero-cost applications, functions, tuples, pointers, and capability rows; semantic substitutions, unordered open-row unification, constrained schemes; annotation-aware Algorithm W inference for core expressions, operators, records, guarded matches, Result pipelines, and `let?`; sequential whole-program inference with polymorphic constructors and shared-monomorphic recursive groups; external source typing for opaque and resource declarations, ownership modes, scalar and pointer ABI types, buffers, compiler-owned out values, native-string results, symbols and libraries, closed runtime capability rows, and direct-only versus first-class call contracts; validated external ABI metadata with representation erasure, destructor contracts, ordered parameter/source shapes, symbols, libraries, direct-call constraints, and sorted runtime authority; a complete typed IR program/function/instruction model with all current operations, task-frame ABI metadata, stable source/generated function lineage, deterministic lowered/final text dumps, shared source/generated function selection, and core lowering for constants, lexical locals, strict curried calls, closures, deterministic captures, partial application, lifted functions, conditions, guarded scalar and structural matches, recursive functions, ordered shared-environment recursive groups, tuples, immutable lists, strings, tagged and zero-cost constructors, records, field access, immutable record updates, operators, BigInt literals, the shipped non-async builtin registry and operations, external calls with C-string/out-parameter/native-string conversions, direct-only enforcement, library/symbol lineage, and resource cleanup, capability handlers with stack frames and evidence snapshots, dynamic perform dispatch with unhandled panic guards, static capability providers, Stop capability server shutdown requests, trait dictionary construction/method dispatch, and coroutine state machine transformation with await-splitting, live variable preservation across suspend points, state dispatch, resume prologues, structured parallelism verification, and coroutine frame slot and representation record construction; single-file and multi-file source context mapping with UTF-8 line/column coordinate resolution and runtime machinery filtering; function origin construction and AST source function discovery across entry, source functions, lambdas, specializations, wrappers, coroutines, normalizers, droppers, and copiers; hover type indexing and public authority collection; and compilation decision snapshots capturing function ownership, value placements, and external authority records; registered capability declarations with qualified, parameter-sharing operation schemes; open-row effect propagation through implicit and explicit operation calls, lambdas, higher-order calls, and Result mappers; complete handler-arm, `resume`, return-arm, shared-instance, and effect-discharge inference; coherent, complete, instance-specialized, type-checked static provider registration with exact concrete call-site satisfaction, abstract requirement preservation, and provider/handler ambiguity rejection; registered trait declarations with constrained qualified method schemes, forward supertraits, cycle checks, and type-checked default bodies; ordinary trait implementation registration with rigid generic heads, validated requirements, optional inherited defaults, substituted method/body capability-row checks, and deterministic duplicate/structural-overlap rejection; deterministic trait-evidence ABI, resolution, construction, forwarding, method-access, and value-transport plans; constrained-value, constrained-reference, and concrete dictionary-value rewriting; a seeded shipped standard-trait ABI with primitive and structural evidence heads bound to rewritten `Ashes.Trait` source bodies by alpha-normalized head structure; and pre-coherence `Eq`, `Ord`, `Show`, and `Hash` deriving expansion for ordinary and zero-cost nominal types with declaration-aware rejection of resource, opaque-external, capability, and unsupported-alias fields; compile-time evaluation of pure constant-argument calls and the deterministic IR optimization pipeline (ownership-copy elision, RcDup sinking and pair fusion, known-closure devirtualization, constant propagation with a true meet-over-paths at multi-predecessor labels over temp and local-slot facts, known-condition branch and known-tag switch folding, identity/strength reduction with a second copy elision over the copies it introduces, unreachable and dead code elimination, erased-drop elision, whole-program returned-closure devirtualization of curried calls, and interprocedural arena-bracket elimination); ordinary and mutual tail-call analysis with profitability and cost signals; and lowered-IR invariant validation |
| Projects | Typed manifests and lock files; discovery and deterministic source enumeration; recursive path dependency graphs; restored locked packages consumed from the content-addressed cache; root-only local overrides with exact locked namespace/version validation; reachable compilation planning across project, include, and dependency roots, including structured parse diagnostics in stable source/span/emission order, nested inline-module provenance, child-before-parent ordering, collision rejection, export visibility, aliases, selectors, and module/type ambiguity; semantic scope and syntax stitching over dependency-ordered plans; declaration-only entry inference with non-entry bodies ignored; and package-provenance-preserving inference of the stitched program |
| Backend, test runner, LSP, DAP, CLI, and fuzzing | Not started |

The syntax model orders `Pattern` and `TypeExpr` before `Expr` so each category remains distinct in
Ashes' sequential type-declaration model. Match cases and handler arms are typed tuples inside
`Expr`; this removes the two mutual type-declaration cycles in the C# record graph without weakening
the public expression, pattern, or type categories.

## Module documentation

Every production `.ash` module starts with its responsibility and load-bearing invariants. Local
comments explain non-obvious behavioral contracts, ordering rules, ownership constraints, and
algorithmic choices where the code alone does not explain why they matter. The .NET stage-0 comments
are audit input for these contracts, but C#-specific API narration and implementation details are not
copied into the pure-Ashes sources. Migration status remains in this README and
`docs/md/future/SELF_HOSTING.md`, not in module headers.

## Test discipline

Each port starts with pure-Ashes tests derived from the current implementation's observable
contract. Unit tests live under `selfhost/tests/<package>/`, integration tests cross package
boundaries only when the production tool does, and bootstrap parity tests compare serialized public
results rather than internal representations.

Run the self-hosted frontend tests with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/frontend/ashes.json \
  -o /tmp/ashes-selfhost-frontend-tests
/tmp/ashes-selfhost-frontend-tests
```

Run the shared stage-0/self-hosted token parity fixtures with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/frontend-token-parity/ashes.json \
  -o /tmp/ashes-selfhost-frontend-token-parity-tests
/tmp/ashes-selfhost-frontend-token-parity-tests selfhost/parity/frontend/tokens
```

Run the shared stage-0/self-hosted lowered IR parity fixtures with:

```bash
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/SelfhostIrParityTests/**"
```

Set the env variable ASHES_UPDATE_PARITY_FIXTURES=1 to generate new ir output.

The fixture schema and extension rules are documented in [`parity/README.md`](parity/README.md).

Run the self-hosted formatter tests with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/formatter/ashes.json \
  -o /tmp/ashes-selfhost-formatter-tests
/tmp/ashes-selfhost-formatter-tests
```

Run the self-hosted semantics tests with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/semantics/ashes.json \
  -o /tmp/ashes-selfhost-semantics-tests
/tmp/ashes-selfhost-semantics-tests
```

Run the focused self-hosted project dependency and lock-file tests with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/projects/ashes.json \
  -o /tmp/ashes-selfhost-project-tests
/tmp/ashes-selfhost-project-tests
```

These tests cover recursive path dependencies, restored registry packages consumed from the selected
manifest's lock file and content-addressed source cache, and root-only local overrides that preserve the
locked package namespace and version. Registry resolution, downloads, cache writes, and source-hash
verification remain CLI work.
