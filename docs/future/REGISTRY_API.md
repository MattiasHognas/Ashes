# Registry API and Server

This sketches the Ashes registry: the HTTP API, the server's inner workings, its pluggable storage, and
the testing approach. It is the reference for Phase 3 of [PACKAGE_MANAGER.md](PACKAGE_MANAGER.md) — read
that first for the *why* (source-only packages, single-version world, namespace ownership, hash-pinned
reproducibility). This doc is the *how*.

Scope is the **minimal, self-hostable first cut**: a single deployable that resolves, downloads, lists,
searches, and accepts publishes, backed by filesystem storage. Web UI, object-store/database storage,
and mirroring are later, client-transparent additions (PACKAGE_MANAGER §7.5).

---

## 1. Shape and conventions

- **A .NET 10 minimal-API application**, in its own top-level `registry/` directory — a standalone app,
  **not** part of the compiler's `src/` dependency DAG (PACKAGE_MANAGER §12.1). Its only contracts with
  the rest of Ashes are the HTTP API (below) and a **library reuse** of the compiler front end for
  publish-time validation (§6).
- **Versioned base path:** `/api/v1`. Unknown fields in requests are ignored; new fields are additive.
- **Content types:** JSON (`application/json`) for metadata; the source blob is an opaque
  `application/gzip` tarball.
- **Auth:** a bearer token in `Authorization: Bearer <token>` on write endpoints; read endpoints are
  unauthenticated and cacheable.
- **Errors:** a uniform body so the CLI can map to diagnostics:
  ```json
  { "error": { "code": "namespace_owned_by_another", "message": "Namespace 'Json' is owned by another account." } }
  ```
  Codes align with the Phase 3 diagnostics: `unauthorized`, `namespace_owned_by_another`,
  `version_exists` (immutable overwrite), `version_yanked`, `limit_exceeded`, `namespace_lint`,
  `not_found`.
- **Reproducibility:** every version response carries the `ash1:` content hash; the client verifies each
  download against it regardless of transport or mirror.

---

## 2. Data model

```
Account   { id, name, createdAt }
Token     { id, accountId, hashedSecret, createdAt, lastUsedAt }      // the secret is shown once
Package   { namespace, description, keywords[], ownerAccountIds[], createdAt, updatedAt }
Version   { namespace, version, hash, dependencies[], capabilities[], yanked, size, publishedAt }
Blob      { hash -> compressed source bytes }                         // content-addressed
```

`Package` is the searchable/browsable unit (one per namespace); `Version` is immutable once written;
`Blob` is the content-addressed source, shared across any versions with identical trees (§5).

---

## 3. Endpoints

### 3.1 Read (unauthenticated, cacheable)

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/index` | Registry info + effective limits (so the client can discover caps) |
| `GET` | `/api/v1/packages` | Browse: paginated package list (`?limit=&cursor=&sort=recent\|name\|downloads`) |
| `GET` | `/api/v1/search?q=` | Search packages (`&limit=&cursor=`), ranked (§7) |
| `GET` | `/api/v1/packages/{namespace}` | Package metadata + full version list |
| `GET` | `/api/v1/packages/{namespace}/{version}` | One version's metadata (hash, deps, capabilities, yanked) |
| `GET` | `/api/v1/packages/{namespace}/{version}/source` | Download the source tarball (may 302 to a blob/CDN URL) |
| `GET` | `/healthz` | Liveness |

`GET /api/v1/packages/{namespace}` response:

```json
{
  "namespace": "Json",
  "description": "A JSON parser and encoder.",
  "keywords": ["json", "parsing"],
  "owners": ["alice"],
  "versions": [
    { "version": "1.2.0", "hash": "ash1:9f2c...", "yanked": false,
      "dependencies": [{ "namespace": "Utf8", "req": "^0.4" }],
      "capabilities": [], "size": 20481, "publishedAt": "2026-07-04T10:00:00Z" }
  ]
}
```

`GET /api/v1/search?q=json` response:

```json
{
  "results": [
    { "namespace": "Json", "description": "A JSON parser and encoder.",
      "latest": "1.2.0", "downloads": 1234, "score": 0.98 }
  ],
  "nextCursor": null
}
```

### 3.2 Write (bearer token)

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/tokens` | Mint an API token (credentialed); MVP may also provision tokens via a server CLI |
| `PUT` | `/api/v1/packages/{namespace}/{version}` | Publish a version (multipart: `metadata` JSON + `source` tarball) |
| `POST` | `/api/v1/packages/{namespace}/{version}/yank` | Mark a version un-resolvable for new builds |
| `POST` | `/api/v1/packages/{namespace}/{version}/unyank` | Reverse a yank |
| `GET/POST/DELETE` | `/api/v1/packages/{namespace}/owners` | List / add / remove co-owners (owner-only) |

Publish is idempotent-by-immutability: re-`PUT`ting an existing `{namespace}/{version}` with the *same*
hash is a no-op success; a *different* hash is `version_exists` (never an overwrite).

---

## 4. The publish pipeline

`PUT /packages/{namespace}/{version}` runs an ordered pipeline; any stage's failure aborts with a typed
error and writes nothing:

1. **Authenticate.** Resolve the bearer token to an account, or `unauthorized`.
2. **Parse upload.** Read the declared metadata (name, namespace, version, deps, declared hash) and the
   source tarball.
3. **Enforce limits.** Per-file size, total uncompressed size, file count, the **decompressed-size
   ceiling during unpack** (zip-bomb defense), and the source-only content allowlist
   (PACKAGE_MANAGER §7.2). Exceeding any is `limit_exceeded`.
4. **Authorize namespace.** First publish of a namespace **claims** it for the account; subsequent
   versions require the account to be an owner, else `namespace_owned_by_another`.
5. **Validate immutability + semver.** The version must be a valid SemVer and must not already exist with
   a different hash (`version_exists`).
6. **Validate source (compiler reuse, §6).** Namespace lint — every exported module lives under
   `{namespace}` — else `namespace_lint`.
7. **Compute + verify hash.** Compute the canonical `ash1:` content hash server-side; it must match the
   client's declared hash (defends against a lying or buggy client).
8. **Extract capabilities (§6).** Record the public API's inferred `needs {...}` rows.
9. **Store atomically.** Write the blob (content-addressed), then the `Version`, then update the
   `Package` and the search index — ordered so a crash never leaves a version without its blob.
10. **Respond** `201` with the stored version metadata.

Stages 3–8 are pure validation over the upload and are the natural unit-test seam (§8).

---

## 5. Storage abstraction

Storage is behind narrow interfaces so the filesystem MVP can be swapped for object-store/database
implementations without touching endpoints, the pipeline, or the client. These interfaces are also the
**mock seam** for tests (§8).

```csharp
public interface IBlobStore                 // content-addressed source
{
    Task<bool> ExistsAsync(string hash, CancellationToken ct);
    Task PutAsync(string hash, Stream compressed, CancellationToken ct);
    Task<Stream?> OpenAsync(string hash, CancellationToken ct);
}

public interface IMetadataStore              // packages, versions, owners
{
    Task<Package?> GetPackageAsync(string ns, CancellationToken ct);
    Task<Version?> GetVersionAsync(string ns, string version, CancellationToken ct);
    Task UpsertPackageAsync(Package pkg, CancellationToken ct);
    Task AddVersionAsync(Version v, CancellationToken ct);          // fails if (ns,version) exists
    Task SetYankedAsync(string ns, string version, bool yanked, CancellationToken ct);
}

public interface IAccountStore               // accounts + tokens
{
    Task<Account?> ResolveTokenAsync(string presentedToken, CancellationToken ct);
    Task<(Account, string secret)> CreateTokenAsync(/* credentials */ CancellationToken ct);
}

public interface ISearchIndex                // list + search (§7)
{
    Task IndexAsync(Package pkg, CancellationToken ct);
    Task<SearchPage> SearchAsync(string query, int limit, string? cursor, CancellationToken ct);
    Task<ListPage> ListAsync(SortOrder sort, int limit, string? cursor, CancellationToken ct);
}
```

**Filesystem MVP layout** under a `--data` directory:

```
data/
  blobs/<hash[0:2]>/<hash>            # compressed source, content-addressed
  packages/<namespace>/meta.json     # Package
  packages/<namespace>/<version>.json# Version
  accounts/...                        # accounts + hashed token secrets
  index/...                           # search index (§7)
```

---

## 6. Compiler reuse for validation

The namespace lint (stage 6) and capability extraction (stage 8) require real compiler logic, so the
registry **reuses the compiler front end as a library** — the same way the LSP consumes compiler logic
rather than reimplementing it. This is exposed to the pipeline behind two interfaces so it is mockable in
handler tests and swappable in deployment:

```csharp
public interface IManifestValidator   { ValidationResult Validate(SourceTree tree, string ns); }
public interface ICapabilityExtractor { IReadOnlyList<string> PublicCapabilities(SourceTree tree); }
```

The default implementation references `Ashes.Frontend`/`Ashes.Semantics`. An alternative for looser
coupling is to shell out to the `ashes` CLI (a metadata-extraction subcommand) so the deployable only
bundles the compiler binary — the interface makes either choice invisible to the rest of the server.

Because these run over the *uploaded* source server-side, the capability audit and namespace guarantee
are authoritative, not client-trusted.

---

## 7. List and search

Both browse (`/packages`) and `/search` back onto `ISearchIndex`:

- **MVP:** an in-process index built from package metadata (namespace, description, keywords, latest
  version, download count), rebuilt on startup and updated on publish. Ranking is a simple weighted match
  (exact namespace > prefix > description/keyword), tie-broken by downloads. Filesystem-persisted so a
  restart doesn't lose it.
- **Later:** swap `ISearchIndex` for a real search backend at scale — no API or client change.

This is what powers the client's discovery command (§9).

---

## 8. Testing (TUnit + Shouldly + Imposter)

Tests live in the registry app's own test project and mirror the orchestration app's stack and BDD
style — TUnit as the runner, Shouldly for assertions, and Imposter for source-generated mocks
(`Imposter.Abstractions`, `[assembly: GenerateImposter]`, `.Returns`/`.Callback`, `.Instance()`).

Three layers:

- **Pipeline unit tests.** Drive the publish pipeline (§4) directly with the storage and validator
  interfaces **mocked via Imposter**, one test per stage outcome — first-claim vs `namespace_owned_by_another`,
  `version_exists` on a differing hash, each limit tripping `limit_exceeded`, `namespace_lint`,
  server-hash-mismatch. Given/when/then bodies; Shouldly on the `error.code` and the store interactions.
- **Endpoint tests.** A minimal-API `WebApplicationFactory` in-memory host with the stores mocked, so
  routing, auth, content negotiation, status codes, and the error envelope are covered without disk —
  including resolve, download, list, and search happy/edge paths and the `Authorization` header handling.
- **Storage integration tests.** The **real filesystem store** against a temp `--data` dir: blob
  content-addressing/dedup, atomic version write ordering, and index persistence across restart.

Example (illustrative):

```csharp
[Test]
public async Task Publishing_a_namespace_owned_by_another_account_is_rejected()
{
    // given
    var meta = Imposter.For<IMetadataStore>();
    meta.GetPackageAsync("Json", Any.Token).Returns(PackageOwnedBy("bob"));
    var pipeline = new PublishPipeline(meta.Instance(), /* blobs, validators mocked */ ...);

    // when
    var result = await pipeline.RunAsync(UploadFrom("alice"), Ct);

    // then
    result.Error!.Code.ShouldBe("namespace_owned_by_another");
}
```

---

## 9. Client commands this enables

The read endpoints give the CLI discovery, so `ashes` gains a search-and-add flow on top of the verbs in
PACKAGE_MANAGER §9:

- `ashes search <query>` → `GET /search`, prints a ranked list (namespace, latest, description); with an
  interactive terminal it offers selection, and choosing an entry runs the normal `add` on it.
- `ashes add <namespace>` → uses `GET /packages/{namespace}` to resolve and `.../source` to download.
- `ashes info <namespace>[@<version>]` → `GET /packages/{namespace}[/{version}]` for a details view
  (versions, capabilities, owners).

Discovery is read-only and unauthenticated, so it works against any registry the project is configured
for (PACKAGE_MANAGER §7.4), including private ones.

---

## 10. Project layout

```
registry/
  src/AshesRegistry/          # the minimal-API app (endpoints, pipeline, storage, validators)
  src/AshesRegistry.Storage.FileSystem/   # filesystem IBlobStore/IMetadataStore/... impls
  tests/AshesRegistry.Tests/  # TUnit + Shouldly + Imposter
  AshesRegistry.slnx
```

Independent build/test lifecycle from the compiler solution; the compiler is consumed only as a library
(or CLI) behind the §6 interfaces.
