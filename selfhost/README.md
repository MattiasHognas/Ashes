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
| Package contracts | Complete portable source archives for frontend, formatter, semantics, and (so far, `add`/`fmt`/`init`/`remove`/`tree`/`why` only) cli, plus (so far, LLVM C API bindings only) backend, with versioned registry dependencies, exact locks, and declared local overrides |
| Frontend | Source tokens, UTF-8 spans, lexical diagnostics, the complete lexer, the typed AST model, inline-module lifting, restricted-body validation, compatibility/explicit nested-module interfaces, and dependency planning, expression/pattern/type parsing, and whole-program parsing for every current declaration form and trailing bodies; versioned token-stream and diagnostic-corpus parity against stage 0 |
| Formatter | Canonical whole-program, declaration, expression, pattern, and type rendering with precedence preservation and idempotence coverage; whole-file formatting that keeps the leading comment block, re-renders the import header canonically, and reinserts standalone comments at their token-signature anchors; configurable indent size, tabs-vs-spaces, and newline style (`FormattingOptions`/`formatProgramWithOptions`/`formatExpressionWithOptions`) applied as a post-processing rescale over the fixed-4-space internal output; opt-in pipeline-layout collection that rewrites an eligible nested call chain into `x |> f |> g` |
| Semantics | Stable symbols and lexical scopes; dependency-ordered module semantic scopes with sequential and recursive visibility boundaries, resolved import bindings, qualified access, deterministic definition identities, provenance, stable compiler names, span-preserving syntax-tree reference rewriting, and package-aware inference of the combined project with module-local deriving, program-global deriving eligibility context, and program-global trait coherence; source type resolution for primitives, parameters, transparent aliases, nominal and zero-cost applications, functions, tuples, pointers, and capability rows; semantic substitutions, unordered open-row unification, constrained schemes; annotation-aware Algorithm W inference for core expressions, operators, records, guarded matches (with a bare nullary constructor name resolved as that constructor's pattern, and every match checked for constructor-set, list, bool, and per-field coverage, unreachable arms, and mixed-ADT constructor patterns with stage 0's diagnostic wording), Result pipelines, `let?`, and `await`/`let!` over the seeded `Task(e, a)` type; sequential whole-program inference with polymorphic constructors and shared-monomorphic recursive groups; external source typing for opaque and resource declarations, ownership modes, scalar and pointer ABI types, buffers, compiler-owned out values, native-string results, symbols and libraries, closed runtime capability rows, and direct-only versus first-class call contracts; validated external ABI metadata with representation erasure, destructor contracts, ordered parameter/source shapes, symbols, libraries, direct-call constraints, and sorted runtime authority; a complete typed IR program/function/instruction model with all current operations including the N-ary string concatenation, task-frame ABI metadata, stable source/generated function lineage, deterministic lowered/final text dumps, shared source/generated function selection, and core lowering for constants, lexical locals, strict curried calls, closures, deterministic captures, partial application, lifted functions with dead-capture pruning at the creation site, conditions, guarded scalar and structural matches with dead-arm trimming gated to exact-coverage pattern shapes and tag-group switch dispatch (one tag switch per match, trivial single-case groups binding their payload without a tag re-test, other groups tested linearly within the group and falling through to the trailing default arm), recursive functions, ordered shared-environment recursive groups, whole-program lowering driving a `ProgramSyntax`'s top-level `let`/`let recursive`/mutual-recursion-group declarations directly rather than only a single expression (with duplicate-top-level-binding rejection), tuples, immutable lists, strings, tagged and zero-cost constructors, records, field access, immutable record updates, operators, BigInt literals, the shipped non-async builtin registry and operations, external calls with C-string/out-parameter/native-string conversions, direct-only enforcement, library/symbol lineage, and resource cleanup, capability handlers with stack frames and evidence snapshots, dynamic perform dispatch with unhandled panic guards, static capability providers, Stop capability server shutdown requests, trait dictionary construction/method dispatch, and coroutine state machine transformation with await-splitting, live variable preservation across suspend points, state dispatch, resume prologues, structured parallelism verification, and coroutine frame slot and representation record construction; single-file, multi-file, and stitched-project source context mapping with UTF-8 line/column coordinate resolution (a stitched item's span resolved through its module region against that module's own file) and runtime machinery filtering, with every lowered instruction tagged by its innermost enclosing source span; function origin construction and AST source function discovery across entry, source functions, lambdas, specializations, wrappers, coroutines, normalizers, droppers, and copiers; hover type indexing and public authority collection; and compilation decision snapshots capturing function ownership, value placements, and external authority records; registered capability declarations with qualified, parameter-sharing operation schemes; open-row effect propagation through implicit and explicit operation calls, lambdas, higher-order calls, and Result mappers; complete handler-arm, `resume`, return-arm, shared-instance, and effect-discharge inference; coherent, complete, instance-specialized, type-checked static provider registration with exact concrete call-site satisfaction, abstract requirement preservation, and provider/handler ambiguity rejection; registered trait declarations with constrained qualified method schemes, forward supertraits, cycle checks, and type-checked default bodies; ordinary trait implementation registration with rigid generic heads, validated requirements, optional inherited defaults, substituted method/body capability-row checks, and deterministic duplicate/structural-overlap rejection; deterministic trait-evidence ABI, resolution, construction, forwarding, method-access, and value-transport plans; constrained-value, constrained-reference, and concrete dictionary-value rewriting; a seeded shipped standard-trait ABI with primitive and structural evidence heads bound to rewritten `Ashes.Trait` source bodies by alpha-normalized head structure; and pre-coherence `Eq`, `Ord`, `Show`, and `Hash` deriving expansion for ordinary and zero-cost nominal types with declaration-aware rejection of resource, opaque-external, capability, and unsupported-alias fields; compile-time evaluation of pure constant-argument calls and the deterministic IR optimization pipeline (ownership-copy elision, RcDup sinking and pair fusion, known-closure devirtualization (including let-bound local helpers through their single-store slot), constant propagation with a true meet-over-paths at multi-predecessor labels over temp and local-slot facts, known-condition branch and known-tag switch folding, identity/strength reduction with a second copy elision over the copies it introduces, control-flow simplification (jump threading, unreferenced-label removal, redundant fall-through elision) iterated to a fixed point with unreachable code elimination, dead code elimination, erased-drop elision, local common-subexpression elimination of duplicate field reads and duplicate pure known calls within a straight-line block over a local-slot/borrow alias map with store-to-load forwarding through records allocated in the same block, whole-program captured-closure devirtualization of a call through an environment word every creation site fills with the same closure, whole-program returned-closure devirtualization of curried calls, currying-stage inlining of a pure copy-and-return stage into a caller-frame environment, one- and two-scalar-capture closure environment scalarization through generated raw-parameter callee variants, interprocedural arena-bracket elimination, and last-step folding of left-nested single-use string-concatenation chains into one N-ary concatenation); ordinary and mutual tail-call analysis with profitability and cost signals; and lowered-IR invariant validation |
| Projects | Typed manifests and lock files; discovery and deterministic source enumeration; recursive path dependency graphs; restored locked packages consumed from the content-addressed cache; root-only local overrides with exact locked namespace/version validation; reachable compilation planning across project, include, and dependency roots, including structured parse diagnostics in stable source/span/emission order, nested inline-module provenance, child-before-parent ordering, collision rejection, export visibility, aliases, selectors, and module/type ambiguity; semantic scope and syntax stitching over dependency-ordered plans; declaration-only entry inference with non-entry bodies ignored; and package-provenance-preserving inference of the stitched program |
| CLI | `add`, `fmt`, `init`, `remove`, `restore` (path dependencies only), `tree`, and `why`, wired into an actual runnable `ashes` executable (`selfhost/packages/cli`). `Package.ash` is now the real entry point (`Ashes.IO.exit(runCli(Ashes.IO.args))`); `Dispatch.ash`'s `runCli` matches the command name case-insensitively and routes to the commands below, falling through to usage on no arguments or an unknown command, and treating a leading `--help`/`-h` as global help regardless of what follows — verified by compiling and running the actual binary end-to-end (`init` → `add` → `tree` → `remove`), not just unit tests. `fmt`: file/directory discovery (recursive `.ash` enumeration under a directory, sorted), preview (stdout) and `-w`/`--write` (rewrite only when content changed) modes, the inline-`module`-block skip carve-out, and stage 0's `0`/`1`/`2` exit-code contract; deliberately narrower than stage 0 for now, with no `.editorconfig` resolution (always the formatter's 4-space/`\n` defaults) and no elapsed-time in the write-mode summary (no monotonic-clock capability is shipped yet). `init`: scaffolds `ashes.json` (name from the target directory's basename) and `src/Main.ash` (only when absent), failing without writing anything if `ashes.json` already exists; verified byte-for-byte identical to stage 0's own output. `why`: resolves the target project's manifest, flattens its dependency graph, and breadth-first searches from the root's own direct dependencies over the lock-recorded edges to report the shortest path to a target namespace or that it isn't a dependency. `tree`: resolves the same root-dependency/lock-edge data as `why` and renders it as a plain-text guide-connected tree (root, then each direct dependency and its lock-recorded transitive dependencies), a shared branch expanded wherever it's reachable and only a same-path cycle cut and marked. `add`: edits the manifest's raw JSON directly (not the typed `ProjectManifest` model) so unknown fields survive, updating an existing dependency entry in place or appending a new one, and re-serializes with a private indented JSON writer matching `System.Text.Json`'s default pretty-printer closely enough for a manifest file. `remove`: shares `add`'s raw-JSON model and indented writer, removing the named package from both `dependencies` and `devDependencies` and omitting either field once it empties out. `restore`: resolves and lists a project's dependencies via the same `resolveProjectDependencyGraph` `tree`/`why` use, honoring root-level `overrides` so a registry-named dependency pointed at a local path resolves normally; a root dependency with a real registry source and no override refuses cleanly rather than claiming a restore it cannot perform (no network access, lock-file writing, or hash verification exists yet). Every other subcommand is unported |
| Backend | `selfhost/packages/backend`'s `AshesCompiler.Backend.Llvm` binds a growing subset of the LLVM C API (context/module/builder/type/value/basic-block/global creation, arithmetic/comparison/multi-arm switch dispatch, control flow with the no-`phi` alloca/store/load slot pattern, function calls, and object/assembly emission with host-CPU-tuned target machines), proven end to end by `selfhost/tests/backend`'s hand-built test programs, checked with `readelf` and independent exact-instruction assembly dumps. `AshesCompiler.Backend.IrCodegen` is the first genuinely IR-driven slice: it walks a REAL `IrFunction` (produced by running actual source through the self-hosted `Frontend`/`Semantics` packages) and drives the LLVM bindings from its real instructions, currently covering scalar arithmetic, locals, control flow, and `PrintInt` (`LoadConstInt`/`MulInt`/`AddInt`/`SubInt`/`CmpIntGt`/`StoreLocal`/`LoadLocal`/`Label`/`Jump`/`JumpIfFalse`/`Return`/`PrintInt`, enough for a plain `if`/`then`/`else` and printing a computed `Int`), with a zero-field `AllocAdt` (a stack slot standing in for real arena allocation) plus, now, a real `malloc`-backed `AllocAdt`/`SetAdtField` for any field-carrying constructor (`runtimeManaged = fieldCount > 0`, a conservative RC-by-default classification — verified byte-exact via assembly dump: real 16-byte RC header, tag/field words at the documented offsets), and arena bookkeeping instructions otherwise treated as explicit no-ops rather than real scoped-arena codegen. `CoreLowering.ash` now emits a single, non-cascading `RcDrop` for a top-level RC-managed binding that is never referenced again (a narrow, always-safe case — cascading drops, multi-argument constructors, and shadowing-aware liveness remain unimplemented); `IrCodegen`'s `RcDrop` codegen decrements the header and calls `free` only at zero, verified via a real-IR test and a byte-exact assembly dump. `IrCodegen` also gained `GetAdtField`/`GetAdtTag` (the read half of `SetAdtField`/`AllocAdt`'s tag write), verified via a hand-built `IrFunction` since no real source can drive them yet — real `match` needs a null-representable-type check and per-arm RC cleanup not yet built, and a user-defined `type` declaration has no lowering path at all. The entry function's `Return` lowers unconditionally to a raw Linux `exit` syscall plus `buildUnreachable` rather than `ret`, matching the real linker's `e_entry` contract (the entry function's own machine code is the literal OS process entry point, with no return address on the stack); `PrintInt` reuses the same inline-assembly syscall mechanism for a raw `write`. `AshesCompiler.Backend.ElfLinker` links objects into real linux-x64 executables (pure Ashes byte manipulation, no `ld`/`lld`), choosing automatically between two paths: a real `.ash` **file** (`Ashes.IO.print(42 - 84)`) has been compiled through the complete self-hosted pipeline, statically linked, `chmod +x`'d, and spawned by the automated test suite itself, producing real stdout (`-42`) and exit code `0`; separately, the linker also now supports dynamic linking — `.text` relocations against a known external symbol (`malloc`/`free`) link a real dynamically-linked executable (`jmp`-through-GOT stubs, `.dynamic`/hash/`.dynstr`/`.dynsym`/`.rela.dyn`), verified via a hand-built module and `strace`: the kernel loads the real `ld-linux-x86-64.so.2`, which loads real glibc and calls its actual `malloc` (a genuine `brk` syscall), before the program's own `exit(0)` fires — the first Linux `ld.so`-loaded, real-libc-calling executable this compiler has produced (`IrCodegen` doesn't call `malloc`/`free` from real IR yet, so this proves the linker mechanism alone via a hand-built module, not an end-to-end `.ash` file). Landing the print path also required a genuinely separate fix in `AshesCompiler.Semantics.CoreLowering`: builtin resolution is now intrinsic (`standardBuiltinLayouts`/`standardConstructorLayouts`, seeded automatically by `initialState`), matching language.md's "qualified access (no import required)"; `standardConstructorLayouts` now also covers `Maybe`'s `None`/`Some` and `Result`'s `Ok`/`Error` on the same "always available, no import required" basis. See `docs/md/future/SELF_HOSTING.md`'s "LLVM code generation and runtime integration" checklist for the current surface and exact scope boundary. No optimization-level selection, and closures, non-trivial ADTs, RC/drop/reuse codegen, real arena/region codegen, TLS sections, the other three target RIDs, and the rest of the LLVM C API surface are unported |
| Test runner, LSP, DAP, and fuzzing | Not started |

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

## Phase benchmarks

`selfhost/bench/` is the standing comparison between the .NET compiler and the self-hosted
compiler, phase by phase (import header, lex, parse, format, inference, ...) over the same corpus
of `.ash` files. `selfhost/bench/run.sh` builds both sides and prints one table; the results table
in [`selfhost/bench/README.md`](bench/README.md) is refreshed with each self-hosting milestone and
gains a row whenever a phase becomes available on the self-hosted side.

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

Run the shared stage-0/self-hosted frontend diagnostic parity fixtures with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/frontend-diagnostic-parity/ashes.json \
  -o /tmp/ashes-selfhost-frontend-diagnostic-parity-tests
/tmp/ashes-selfhost-frontend-diagnostic-parity-tests selfhost/parity/frontend/diagnostics
```

Run the shared stage-0/self-hosted lowered IR parity fixtures with:

```bash
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/SelfhostIrParityTests/**"
```

Set the env variable ASHES_UPDATE_PARITY_FIXTURES=1 to generate new ir output.

Run the self-hosted whole-program consumer of those same fixtures with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/ir-program-parity/ashes.json \
  -o /tmp/ashes-selfhost-ir-program-parity-tests
/tmp/ashes-selfhost-ir-program-parity-tests selfhost/parity/semantics/lowered-ir
```

Only fixtures whose IR needs no ownership/reuse arena bracketing or constructor-layout registration
match yet (currently `simple_arith`); the rest are covered by `SelfhostIrParityTests` on the stage-0
side only until that machinery is ported.

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

Run the self-hosted `fmt` command tests with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/cli/ashes.json \
  -o /tmp/ashes-selfhost-cli-tests
/tmp/ashes-selfhost-cli-tests
```

Run the self-hosted backend (LLVM bindings and IR codegen) tests with:

```bash
dotnet run --project src/Ashes.Cli -- compile \
  --project selfhost/tests/backend/ashes.json \
  -o /tmp/ashes-selfhost-backend-tests
cp runtimes/linux-x64/lib*.so* /tmp/
/tmp/ashes-selfhost-backend-tests
```

Unlike every other selfhost test, this one links against `libLLVM` at runtime, so the vendored
`.so` files (`libLLVM.so` and its own dependencies — see `LLVM_DEPENDENCY_PACKAGES` in
`scripts/download-llvm-native.sh`) need to sit next to the compiled binary: it carries a `$ORIGIN`
RUNPATH, but nothing copies the library there automatically (see
`AshesCompiler.Backend.Llvm`'s own header comment for why that is this binary's own
responsibility, not the compiler's).
