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
| Frontend | Source tokens, UTF-8 spans, lexical diagnostics, the complete lexer, the typed AST model, expression/pattern/type parsing, and whole-program parsing for every current declaration form and trailing bodies; versioned token-stream parity against stage 0 |
| Formatter | Canonical whole-program, declaration, expression, pattern, and type rendering with precedence preservation and idempotence coverage |
| Semantics | Stable symbols and lexical scopes; dependency-ordered module semantic scopes with sequential and recursive visibility boundaries, resolved import bindings, qualified access, deterministic definition identities, provenance, and stable compiler names; source type resolution for primitives, parameters, transparent aliases, nominal and zero-cost applications, functions, tuples, pointers, and capability rows; semantic substitutions, unordered open-row unification, constrained schemes; annotation-aware Algorithm W inference for core expressions, operators, records, guarded matches, Result pipelines, and `let?`; sequential whole-program inference with polymorphic constructors and shared-monomorphic recursive groups; registered capability declarations with qualified, parameter-sharing operation schemes; open-row effect propagation through implicit and explicit operation calls, lambdas, higher-order calls, and Result mappers; complete handler-arm, `resume`, return-arm, shared-instance, and effect-discharge inference; coherent, complete, instance-specialized, type-checked static provider registration with exact concrete call-site satisfaction, abstract requirement preservation, and provider/handler ambiguity rejection; registered trait declarations with constrained qualified method schemes, forward supertraits, cycle checks, and type-checked default bodies; ordinary trait implementation registration with rigid generic heads, validated requirements, optional inherited defaults, substituted method/body capability-row checks, and deterministic duplicate/structural-overlap rejection; deterministic trait-evidence ABI, resolution, construction, forwarding, method-access, and value-transport plans; constrained-value, constrained-reference, and concrete dictionary-value rewriting; a seeded shipped standard-trait ABI with primitive and structural evidence heads; and pre-coherence `Eq`, `Ord`, `Show`, and `Hash` deriving expansion for ordinary and zero-cost nominal types |
| Projects | Typed manifests and lock files; discovery and deterministic source enumeration; recursive path dependency graphs; restored locked packages consumed from the content-addressed cache; root-only local overrides with exact locked namespace/version validation; reachable compilation planning across project, include, and dependency roots; and semantic scope stitching over dependency-ordered plans |
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
