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

The eventual bootstrap stages, test infrastructure, CLI, LSP, DAP, TestRunner, and deterministic
fuzzing runner will be implemented in Ashes as well. Host-language helper programs are not part of
self-hosted implementation or its test path.

## Implementation status

| Area | Current self-hosted surface |
|---|---|
| Frontend | Source tokens, UTF-8 spans, lexical diagnostics, the complete lexer, the typed AST model, expression/pattern/type parsing, and whole-program parsing for every current declaration form and trailing bodies |
| Formatter | Canonical whole-program, declaration, expression, pattern, and type rendering with precedence preservation and idempotence coverage |
| Semantics | Stable symbols and lexical scopes; source type resolution for primitives, parameters, transparent aliases, nominal and zero-cost applications, functions, tuples, pointers, and capability rows; semantic substitutions, unification, constrained schemes; annotation-aware Algorithm W inference for core expressions, operators, and guarded matches; and sequential whole-program inference with polymorphic constructors and shared-monomorphic recursive groups |
| Backend, test runner, LSP, DAP, CLI, and fuzzing | Not started |

The syntax model orders `Pattern` and `TypeExpr` before `Expr` so each category remains distinct in
Ashes' sequential type-declaration model. Match cases and handler arms are typed tuples inside
`Expr`; this removes the two mutual type-declaration cycles in the C# record graph without weakening
the public expression, pattern, or type categories.

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
