# Package Manager

This document is the design of record for package management in Ashes. It supersedes the earlier
"local dependencies first, registry someday" sketch: the registry is a committed phase here, fully
specified, not deferred.

The guiding philosophy is **make the common case trivial while allowing advanced workflows when
needed**. For a small project, package management should be nearly invisible — edit `ashes.json` (or
`ashes add`), then `ashes run`. For a large workspace it should scale without introducing extra tools
or new concepts.

---

## 1. The fact that drives the whole design

Ashes has **no separate compilation and no binary artifacts**. `ProjectSupport.BuildCompilationPlan`
resolves imports across *directories*, and `BuildCompilationLayoutCore` **stitches every module into a
single source string** that is then type-inferred, monomorphized, and lowered as one unit. There is no
ABI, no per-package object file, no linker step between packages.

Three consequences follow, and everything below is downstream of them:

1. **A package is a source tree.** There is nothing to build, cache as a binary, or version by ABI.
   The global cache is content-addressed *source*. "Installing" a cached package means "make its
   source visible to resolution," not "compile it" — which is why a cached `ashes add` is instant
   (the pnpm/uv feel comes almost for free).
2. **Two versions of the same package cannot coexist in one build.** Their modules would stitch into
   the same namespace and collide. Ashes is therefore a **single-version-per-package world** (like Go,
   unlike npm). This is a simplification to lean into: it eliminates diamond duplication and multiple
   instances — the hardest part of npm-style resolution — by construction.
3. **The compiler must never be the dependency solver.** Resolution happens in the CLI at `restore`
   time and produces a deterministic set of source roots. The compiler, LSP, and test runner all
   consume that same resolved view. `ResolveImport` itself never learns about registries, versions, or
   archives.

---

## 2. Namespacing — the central rule

**A library publishes all of its modules under a single top-level namespace directory equal to its
declared namespace.** The module resolver does not change at all: a dependency's source root is added
to the search roots, and non-collision falls out for free.

- Package `json` declares namespace `Json`; its public modules live at `src/Json.ash`,
  `src/Json/Parser.ash`, and so on.
- A consumer writes `import Json.Parser`. The resolver finds exactly one `Json/Parser.ash` across all
  roots, because only the `json` package owns the `Json/` subtree.
- The namespace **defaults from the package name** in PascalCase (`json` -> `Json`,
  `json-parser` -> `JsonParser`) and is overridable via a `namespace` field for names that do not map
  cleanly.
- Applications (a project with a trailing `Main.ash` entry) do not publish, so they keep flat,
  unnamespaced modules. The namespace discipline binds libraries only, and only at publish time.

This is Go's "import path = ownership" model adapted to Ashes module syntax. The entire resolver stays
untouched; dependencies are **just additional search roots**.

### 2.1 Why this is not a new restriction

Collisions inside a single build are only possible if two dependencies both claim the same namespace.
The registry prevents that by construction — see §7.1. Concretely:

- **Publishing a namespace someone else already registered** is blocked at publish, exactly like
  every registry reserves package names. Pick another name.
- **A private, project-local module** with any name is always fine: project-local source roots resolve
  before dependencies and before the shipped standard library, so your own tree owns your own names.
- **Depending on a package *and* hand-writing a local module of the same namespace** is a collision
  *you* created inside *your* project; it produces a clear ambiguity error, and you resolve it (rename
  yours, or drop the dependency).

Because the name-to-namespace mapping is not injective (`json-parser`, `json_parser`, and `JsonParser`
would all map to `JsonParser`), **the registry's uniqueness key is the namespace, not the raw name**.
`ashes add json-parser` resolves through to whoever owns the `JsonParser` namespace. Git and path
dependencies are the only way to manufacture a namespace clash, and there it surfaces as a build-time
error rather than a silent surprise.

---

## 3. Manifest (`ashes.json`)

A string value is a registry SemVer shorthand; an object value carries a path, a git source, or extra
fields.

```json
{
  "name": "my-app",
  "namespace": "MyApp",
  "entry": "src/Main.ash",
  "dependencies": {
    "json":      "^1.2.0",
    "local-lib": { "path": "../local-lib" },
    "parser":    { "git": "https://github.com/user/parser", "rev": "abc123" }
  }
}
```

- **SemVer constraints:** `^1.2` (caret, the default written by `ashes add`), `~1.2`, `=1.2.3`, `*`.
- **`path` dependencies:** no version; resolved live from disk. This is the Phase 1 minimum.
- **`git` dependencies:** pinned by `rev`, `tag`, or `branch`; checked out into the cache.
- **`namespace` (optional):** overrides the PascalCase default. For a library this is the public
  module prefix consumers import under.
- Unknown fields remain ignored for forward compatibility, as they are today.

Distinguish **dev dependencies** (needed only to build/run tests and examples, never part of a
published library's public surface) via a separate `devDependencies` map, added with `ashes add --dev`.

---

## 4. Lock file (`ashes.lock`)

A generated, committed file holding the fully resolved graph, so the CLI, LSP, and test runner all
consume an identical, deterministic root set. Because packages are source, the integrity value is a
**content hash of the source tree** (`ash1:` prefixed), not an archive hash — reproducible regardless
of how the source was fetched.

```json
{
  "version": 1,
  "package": [
    { "name": "json",   "version": "1.2.3", "source": "registry+https://pkg.ashes-lang.org",
      "namespace": "Json", "hash": "ash1:9f2c...", "dependencies": ["utf8"] },
    { "name": "utf8",   "version": "0.4.1", "source": "registry+https://pkg.ashes-lang.org",
      "namespace": "Utf8", "hash": "ash1:..." },
    { "name": "parser", "source": "git+https://github.com/user/parser", "rev": "abc123...",
      "namespace": "Parser", "hash": "ash1:..." }
  ]
}
```

- `ashes restore` writes it; `build`/`run`/`test` consume it and verify hashes.
- `--frozen` (for CI) fails if resolution would change the lock at all.
- The lock is also the natural home for the capability-audit snapshot (§8), so a review can see when a
  dependency update changes the capability surface.

---

## 5. Global content-addressed cache

- **Location:** `$XDG_CACHE_HOME/ashes` (fallback `~/.ashes/cache`), consistent with the
  `~/.local/share/ashes-tools/` convention Ashes already uses for provisioned tool payloads.
- **Layout:** `cache/pkg/<name>/<version>/<hash>/...` for registry dependencies,
  `cache/git/<rev-hash>/...` for git sources, and `cache/index/` for the registry index.
- **Content-addressed by source-tree hash**, so identical package versions are shared across every
  project, deduplicated automatically, and safe under concurrent CI.
- "Installing" a cached package is making its root visible to resolution — no copy, no build. This is
  why `ashes add <cached>` completes instantly.

### 5.1 The `ash1:` content hash

The integrity value is a hash of the **source tree**, not of any particular archive, so it is identical
however the source was fetched (registry tarball, git checkout, vendored copy). It is computed
deterministically over the set of packaged files:

1. Consider every packaged file as a `(path, bytes)` pair, where `path` is the package-root-relative
   path with `/` separators (never a leading `./` or platform backslashes).
2. For each file emit the line `"<sha256-hex-of-bytes>  <path>\n"` (two spaces, LF).
3. Sort the lines by `path` with an ordinal (byte-wise) comparison.
4. Concatenate them, take the SHA-256 of that concatenation, hex-encode it lowercase, and prefix
   `ash1:`. The scheme prefix names the algorithm so a future `ash2:` can coexist.

Directory entries, symlinks, and file modes are deliberately excluded — only file paths and contents
contribute, matching the "a package is source" model (§1). The registry recomputes this hash server-side
at publish and rejects a mismatch against the client's declared value; the client re-verifies every
download against the lock (§4).

---

## 6. Resolution

**Model: SemVer constraints, highest-compatible selection, unified to one version per package, pinned
in the lock file** — the familiar Cargo mental model, minus the hard multi-version part (impossible
here per §1).

The resolver's job for each package: find the single highest version satisfying **all** constraints
across the transitive graph. If none exists, fail with a clear conflict diagnostic naming who requires
what. There is never silent duplication.

### 6.1 Why this model over Go-style MVS

Given `json ^1.2` in the manifest and `json 1.5` later published:

- **This model (Cargo):** the first resolve pins the highest compatible version available at that
  moment into `ashes.lock`; later publishes do not change the build until `ashes update`. Newest
  compatible on first add, then frozen. This matches what users expect from npm/pnpm/Cargo, and the
  lock file is where integrity hashes and the capability snapshot live regardless.
- **MVS (Go):** always selects the *lowest* version satisfying all requirements, so you get `1.5` only
  when a requirement is explicitly bumped. Trivially reproducible and a simpler resolver, but the UX is
  unfamiliar and can silently strand a project on old versions.

MVS remains a viable fallback if the Cargo-style resolver proves troublesome, but the committed design
is the Cargo model.

### 6.2 Single version per package

Because coexisting versions cannot be stitched (§1), a version conflict is an error, not a
duplicate-and-isolate. The diagnostic shows the conflicting constraints and their sources so the user
can widen a bound or update.

---

## 7. The registry (a committed phase)

The registry is **a self-contained, hostable server** — not a third-party index. Ashes defines a
registry API, ships a reference server that implements it, and runs the canonical public instance on
that server. Anyone — a company, a community, or a solo developer — can host their own by deploying the
same server (or by implementing the API). The registry is authoritative for both metadata *and* source,
so there is no dependency on any external host (no GitHub, no VCS-tag pulling) for a package's bytes.

The design is deliberately three separable things, so "host your own" is real:

1. **A registry API specification** — the read/publish/yank contract below. This is the interoperability
   surface; a third-party registry only has to speak it.
2. **A reference server** implementing the spec — what Ashes ships and what self-hosters deploy. It is
   written in **C#/.NET** (matching the compiler codebase and CI) and targets a **minimal, self-hostable
   first cut**: a single deployable with filesystem-backed storage and API-token auth, with object
   storage / a database as a later scale option.
3. **The canonical public instance**, operated by the Ashes org on the reference server.

### 7.1 What the registry stores and serves (read API)

The read API is unauthenticated, cacheable, and CDN-frontable (full sketch in
[REGISTRY_API.md](REGISTRY_API.md)):

- **Resolve:** `GET /api/v1/packages/<namespace>` returns the version list and per-version metadata
  (content hash, dependencies, capability metadata from §8).
- **Download:** `GET /api/v1/packages/<namespace>/<version>/source` returns the content-addressed source
  tarball.
- **List / search:** `GET /api/v1/packages` (browse) and `GET /api/v1/search?q=` (ranked) power the
  client's discovery flow — `ashes search <query>` (§9).

The registry stores each published version's source immutably (content-addressed), so a version's bytes
never change or disappear once published. A client resolves against a registry, downloads the source,
verifies it against the hash, and caches it (§5). The lock records `source: "registry+<base-url>"` plus
the version and hash, so a build is reproducible and pinned to a specific registry.

### 7.2 Namespace ownership and publishing (publish API)

Publishing is authenticated and governed by the registry itself — the accounts/token machinery lives in
the server, which is the price of independence from any external host:

- **Accounts and tokens.** `ashes login` obtains and stores an API token (in `~/.ashes/credentials`);
  publishing is authorized by that token. The minimal server keeps this small (token-based accounts, no
  web UI required to start).
- **Namespace ownership.** The registry's uniqueness key is the **namespace** (§2.1). The first publish
  of a namespace **claims it for the publishing account**; owners may add co-owners; only owners may
  publish new versions of that namespace.
- **`ashes publish`** uploads the version's source tarball plus its manifest metadata to the registry.
  The **server** then validates and stores it — validation is server-side, not client-trusted:
  - **Append-only / immutable.** A version may be added but never overwritten or silently changed.
  - **Namespace lint (§2).** Every exported module must live under the package's namespace.
  - **Hash computation.** The server computes and records the content hash the client will verify.
  - **Capability extraction (§8).** The server records the public API's capability rows.
- **`ashes yank`** marks a published version unusable for *new* resolutions without deleting it, so
  existing locks that pin it still resolve — reproducibility is never broken by a yank.

Because ownership and immutability are enforced by the server, the "open to contribute, closed to
overwrite" property holds without any external permission model: an account can publish only namespaces
it owns, and no account can rewrite a published version.

**Publish limits and quotas.** The server also enforces size and abuse limits at publish (they gate what
gets in; they never affect an already-published, immutable version, so reproducibility is untouched).
The set to enforce, with defaults as starting points for the registry-API spec:

- **Max per-file size** — source is text, so a small cap (~1 MiB) is generous and blocks binary-blob
  smuggling.
- **Max total package size (uncompressed)** — the headline limit; ~10 MiB (crates.io's default, our
  closest source-only analog), adjustable per package.
- **Max file count** — ~10,000, to stop pathological many-tiny-files trees.
- **Decompressed-size ceiling enforced during unpack** — the critical zip-bomb defense: cap the
  *decompressed* content, not just the uploaded bytes, so a tiny upload cannot expand to gigabytes.
- **Publish rate / storage quota** — per-account rate limits and an optional per-namespace quota.
- **Content allowlist** — keep packages source-only: `.ash` sources plus a few metadata files
  (`ashes.json`, `README`, `LICENSE`); reject or tightly cap anything else.

These are **per-registry policy** behind the storage/policy layer (§12.1): the public instance ships the
defaults, and a self-hosted or corporate registry may set its own (larger packages, internal-only
namespaces, and so on).

### 7.3 Reproducibility and trust

Trust is scoped to whichever registry a project configures. Published versions are immutable, the lock
pins a content hash, and the client verifies every download against it — so even a compromised transport
or a buggy mirror cannot alter a build silently. A self-hosted registry is trusted by exactly the people
who point their config at it; the public instance is trusted by those who use the default.

### 7.4 Multiple and custom registries

Registry pluralism is first-class, mirroring Cargo `[registries]`, Go's `GOPROXY`, and npm scoped
registries:

- A **`registries`** map (global in `~/.ashes/config` and/or per-project in `ashes.json`) binds names to
  base URLs, with an overridable **`default`** pointing at the public instance:
  ```json
  "registries": { "default": "https://pkg.ashes-lang.org", "acme": "https://pkg.acme.internal" }
  ```
- A dependency selects a registry, defaulting to `default`:
  ```json
  "dependencies": { "widgets": { "version": "^1.2", "registry": "acme" } }
  ```
- The lock's `source: "registry+<base-url>"` field already records which registry each package came
  from, so private and public dependencies coexist and stay reproducible.
- **Namespace uniqueness is per registry.** Two registries may each carry a `Json`; a project that pulls
  both must resolve the cross-registry collision (the same class as a git/path override), which is why
  naming a non-default registry on a dependency is an explicit statement of intent.

This is the direct answer to host independence: a project can point at the public instance, a mirror of
it, a corporate registry, or one you build yourself, without any change to the client.

### 7.5 Mirroring and scale

- **Mirroring.** A registry can run as a **pull-through cache** of another (fetch-on-miss, then serve
  locally), giving availability and offline/air-gapped resilience. Because the client verifies hashes, a
  mirror is an availability optimization, never a trust anchor.
- **Scale.** The minimal server uses filesystem storage; the same server swaps in object storage and a
  database for larger instances, and a CDN fronts the read API. None of this changes the client or the
  API — it is deployment configuration of the reference server.

---

## 8. Capability audit — the differentiating feature

Every Ashes function carries an inferred `needs {...}` capability row (LANGUAGE_SPEC §20), and the
compiler already computes these when it stitches the world. The package manager can therefore report,
**statically and precisely** — not by heuristic source scanning as npm-era tools do — exactly which
capabilities each dependency's public API introduces:

```text
$ ashes capabilities
my-app
|- json     pure
|- http     needs { Network, Tls }
|- logger   needs { Clock, File }

Newly introduced by this dependency set: Network, Tls, File, Clock
```

Because this is the compiler's own capability inference, it cannot be evaded by obfuscation: a package
that touches the network has `Network` in its row or it does not compile. `ashes add` can prompt "this
adds the Network and Tls capabilities to your project — continue?", turning supply-chain auditing into
a language feature. The capability snapshot is recorded in the lock (§4) so a diff shows when an update
changes the capability surface.

This feature matures alongside the built-in capabilities (IO-as-capability is on the roadmap), but the
mechanism — inferred `needs` rows on exported functions — is real today.

---

## 9. Command surface

```sh
ashes init            # scaffold ashes.json + src/<Namespace>/... (namespaced with --lib)
ashes add <pkg>       # add + resolve + lock + cache (instant if cached); --path / --git / --dev
ashes remove <pkg>
ashes restore         # materialize the lock; --offline, --frozen
ashes build | run | test   # auto-restore if the lock is stale or missing (unless --frozen/--offline)
```

Later additions: `ashes tree`, `ashes why <pkg>`, `ashes outdated`, `ashes update [<pkg>]`,
`ashes vendor`, `ashes clean`, `ashes capabilities`, the discovery commands
`ashes search <query>` / `ashes info <pkg>` (backed by the registry list/search API,
[REGISTRY_API.md](REGISTRY_API.md) §9), and the registry-facing
`ashes login` / `ashes publish` / `ashes yank` (§7.2).

The common path requires **zero explicit package commands**: edit `ashes.json` (or run `ashes add`),
then `ashes run` — restore happens implicitly when the lock is stale or a cached root is missing.

`ashes install` is retired. Its former behavior (listing dependencies) is subsumed by `restore` and
`tree`; there is no `install` alias.

---

## 10. Workspaces

A root `ashes.json` with a `workspace.members` glob defines a workspace with **one `ashes.lock`, one
resolved graph, one shared cache, and one output directory**. Members path-reference one another by
namespace with no intermediate publish. Workspace loading produces the union of member source roots
feeding a single `BuildCompilationPlan` per member entry — a small extension of `LoadProject`.

---

## 11. Offline and vendoring

- `ashes restore --offline` / `ashes build --offline` succeed only if everything required is already
  cached; otherwise they fail with a precise "missing `<name>@<version>`, not in cache" message.
- `ashes vendor` copies the resolved source trees into `vendor/` and rewrites the effective roots to
  point there, for hermetic, air-gapped, or archival builds. This is trivial precisely because packages
  are source.

---

## 12. How it lands in the code

The integration is small and localized, centered where the resolver already lives.

| Area | File | Change |
|------|------|--------|
| Project model | `src/Ashes.Semantics/ProjectSupport.cs` | Add dependency data and a resolved `Root`/`Namespace`/`Hash` list to `AshesProject`; `LoadProject` parses dependencies. Resolved dependency roots are appended to the effective search roots **before** `BuildCompilationPlan` runs. `ResolveImport` is unchanged. |
| Resolver/cache/registry client | `src/Ashes.Cli/` (new) | A CLI-side `PackageResolver` (SemVer + lock), a content-addressed cache, and the registry HTTP client (resolve/download/login/publish). Pure .NET: HTTP, hashing, and zip handling are all in the base library. Not in the compiler phases. |
| CLI commands | `src/Ashes.Cli/Program.cs` | `RunAdd`/`RunRemove` write the new manifest shape; new `RunRestore`; `build`/`run`/`test` gain an auto-restore + lock-verify pre-step; new `login`/`publish`/`yank`; retire `install`. |
| Shared consumers | `src/Ashes.Lsp/DocumentService.cs`, `src/Ashes.TestRunner/Runner.cs` | Consume the same resolved-roots view so all front ends agree. |
| Registry server (Phase 3) | `src/Ashes.Registry/`, `src/Ashes.Registry.Tests/` (new) | The reference registry app + its tests, added to `Ashes.slnx` and the `just` CI (§12.1, [REGISTRY_API.md](REGISTRY_API.md)). A downstream consumer of `Ashes.Frontend`/`Ashes.Semantics`; never referenced back by any compiler phase. |
| Ripple | tests, runner | The new `AshesProject` field touches its manual constructors and `project with { ... }` call sites. |

The principle throughout: the CLI resolves and materializes a deterministic set of roots; the compiler,
LSP, and test runner share that single resolved project view.

### 12.1 The registry server is a downstream consumer, not a compiler phase

The registry **client** lives in the compiler tree (`src/Ashes.Cli/`). The registry **server** (§7)
lives alongside it as **`src/Ashes.Registry`** (the minimal-API app) with **`src/Ashes.Registry.Tests`**
for its tests, both added to `Ashes.slnx`. Keeping it in the solution is deliberate: the existing
`just` CI already builds `Ashes.slnx` and runs the test projects, so the server is compiled,
format-checked, and its tests executed by the same pipeline with no separate build lifecycle to
maintain (§13 Phase 3 wires the one extra `test`-job line).

The one rule that must not bend is the **direction of the dependency DAG**. `Ashes.Registry` is a
*downstream consumer* of the compiler front end (`Ashes.Frontend`/`Ashes.Semantics`) for publish-time
validation (§8, [REGISTRY_API.md](REGISTRY_API.md) §6) — exactly as `Ashes.Lsp` consumes compiler logic
rather than reimplementing it. Nothing in the compiler phases may depend on `Ashes.Registry`: it is a
leaf of the graph, never referenced by Frontend, Semantics, Backend, or the CLI. It has nothing to do
with lexing, inference, or codegen and must not entangle them; it only *reads* them as a library.

Being in the solution is a build/test convenience, not a shipping coupling: the registry server has its
own **deploy/release lifecycle** and is **not** part of the compiler's published artifacts (the
CLI/LSP/DAP self-contained binaries), so its ASP.NET Core and EF Core dependencies never reach a
compiler release.

Shape of the reference server:

- A **.NET 10 minimal-API** service exposing the read/publish/yank endpoints of §7.
- A **pluggable persistent-storage abstraction** — narrow interfaces (packages, versions, ownership,
  tokens, blobs) with a **filesystem + embedded-SQLite implementation** for the minimal self-hostable
  cut, and room to swap in an object-store/database implementation for scale without touching the API or
  the client. The interfaces live in `Ashes.Registry`; if the storage layer grows it can be split into
  its own `src/Ashes.Registry.Storage` project without any API or client change.
- The same solution as the compiler for build/test/format, but an independent deploy lifecycle, and a
  strict one-way dependency on the front end (never the reverse).

---

## 13. Rollout

The registry is a committed phase, not an optional someday. It comes after Phase 2 only because it feeds
the lock/cache pipeline that Phase 2 builds.

Each phase decomposes into ordered, roughly PR-sized steps that are independently shippable and testable.
Every phase leads with a **spec-first** step: per the repository rules, the relevant docs
(`PROJECT_SPEC.md`, `COMPILER_CLI_SPEC.md`, `DIAGNOSTICS.md`, and for Phase 3 a new registry-API doc) are
updated before the behavior, and diagnostic codes are allocated up front. Each step below names its
acceptance signal.

### Phase 1 — path dependencies, namespacing, plumbing

Goal: one Ashes project can depend on another on disk, imported under its namespace. No lock, cache,
remote fetch, or transitive graph.

1. **Spec + diagnostics.** Update `PROJECT_SPEC.md` (dependency object shape incl. `path`, and the
   `namespace` field/§2 rule), `COMPILER_CLI_SPEC.md` (new `add`/`remove` shape, `restore`, retire
   `install`), and allocate `DIAGNOSTICS.md` codes for: namespace-lint violation, dependency-not-found,
   dependency-is-not-a-project, and cross-dependency namespace collision. *Acceptance: docs merged, codes
   reserved.*
2. **Manifest model.** Extend `AshesProject` with dependency data and a resolved
   `{ Name, Namespace, Root }` list; teach `LoadProject` to parse `dependencies` (string vs object form)
   and resolve `path` entries to absolute source roots. Update the manual `AshesProject` constructors in
   tests/runner. *Acceptance: `LoadProject` returns resolved path-dependency roots.*
3. **Resolved-roots seam.** Append resolved dependency roots to the effective search roots before
   `BuildCompilationPlan`; `ResolveImport` unchanged. *Acceptance: a program importing `Dep.Module` from a
   path dependency compiles and runs.*
4. **Namespace discipline.** Enforce that a dependency exports modules only under its declared namespace,
   and detect cross-dependency namespace collisions; emit the allocated diagnostics. *Acceptance: a
   dependency exporting outside its namespace fails with the code.*
5. **CLI.** `RunAdd`/`RunRemove` write the object shape (`--path`, `--dev`); retire `install`; add
   `restore` (Phase 1 = validate + materialize path dependencies, no cache); wire the auto-restore hook in
   `build`/`run`/`test` (a validation pass for path deps). Update `PackageManagementCliTests`. *Acceptance:
   the CLI round-trips a path dependency and `run` auto-validates it.*
6. **Shared consumers.** `Ashes.Lsp/DocumentService.cs` and `Ashes.TestRunner/Runner.cs` consume the same
   resolved-roots view. *Acceptance: LSP go-to-definition resolves into a path dependency; project-mode
   tests import one.*
7. **End-to-end tests.** A multi-package `.ash` fixture covering import-from-path-dependency and the
   namespace-violation error. *Acceptance: fixtures pass in `ashes test`.*

### Phase 2 — lock file, cache, git, transitive resolution, workspaces

Goal: reproducible, transitive, cached resolution; git dependencies; workspaces.

1. **Spec + diagnostics.** Update `PROJECT_SPEC.md` (`ashes.lock` format, `git` dependency shape,
   `workspace.members`) and `COMPILER_CLI_SPEC.md` (`restore --frozen/--offline`, `tree`, `why`, `update`);
   allocate codes for version conflict, stale lock, hash mismatch, and dependency-graph cycle. *Acceptance:
   docs merged, codes reserved.*
2. **Content-addressed cache.** Cache layout under `$XDG_CACHE_HOME/ashes`, source-tree hashing (`ash1:`),
   and read/write. *Acceptance: a resolved source tree round-trips through the cache by hash.*
3. **Resolver.** Build the dependency graph and the Cargo-model selector (§6): SemVer parse, highest-
   compatible selection, single version per package, conflict as a typed error. A pure function producing a
   resolved set. *Acceptance: unit tests for selection and conflict.*
4. **Lock file.** Write/read `ashes.lock`; `restore` materializes from it and verifies hashes; `--frozen`
   fails on drift. *Acceptance: restore is idempotent and hash-verified; `--frozen` catches a changed
   manifest.*
5. **Git dependencies.** Fetch by `rev`/`tag`/`branch` into the cache, pinned by hash. *Acceptance: a git
   dependency resolves and locks reproducibly.*
6. **Auto-restore.** `build`/`run`/`test` detect a stale or missing lock and restore (unless
   `--frozen`/`--offline`). *Acceptance: editing `ashes.json` then `ashes run` restores automatically.*
7. **Inspection.** `ashes tree` and `ashes why <pkg>`. *Acceptance: both render the resolved graph.*
8. **Workspaces.** Parse `workspace.members`; one lock/graph/cache/out-dir; union member roots via a
   `LoadProject` extension. *Acceptance: members import each other with no intermediate publish.*

### Phase 3 — the registry

Goal: a self-hostable registry server (§7, §12.1) and the client integration that uses it.

1. **Spec + diagnostics.** [REGISTRY_API.md](REGISTRY_API.md) sketches the API/server; finalize it
   (read/list/search/publish/yank endpoints, auth, storage contract) and update `COMPILER_CLI_SPEC.md`
   (`login`, `publish`, `yank`, `search`, `info`, the `registries` config, per-dependency `registry`);
   allocate codes for auth failure, namespace-owned-by-another-account, immutable-version-overwrite, and
   yanked-version. *Acceptance: docs merged, codes reserved.*
2. **Server scaffolding + CI.** New `src/Ashes.Registry` .NET 10 minimal-API app and
   `src/Ashes.Registry.Tests`, both added to `Ashes.slnx`; the storage interfaces plus the
   filesystem/SQLite implementation; the read endpoints (resolve, download, list, search). Tests in
   TUnit + Shouldly + Imposter (REGISTRY_API §8), and the `just` CI `test` job gains a line running
   `Ashes.Registry.Tests` alongside `Ashes.Tests`/`Ashes.Lsp.Tests` (`ci/jobs.sh`). *Acceptance: a
   locally-run server resolves, serves, and searches a hand-seeded package; `just test` runs the
   registry tests.*
3. **Server publish + auth.** Accounts and API tokens; namespace ownership with first-claim and co-owners;
   the publish pipeline (limits, append-only, namespace lint, hash computation, capability extraction);
   `yank`. *Acceptance: an authorized publish stores an immutable version; an unauthorized or overwriting
   publish is rejected.*
4. **Client integration.** Registry HTTP client; the `registries` config and per-dependency `registry`
   selection; the lock's `registry+<url>` source; download-and-verify against the cache. *Acceptance: a
   project resolves and builds a package from a running registry.*
5. **Client verbs.** `ashes login`, `ashes publish`, `ashes yank`, and the discovery commands
   `ashes search` / `ashes info` (REGISTRY_API §9). *Acceptance: a full search → add → publish → resolve →
   build loop against a local server.*
6. **Capabilities.** `ashes capabilities` as a first-class command; write the capability snapshot into the
   lock. *Acceptance: the command reports each dependency's introduced capability row.*
7. **Operate + document.** Stand up the canonical public instance and document self-hosting.

Deferred, client-transparent additions to the same server: mirroring (pull-through cache) and object-store
/ database storage for scale (§7.5).

---

## 14. Decisions locked in

1. **Namespacing:** hard ownership. A library's public modules must live under its declared namespace;
   violations are errors, and the registry reserves on namespace.
2. **Resolution:** Cargo model — SemVer constraints, highest-compatible on first add, pinned in a lock
   file, one version per package, conflicts are errors.
3. **Registry:** a self-contained, hostable registry server that is authoritative for source and
   metadata, with immutable versions, per-namespace account ownership, API-token auth, and hash-pinned
   downloads. The reference server is a .NET 10 minimal-API app with pluggable storage (filesystem +
   SQLite first), living in the compiler solution as `src/Ashes.Registry` (+ `src/Ashes.Registry.Tests`)
   so the same `just` CI builds, format-checks, and tests it — a strict downstream consumer of the
   compiler front end, never referenced back by any compiler phase, and with its own deploy lifecycle
   outside the compiler's published artifacts. The Ashes org runs the canonical public instance and
   anyone can self-host. Multiple/custom registries are first-class (`registries` config +
   per-dependency `registry`).
4. **`ashes install`:** retired; `build`/`run`/`test` auto-restore and `ashes restore` is explicit.
