# Package Manager

This document describes a practical implementation path for package
management in Ashes.

The key recommendation is:

1. **Step 1:** local/path dependencies only
2. **Step 2:** lock file + transitive dependency resolution
3. **Step 3:** registry lookup/fetch/publish

A registry is a large piece of product and infrastructure work. The current
compiler codebase is much better aligned with **local dependency wiring
first**, then graph resolution, and only then a registry.

------------------------------------------------------------------------

## Current State in the Repository

Ashes already has the beginnings of a package-management surface, but only
at the manifest/CLI layer.

### What exists today

- `docs/PROJECT_SPEC.md` defines an optional `dependencies` field in
  `ashes.json`.
- `docs/COMPILER_CLI_SPEC.md` defines `ashes add`, `ashes remove`, and
  `ashes install`.
- `src/Ashes.Cli/Program.cs` implements those commands by reading and
  rewriting `ashes.json`.
- `src/Ashes.Tests/PackageManagementCliTests.cs` tests the current CLI
  behavior.

### What does not exist yet

- No dependency information is loaded into `AshesProject`.
- No dependency graph is built during project loading.
- No package contents are fetched, copied, cached, or materialized.
- No registry client exists.
- No lock file exists.
- Import resolution knows only:
  - project `sourceRoots`
  - project `include`
  - shipped compiler `lib/`

That last point is the most important implementation insight: package
support does **not** need a brand-new import system first. The existing
module resolver already works over **directories**. The easiest package
manager implementation is therefore to turn dependencies into additional
resolved source roots.

------------------------------------------------------------------------

## Relevant Code Paths

| Area | File | Why it matters |
|------|------|----------------|
| Manifest/CLI surface | `src/Ashes.Cli/Program.cs` | `RunAdd`, `RunRemove`, and `RunInstall` currently only edit/list JSON. |
| Project model | `src/Ashes.Semantics/ProjectSupport.cs` | `AshesProject` and `LoadProject()` currently ignore dependencies entirely. |
| Module resolution | `src/Ashes.Semantics/ProjectSupport.cs` | `BuildCompilationPlan()` resolves imports from `SourceRoots.Concat(Include)` plus shipped `lib/`. |
| LSP project analysis | `src/Ashes.Lsp/DocumentService.cs` | Reuses `LoadProject()` and `BuildCompilationPlan()`, so package support must flow through the shared project model. |
| Test runner | `src/Ashes.TestRunner/Runner.cs` | Reconstructs `AshesProject` instances for project-mode tests. |
| Compiler tests | `src/Ashes.Tests/ImportTests.cs` and `src/Ashes.Tests/ProjectSupportTests.cs` | These will need updates if the project model grows new dependency fields or effective include roots. |

### Key implementation observation

`ProjectSupport.BuildCompilationPlan()` already has the right shape for
package consumption:

- it computes `searchRoots = project.SourceRoots.Concat(project.Include)`
- it resolves module imports by searching those roots
- it already handles ambiguity and missing-module diagnostics

That means the package manager should preferably feed **resolved package
roots** into project loading, instead of teaching `ResolveImport()` about
registries, versions, or archives directly.

------------------------------------------------------------------------

## Recommended Rollout

### Step 1 — Local/Path Dependencies Only

This should be the first real implementation step.

### Goal

Allow one Ashes project to depend on another Ashes project that already
exists on disk.

### Explicit non-goals for step 1

- no registry
- no version solving
- no remote fetch
- no transitive resolution
- no publishing flow

### Why this fits the current codebase

The compiler already knows how to compile multiple source roots. A local
dependency can be implemented as:

1. read the dependency manifest
2. resolve its source roots to absolute directories
3. append those directories to the consuming project's effective include
   roots

This keeps the existing import resolver intact.

### Recommended implementation shape

1. **Spec update first**
   - Update `docs/PROJECT_SPEC.md` and `docs/COMPILER_CLI_SPEC.md`.
   - Define a local-dependency representation that includes a filesystem
     path.
   - Do not force step 1 through registry-style version strings only; the
     current `"name" -> "version"` shape is not enough to locate a local
     package on disk.

2. **Project model**
   - Extend `AshesProject` in `src/Ashes.Semantics/ProjectSupport.cs`
     with dependency data or with precomputed effective package roots.
   - Update `LoadProject()` to parse dependency entries and resolve them
     relative to the project directory.

3. **Resolution**
   - Keep `BuildCompilationPlan()` mostly unchanged.
   - Feed resolved dependency source roots into `Include` or a new
     equivalent “effective include roots” field before resolution begins.

4. **Validation**
   - Require local dependencies to point to valid Ashes projects with
     `ashes.json`.
   - Require direct dependencies only in step 1.
   - Fail if a dependency itself declares dependencies.
   - Reuse existing ambiguity diagnostics if two packages export the same
     module path.

5. **CLI**
   - Update `RunAdd` / `RunRemove` / `RunInstall` in
     `src/Ashes.Cli/Program.cs` so the CLI
     edits and displays the new dependency shape instead of a bare `"*"`.
   - `install` in step 1 should validate and materialize local dependency
     metadata, not talk to a registry.

### Most likely files to change in step 1

- `docs/PROJECT_SPEC.md`
- `docs/COMPILER_CLI_SPEC.md`
- `src/Ashes.Cli/Program.cs`
- `src/Ashes.Semantics/ProjectSupport.cs`
- `src/Ashes.Lsp/DocumentService.cs`
- `src/Ashes.TestRunner/Runner.cs`
- `src/Ashes.Tests/PackageManagementCliTests.cs`
- `src/Ashes.Tests/ProjectSupportTests.cs`

------------------------------------------------------------------------

### Step 2 — Lock File and Transitive Resolution

This is where the dependency graph should become real.

### Goal

Introduce reproducible resolution for:

- transitive dependencies
- deterministic builds
- conflict detection
- future registry compatibility

### Why this should come before a registry

The hard compiler problem is not “download bytes from somewhere”; it is:

- building a full dependency graph
- deciding which versions/instances are present
- materializing a deterministic set of source roots for the compiler,
  LSP, and test runner

A lock file solves that problem in a registry-independent way.

### Recommended implementation shape

1. Keep `ashes.json` as the **user-authored direct dependency file**.
2. Add a lock file as the **fully resolved graph**.
3. Make project loading consume the lock file to derive the effective
   dependency roots used by `BuildCompilationPlan()`.
4. Allow transitive dependencies only after this layer exists.

### Important constraint

Do not bake transitive graph walking directly into ad hoc compile-time
search logic. The compiler should receive a resolved, deterministic set of
roots; it should not become the dependency solver.

------------------------------------------------------------------------

### Step 3 — Registry

Only after steps 1 and 2 should Ashes add a real registry.

### Scope of registry work

- package naming rules
- version constraints and solver behavior
- remote fetch/download
- cache layout
- integrity checking
- registry protocol/API
- authentication/publishing
- offline behavior
- error UX

This is substantially larger than the earlier steps and is only loosely
related to the compiler’s existing module-resolution logic.

### Why step 3 is the right place

Once local dependencies and lockfile-based graph resolution already exist:

- the project model is defined
- the compiler/LSP/test runner already consume resolved package roots
- the registry only needs to feed the resolver/lockfile pipeline

In other words, by step 3 the registry becomes an **input source** to an
existing package system, rather than the thing that defines the package
system.

------------------------------------------------------------------------

## Implementation Advice

### 1. Keep package management outside the parser and language semantics

This feature belongs to project loading and CLI orchestration, not to the
Ashes language grammar or expression semantics.

The most important implementation center is:

- `src/Ashes.Semantics/ProjectSupport.cs`

not the frontend parser.

### 2. Prefer “resolved roots” over “special package imports”

Ashes imports are module-based (`import Foo.Bar`), not package-qualified.
That is an advantage here. If a dependency exposes `Foo/Bar.ash`, the
compiler only needs that directory in its search roots.

### 3. Make shared consumers use the same resolved project view

Do not let CLI compile, LSP analysis, and test execution each resolve
dependencies differently.

The shared project-loading path should remain the source of truth for:

- CLI compile/run
- LSP document analysis/definition lookup
- test runner project mode

### 4. Expect `AshesProject` shape changes to ripple

`AshesProject` is manually constructed in multiple places, including tests
and runner code. Any new dependency-related field will require updates in:

- `src/Ashes.TestRunner/Runner.cs`
- `src/Ashes.Tests/ImportTests.cs`
- project/LSP call sites using `project with { ... }`

### 5. Let ambiguity diagnostics do real work

If two dependencies contain the same module path, the current resolver
already has a useful ambiguity model. Lean on that instead of inventing a
package-prefixed import syntax too early.

------------------------------------------------------------------------

## Recommended First Milestone

If the project wants the smallest meaningful package-manager milestone, it
should be:

1. define local/path dependency manifest syntax
2. teach `LoadProject()` to resolve direct local dependencies
3. add their source roots to the effective project search roots
4. update `ashes install` to validate/report those dependencies
5. add tests proving local modules from dependencies are importable

That would deliver real value without committing the project to registry
design too early.
