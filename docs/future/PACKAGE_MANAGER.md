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

The registry is **a thin, static index** — not a bespoke service. It maps
`name -> { versions, namespace, per-version source URL, published content hash, capability metadata }`.
The index is static files served over HTTPS, so the registry can be hosted on a CDN, GitHub Pages, or
object storage with near-zero infrastructure; the content is immutable and cacheable, which fits the
"boring and deterministic" ethos.

### 7.1 Index-only, with hash-pinned reproducibility

The actual source is pulled from each package's **registered source URL** (typically a GitHub/GitLab
tag) rather than stored by the registry. This is the low-infrastructure start:

```
ashes add json
  -> query index for `json`
  -> versions + for 1.2.3: source URL (github.com/x/json @ v1.2.3) + published hash + namespace
  -> fetch tarball from the source URL
  -> verify against the published hash
  -> cache by hash; lock records { version, url, hash }
```

The registry stores almost no bytes, yet the build is fully reproducible because **the lock always
records the source content hash**. This directly addresses the immutability footgun that index-only
systems (early Go, npm) hit: a force-pushed or deleted tag is *detected* by hash mismatch rather than
silently changing the build.

### 7.2 Uniqueness and publishing

- The registry's uniqueness key is the **namespace** (§2.1), so no two registry packages can claim the
  same public module prefix. `ashes add <name>` resolves the name to its owning namespace.
- `ashes publish` uploads a manifest entry (name, namespace, version, source URL, computed content
  hash, and the capability metadata from §8) to the index. Source stays in the author's repository.
- Authentication and ownership for publishing are the last surface to build; the index format is
  designed so a package's metadata is self-describing and verifiable without trusting the transport.

### 7.3 Proxy / mirror as a later drop-in

The index format is designed so a caching **proxy or mirror** can be introduced later without any
client change — serving immutable copies for availability, offline/air-gapped builds, and independence
from GitHub uptime and rate limits. The client already verifies hashes, so a mirror is purely an
availability optimization, never a trust anchor. This mirrors Go's evolution (index + VCS, then a
caching proxy and checksum database) but bakes the hash pinning in from day one.

---

## 8. Capability audit — the differentiating feature

Every Ashes function carries an inferred `needs {...}` capability row (LANGUAGE_SPEC §20), and the
compiler already computes these when it stitches the world. The package manager can therefore report,
**statically and precisely** — not by heuristic source scanning as npm-era tools do — exactly which
capabilities each dependency's public API introduces:

```
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

```
ashes init            # scaffold ashes.json + src/<Namespace>/... (namespaced with --lib)
ashes add <pkg>       # add + resolve + lock + cache (instant if cached); --path / --git / --dev
ashes remove <pkg>
ashes restore         # materialize the lock; --offline, --frozen
ashes build | run | test   # auto-restore if the lock is stale or missing (unless --frozen/--offline)
```

Later additions: `ashes tree`, `ashes why <pkg>`, `ashes outdated`, `ashes update [<pkg>]`,
`ashes vendor`, `ashes clean`, `ashes capabilities`, `ashes publish`.

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
| Resolver/cache/registry client | `src/Ashes.Cli/` (new) | A CLI-side `PackageResolver` (SemVer + lock), a content-addressed cache, and the registry/index client. Pure .NET: HTTP, hashing, and zip handling are all in the base library. Not in the compiler phases. |
| CLI commands | `src/Ashes.Cli/Program.cs` | `RunAdd`/`RunRemove` write the new manifest shape; new `RunRestore`; `build`/`run`/`test` gain an auto-restore + lock-verify pre-step; retire `install`. |
| Shared consumers | `src/Ashes.Lsp/DocumentService.cs`, `src/Ashes.TestRunner/Runner.cs` | Consume the same resolved-roots view so all front ends agree. |
| Ripple | tests, runner | The new `AshesProject` field touches its manual constructors and `project with { ... }` call sites. |

The principle throughout: the CLI resolves and materializes a deterministic set of roots; the compiler,
LSP, and test runner share that single resolved project view.

---

## 13. Rollout

The registry is a committed phase, not an optional someday. It comes after Phase 2 only because it feeds
the lock/cache pipeline that Phase 2 builds.

### Phase 1 — path dependencies, namespacing, plumbing

- Define the local/path dependency manifest shape and the namespace rule (§2).
- Teach `LoadProject` to resolve direct `path` dependencies to absolute source roots.
- Establish the resolved-roots seam feeding `BuildCompilationPlan`, and the auto-restore hook in
  `build`/`run`/`test`.
- Enforce the namespace discipline (hard ownership; a library exporting a module outside its namespace
  is an error).
- Tests proving modules from a path dependency are importable under its namespace.

Non-goals for Phase 1: no lock file, no cache, no remote fetch, no transitive graph.

### Phase 2 — lock file, cache, git, transitive resolution, workspaces

- Add `ashes.lock` and the content-addressed cache.
- Add git dependencies and full transitive resolution with the Cargo-model resolver (§6).
- Add `ashes tree` / `ashes why`, and workspaces (§10).
- The compiler/LSP/test runner consume the resolved lock, not ad hoc walking.

### Phase 3 — the registry

- Static CDN-hosted index; namespace uniqueness; source pulled from registered URLs with hash-pinned
  reproducibility (§7).
- `ashes publish`, `ashes update`, `ashes outdated`, `ashes vendor --offline` flows.
- The `ashes capabilities` audit graduates to a first-class command, and the capability snapshot is
  written into the lock, as built-in capabilities mature.
- The index format leaves room for a caching proxy/mirror as a later, client-transparent addition.

---

## 14. Decisions locked in

1. **Namespacing:** hard ownership. A library's public modules must live under its declared namespace;
   violations are errors, and the registry reserves on namespace.
2. **Resolution:** Cargo model — SemVer constraints, highest-compatible on first add, pinned in a lock
   file, one version per package, conflicts are errors.
3. **Registry:** a static index that maps names to registered source URLs, with mandatory content-hash
   pinning for reproducibility and room for a later caching mirror.
4. **`ashes install`:** retired; `build`/`run`/`test` auto-restore and `ashes restore` is explicit.
</content>
</invoke>
