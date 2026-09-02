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
| Package contracts | Complete portable source archives for frontend, formatter, semantics, and (so far, `add`/`fmt`/`init`/`remove`/`tree`/`why` only) cli, plus backend (the LLVM bindings, ELF linker, and the IrCodegen dotted-filename slices: Support/Filesystem/TextBytes/Environment/Process plus the core dispatcher, with the linux-x64 syscall wrappers isolated in the platform-suffixed Syscalls.LinuxX64 slice), with versioned registry dependencies, exact locks, and declared local overrides |
| Frontend | Source tokens, UTF-8 spans, lexical diagnostics, the complete lexer, the typed AST model, inline-module lifting, restricted-body validation, compatibility/explicit nested-module interfaces, and dependency planning, expression/pattern/type parsing, and whole-program parsing for every current declaration form and trailing bodies, with column-anchored match/handler arm attachment (a `|` dedented past the first arm's column ends the arm list, so a nested match cannot swallow the enclosing match's arms); versioned token-stream and diagnostic-corpus parity against stage 0 |
| Formatter | Canonical whole-program, declaration, expression, pattern, and type rendering with precedence preservation and idempotence coverage; whole-file formatting that keeps the leading comment block, re-renders the import header canonically, and reinserts standalone comments at their token-signature anchors; configurable indent size, tabs-vs-spaces, and newline style (`FormattingOptions`/`formatProgramWithOptions`/`formatExpressionWithOptions`) applied as a post-processing rescale over the fixed-4-space internal output; opt-in pipeline-layout collection that rewrites an eligible nested call chain into `x |> f |> g` |
| Semantics | Stable symbols and lexical scopes; dependency-ordered module semantic scopes with sequential and recursive visibility boundaries, resolved import bindings, qualified access, deterministic definition identities, provenance, stable compiler names, span-preserving syntax-tree reference rewriting, and package-aware inference of the combined project with module-local deriving, program-global deriving eligibility context, and program-global trait coherence; source type resolution for primitives, parameters, transparent aliases, nominal and zero-cost applications, functions, tuples, pointers, and capability rows; semantic substitutions, unordered open-row unification, constrained schemes; annotation-aware Algorithm W inference for core expressions, operators, records, guarded matches (with a bare nullary constructor name resolved as that constructor's pattern, and every match checked for constructor-set, list, bool, and per-field coverage, unreachable arms, and mixed-ADT constructor patterns with stage 0's diagnostic wording, the same check enforced again during single-file core lowering — `checkCoreMatchCoverage` fails the lowering with the stage-0 message for a non-exhaustive match or unreachable arm, deriving a variable-typed scrutinee's ADT from its arms' constructor names), Result pipelines, `let?`, and `await`/`let!` over the seeded `Task(e, a)` type; sequential whole-program inference with polymorphic constructors and shared-monomorphic recursive groups; external source typing for opaque and resource declarations, ownership modes, scalar and pointer ABI types, buffers, compiler-owned out values, native-string results, symbols and libraries, closed runtime capability rows, and direct-only versus first-class call contracts; validated external ABI metadata with representation erasure, destructor contracts, ordered parameter/source shapes, symbols, libraries, direct-call constraints, and sorted runtime authority; a complete typed IR program/function/instruction model with all current operations including the N-ary string concatenation, task-frame ABI metadata, stable source/generated function lineage, deterministic lowered/final text dumps, shared source/generated function selection, and core lowering for constants, lexical locals, strict curried calls, closures, deterministic captures, partial application, lifted functions with dead-capture pruning at the creation site, conditions, guarded scalar and structural matches with dead-arm trimming gated to exact-coverage pattern shapes and tag-group switch dispatch (one tag switch per match, trivial single-case groups binding their payload without a tag re-test, other groups tested linearly within the group and falling through to the trailing default arm), recursive functions, ordered shared-environment recursive groups, whole-program lowering driving a `ProgramSyntax`'s top-level `let`/`let recursive`/mutual-recursion-group declarations directly rather than only a single expression (with duplicate-top-level-binding rejection), tuples, immutable lists, strings, tagged and zero-cost constructors, records, field access, immutable record updates, operators, BigInt literals, the shipped non-async builtin registry and operations, external calls with C-string/out-parameter/native-string conversions, direct-only enforcement, library/symbol lineage, and resource cleanup, capability handlers with stack frames and evidence snapshots, dynamic perform dispatch with unhandled panic guards, static capability providers, Stop capability server shutdown requests, trait dictionary construction/method dispatch, and coroutine state machine transformation with await-splitting, live variable preservation across suspend points, state dispatch, resume prologues, structured parallelism verification, and coroutine frame slot and representation record construction; single-file, multi-file, and stitched-project source context mapping with UTF-8 line/column coordinate resolution (a stitched item's span resolved through its module region against that module's own file) and runtime machinery filtering, with every lowered instruction tagged by its innermost enclosing source span; function origin construction and AST source function discovery across entry, source functions, lambdas, specializations, wrappers, coroutines, normalizers, droppers, and copiers; hover type indexing and public authority collection; and compilation decision snapshots capturing function ownership, value placements, and external authority records; registered capability declarations with qualified, parameter-sharing operation schemes; open-row effect propagation through implicit and explicit operation calls, lambdas, higher-order calls, and Result mappers; complete handler-arm, `resume`, return-arm, shared-instance, and effect-discharge inference; coherent, complete, instance-specialized, type-checked static provider registration with exact concrete call-site satisfaction, abstract requirement preservation, and provider/handler ambiguity rejection; registered trait declarations with constrained qualified method schemes, forward supertraits, cycle checks, and type-checked default bodies; ordinary trait implementation registration with rigid generic heads, validated requirements, optional inherited defaults, substituted method/body capability-row checks, and deterministic duplicate/structural-overlap rejection; deterministic trait-evidence ABI, resolution, construction, forwarding, method-access, and value-transport plans; constrained-value, constrained-reference, and concrete dictionary-value rewriting; a seeded shipped standard-trait ABI with primitive and structural evidence heads bound to rewritten `Ashes.Trait` source bodies by alpha-normalized head structure; and pre-coherence `Eq`, `Ord`, `Show`, and `Hash` deriving expansion for ordinary and zero-cost nominal types with declaration-aware rejection of resource, opaque-external, capability, and unsupported-alias fields; compile-time evaluation of pure constant-argument calls and the deterministic IR optimization pipeline (ownership-copy elision, RcDup sinking and pair fusion, known-closure devirtualization (including let-bound local helpers through their single-store slot), constant propagation with a true meet-over-paths at multi-predecessor labels over temp and local-slot facts, known-condition branch and known-tag switch folding, identity/strength reduction with a second copy elision over the copies it introduces, control-flow simplification (jump threading, unreferenced-label removal, redundant fall-through elision) iterated to a fixed point with unreachable code elimination, dead code elimination, erased-drop elision, local common-subexpression elimination of duplicate field reads and duplicate pure known calls within a straight-line block over a local-slot/borrow alias map with store-to-load forwarding through records allocated in the same block, whole-program captured-closure devirtualization of a call through an environment word every creation site fills with the same closure, whole-program returned-closure devirtualization of curried calls, currying-stage inlining of a pure copy-and-return stage into a caller-frame environment, one- and two-scalar-capture closure environment scalarization through generated raw-parameter callee variants, interprocedural arena-bracket elimination, and last-step folding of left-nested single-use string-concatenation chains into one N-ary concatenation); ordinary and mutual tail-call analysis with profitability and cost signals; lowered-IR invariant validation; and shipped-module stitching that resolves an import of an intrinsic builtin module (`Ashes.IO` and the rest of `coreBuiltinKind`'s modules, which have no shipped `.ash` source) to a synthesized empty module, leaving its members to the no-import-required qualified-access path; and per-module `moduleAliases` maps (explicit alias and module-path leaf) that let the reference rewriter turn `io.print` back into `Ashes.IO.print` for intrinsic and shipped modules alike; `registerTopLevelTypeDeclaration` now implements language.md's implicit-type-parameter migration rule (a payload name with no explicit `(a)` on the type line is a fresh type parameter only when it denotes no known type — not the declaring type's own name, a primitive, an external opaque type, or an already-registered type), so an omitted-parameter ADT like `type Inner = | Inner(a)` type-checks correctly instead of misresolving `a` as a bogus concrete type; and a bare reference to a parameterized type missing its arguments (`Outer(Inner)` where `Inner` needs one) now reports `Type 'Inner' expects 1 type argument(s) but got 0.` instead of silently accepting it as zero-argument; a plain whole-module import (`import Ashes.IO`, no `as`) now also exposes its members unqualified (a bare `print` resolves, not just `Ashes.IO.print`/`IO.print`), matching language.md's unqualified-name table — `StitchedModuleScope` gained a `plainWholeModuleImports` field (narrower than `moduleAliases`, since an aliased import also gets a leaf entry there) and the reference rewriter's bare-name path checks it against `coreBuiltinKind` after a real stitched-binding search comes up empty |
| Projects | Typed manifests and lock files; discovery and deterministic source enumeration; recursive path dependency graphs; restored locked packages consumed from the content-addressed cache; root-only local overrides with exact locked namespace/version validation; reachable compilation planning across project, include, and dependency roots, including structured parse diagnostics in stable source/span/emission order, nested inline-module provenance, child-before-parent ordering, collision rejection, export visibility, aliases, selectors, and module/type ambiguity; semantic scope and syntax stitching over dependency-ordered plans; declaration-only entry inference with non-entry bodies ignored; and package-provenance-preserving inference of the stitched program |
| CLI | `add`, `compile` (single linux-x64 file), `fmt`, `init`, `remove`, `restore` (path dependencies only), `run` (single file), `tree`, and `why`, wired into an actual runnable `ashes` executable (`selfhost/packages/cli`). `compile`: reads the input, resolves its `import Ashes.*` header against the shipped `lib/Ashes` texts (probed beside the executable, beside its parent directory, then under the working directory) through `stitchWithShippedModules`, lowers and optimizes the stitched program, generates code with `codegenProgram` and real LLVM, links with `linkLinuxExecutable`, writes the executable (`-o`/`--out`, or the input path without `.ash`), and prints stage 0's `OK Wrote <size> to <output>` confirmation with the `Target:` line and its `0`/`1`/`2` exit codes; the binary needs `libLLVM.so` and its dependencies beside it, like the backend test suite. `run`: compiles to `<temp>/ashes/<stem>`, forwards the arguments after `--`, relays the program's stdout and stderr line by line, and returns the program's own exit code. Both cover only what the self-hosted backend compiles today (see the survey in docs/md/future/SELF_HOSTING.md's CLI checklist). `Package.ash` is now the real entry point (`Ashes.IO.exit(runCli(Ashes.IO.args))`); `Dispatch.ash`'s `runCli` matches the command name case-insensitively and routes to the commands below, falling through to usage on no arguments or an unknown command, and treating a leading `--help`/`-h` as global help regardless of what follows — verified by compiling and running the actual binary end-to-end (`init` → `add` → `tree` → `remove`), not just unit tests. `fmt`: file/directory discovery (recursive `.ash` enumeration under a directory, sorted), preview (stdout) and `-w`/`--write` (rewrite only when content changed) modes, the inline-`module`-block skip carve-out, and stage 0's `0`/`1`/`2` exit-code contract; deliberately narrower than stage 0 for now, with no `.editorconfig` resolution (always the formatter's 4-space/`\n` defaults) and no elapsed-time in the write-mode summary (no monotonic-clock capability is shipped yet). `init`: scaffolds `ashes.json` (name from the target directory's basename) and `src/Main.ash` (only when absent), failing without writing anything if `ashes.json` already exists; verified byte-for-byte identical to stage 0's own output. `why`: resolves the target project's manifest, flattens its dependency graph, and breadth-first searches from the root's own direct dependencies over the lock-recorded edges to report the shortest path to a target namespace or that it isn't a dependency. `tree`: resolves the same root-dependency/lock-edge data as `why` and renders it as a plain-text guide-connected tree (root, then each direct dependency and its lock-recorded transitive dependencies), a shared branch expanded wherever it's reachable and only a same-path cycle cut and marked. `add`: edits the manifest's raw JSON directly (not the typed `ProjectManifest` model) so unknown fields survive, updating an existing dependency entry in place or appending a new one, and re-serializes with a private indented JSON writer matching `System.Text.Json`'s default pretty-printer closely enough for a manifest file. `remove`: shares `add`'s raw-JSON model and indented writer, removing the named package from both `dependencies` and `devDependencies` and omitting either field once it empties out. `restore`: resolves and lists a project's dependencies via the same `resolveProjectDependencyGraph` `tree`/`why` use, honoring root-level `overrides` so a registry-named dependency pointed at a local path resolves normally; a root dependency with a real registry source and no override refuses cleanly rather than claiming a restore it cannot perform (no network access, lock-file writing, or hash verification exists yet). Every other subcommand is unported |
| Backend | `AshesCompiler.Backend.Llvm` binds the LLVM C API subset the backend uses (proven by `selfhost/tests/backend` against real emitted objects and assembly dumps); `AshesCompiler.Backend.IrCodegen` compiles whole real `IrProgram`s — scalars/floats, control flow, closures and whole-program lifted functions under stage 0's calling convention, real `match`/`SwitchTag`, native `musttail` tail calls, ADTs with real RC headers plus a narrow provably-dead drop, strings (literals/print/equality/concat), the shipped-stitched stdlib builtin surface (`Ashes.Text`/`Byte`/`Rune`/`UInt`, the `Ashes.IO` write family, `readLine`, `writeBuffered`/`flush`, `exit`), and the full File/Directory filesystem surface — raw syscalls for `exists`/`writeText`/`replace`/`createAll`, libc dynamic imports for `entries`/`removeTree`, `File.open`/`readChunk`/`readLine`/`close` over scalar-fd handles, and the whole-file read family (`readText`/`readAllBytes`/`mmap`-view/`writeBytes`/`makeExecutable`), plus the full `Ashes.IO.Environment` surface over libc `getenv`/`getcwd`/`readlink` and the full `Ashes.IO.Process` surface over raw `pipe2`/`fork`/`execve`/`wait4`/`kill` syscalls (children inherit the parent environment via the entry-captured `__ashes_envp` module global in the linker's new `.bss` segment); `AshesCompiler.Backend.ElfLinker` links static and eager-dynamic linux-x64 ELF executables in pure Ashes (GOT stubs, hash/dynstr/dynsym/rela, multi-`.rodata*` images, local `.text` calls, the entry trampoline). See `docs/md/future/SELF_HOSTING.md`'s checklist (CG-*/LNK-* items) for the exact current surface and open tails: real arena/RC-drop codegen, resource-ownership cleanup for handles, async/net/process runtimes, optimization levels, and the other three target RIDs are the headline gaps. |
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
/tmp/ashes-selfhost-backend-tests lib/Ashes
```

The single argument is the shipped standard-library root (`lib/Ashes` in a checkout): the suite reads
every `<Module.Path>.ash` there once and resolves each test program's `import Ashes.*` header
against those texts through `AshesCompiler.Semantics.ShippedModuleStitching`, the same
plan-and-stitch path a multi-module project takes.

Unlike every other selfhost test, this one links against `libLLVM` at runtime, so the vendored
`.so` files (`libLLVM.so` and its own dependencies — see `LLVM_DEPENDENCY_PACKAGES` in
`scripts/download-llvm-native.sh`) need to sit next to the compiled binary: it carries a `$ORIGIN`
RUNPATH, but nothing copies the library there automatically (see
`AshesCompiler.Backend.Llvm`'s own header comment for why that is this binary's own
responsibility, not the compiler's).
