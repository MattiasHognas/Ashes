# HTTPS/TLS — Status & Roadmap

Transparent `https://` in `Ashes.Http.get` / `Ashes.Http.post` is now
landed on the Linux x64, Linux arm64, and Windows x64 backends via
OpenSSL 3 loaded at runtime.
This document records the completed work and the remaining follow-up
items.

The implementation shipped in this branch prioritizes:

1. **Step 1:** transparent `https://` in `Ashes.Http.get` / `Ashes.Http.post`
2. **Step 2:** raw TLS sockets via a future `Ashes.Net.Tls` module
3. **Step 3:** hermetic TLS (vendored implementation, no runtime
   dependency)

The long-term direction is now decided: Step 3 should become the
preferred shipping model. The current runtime-loaded OpenSSL path is
the landed transitional implementation, not the intended end-state.

## Locked Decisions

- hermetic TLS embedded per executable rather than shipped as a shared
  Ashes runtime library
- TLS payload linked only into compiled outputs that actually require
  `https://` or a future public `Ashes.Net.Tls` surface
- a memory-safe vendored TLS engine, using `rustls` behind a thin C ABI
  wrapper as the chosen implementation target
- system trust roots imported at runtime rather than a bundled CA set
  by default
- a public `Ashes.Net.Tls` module built on top of the same TLS runtime
  foundation as `Ashes.Http`

No further product-direction decisions are required for the base
HTTPS/TLS roadmap. The remaining work is implementation and validation.

HTTPS support layers on top of the async TCP runtime that already
landed in [`ASYNC_NETWORKING.md`](ASYNC_NETWORKING.md). It does not
require new user-visible syntax or changes to the `Task(E, A)`
discipline. The original landed milestone was intentionally narrow:
make `https://` URLs work in the existing HTTP client on the shipped
native backends, then build broader TLS surface area on the same
runtime foundation.

------------------------------------------------------------------------

## Completed Work

| Area | What was done |
|------|---------------|
| **Language specification** | `docs/LANGUAGE_SPEC.md` now documents `Ashes.Http.get` / `Ashes.Http.post` as accepting `http://` and `https://` URLs, with default port 443 for HTTPS and the current Linux x64 / Linux arm64 / Windows x64 runtime caveat. |
| **Standard library docs** | `docs/STANDARD_LIBRARY.md` now describes HTTPS support in `Ashes.Http` and the OpenSSL 3 runtime dependency used on Linux x64, Linux arm64, and Windows x64. |
| **Linux dynamic-import foundation** | The Linux x64 ELF linker now emits the dynamic loader/interpreter metadata and import tables needed for runtime `dlopen` / `dlsym` calls from generated executables. |
| **Windows dynamic-import foundation** | The Windows PE linker now emits the import tables needed for runtime `LoadLibraryA` / `GetProcAddress` calls plus Crypt32 root-store access from generated executables. |
| **OpenSSL runtime initialization** | The generated runtime now lazily loads `libssl.so.3` / `libcrypto.so.3`, initializes a shared `SSL_CTX*`, enables peer verification, and loads default verify paths on first HTTPS use. |
| **Windows trust-store bridge** | The Windows runtime now imports the current user's `ROOT` certificate store into the shared OpenSSL `SSL_CTX*` on first HTTPS use so loopback and system-trusted certificates verify through the same TLS path. |
| **TLS leaf tasks** | Dedicated internal TLS handshake/send/receive/close leaf tasks now exist in IR, backend dispatch, wait integration, and generated runtime helpers. |
| **HTTP staging integration** | The staged HTTP client now accepts `https://`, defaults to port 443, persists the secure stage across resumes, inserts a TLS handshake stage, and routes send/receive/close through TLS task states on Linux x64, Linux arm64, and Windows x64. |
| **Public raw TLS API** | `Ashes.Net.Tls.connect/send/receive/close` now ships as a public built-in module using the same current TLS runtime path as `Ashes.Http`, with `TlsSocket` as a first-class resource type. |
| **Examples and tests** | Added `examples/https_get.ash`, Linux/backend, Windows/backend, and CLI loopback TLS fixture coverage, updated ASH012 coverage for HTTPS, and replaced the old `.ash` expectation that HTTPS is unsupported. |

------------------------------------------------------------------------

## Why This Is Needed

The modern web is HTTPS by default. Before this work,
`Ashes.Http.get("https://...")` returned
`Error("https not supported")`, which made the HTTP client useless for
almost every real-world endpoint.

Ashes already has the async runtime, the staged HTTP-on-TCP state
machine, the socket-fixture test infrastructure, and the
`Task(Str, Str)` shape. The only missing piece is the TLS layer between
the TCP socket and the HTTP request/response framing.

------------------------------------------------------------------------

## Goal

Make `Ashes.Http.get` and `Ashes.Http.post` accept `https://` URLs and
return the same `Task(Str, Str)` they already return for `http://`.

All existing HTTP rules carry over unchanged:

- non-2xx responses return `Error("HTTP <status>")`
- chunked transfer encoding is not supported and returns `Error(...)`
- the successful payload is the response body text after the header
  separator
- the call must be made inside an `async` block (ASH012 still applies)

The default port for `https://` is 443.

------------------------------------------------------------------------

## Scope (V1)

| Area | V1 behavior |
|------|-------------|
| **Direction** | Client connections only. |
| **Protocol** | TLS 1.2 and TLS 1.3 only (older versions disabled). |
| **SNI** | Required. Hostname is sent as the SNI extension. |
| **Certificate validation** | Mandatory. Hostname verification mandatory. |
| **Trust store** | System trust store (see runtime dependency below). |
| **Default port** | 443 when scheme is `https://`. |
| **API surface** | None new. `Ashes.Http.get` / `Ashes.Http.post` only. |

------------------------------------------------------------------------

## Out of Scope (Explicitly Deferred)

The following are **not** part of V1 and should not be described as
done:

- A standalone `Ashes.Net.Tls.connect/send/receive/close` module.
- Mutual TLS (client certificates).
- Custom trust callbacks, custom CA bundles per call, or certificate
  pinning.
- TLS server acceptance.
- ALPN, HTTP/2, HTTP/3.
- Static linking or vendored TLS implementations. OpenSSL is a
  *runtime dependency*, not a build-time link.
- Bundling OpenSSL alongside the produced executable. Distribution
  polish is tracked separately.

------------------------------------------------------------------------

## Runtime Dependency

The landed native-backend implementation dynamically loads OpenSSL 3 at
runtime:

- **Linux x64 / Linux arm64**: `dlopen("libssl.so.3", RTLD_NOW)`. Most
  modern distros
  (Debian 12+, Ubuntu 22.04+, Fedora 36+) ship this by default;
  otherwise install via `apt install libssl3`, `dnf install openssl`,
  or equivalent.
- **Windows x64**: `LoadLibraryA("libssl-3-x64.dll")` /
  `LoadLibraryA("libcrypto-3-x64.dll")` with fallback to the generic
  OpenSSL 3 DLL names, plus `CertOpenSystemStoreA("ROOT")` to import
  the current user's root store into the shared `SSL_CTX*`.

The compiler itself does **not** link against OpenSSL. The dlopen
calls are emitted into the produced executable. Programs that never
touch `https://` have zero OpenSSL dependency — the loader is only
invoked on first HTTPS use.

If OpenSSL is not present at runtime, the call returns
`Error("https requires OpenSSL 3 (libssl) at runtime")`. This is a
recoverable error consistent with Ashes' purity rules — there is no
crash and no panic.

OpenSSL 1.1 is **not** supported in V1.

------------------------------------------------------------------------

## Architecture Sketch

The shipped async networking runtime already exposes:

- a task struct with `WaitKind` / `WaitHandle` / `WaitData0` /
  `WaitData1` slots
- negative `StateIndex` values (`-10..-15`) for leaf tasks
- per-platform readiness waits (Linux `epoll`, Windows `WSAPoll`)
- a staged HTTP leaf task that drives child TCP leaf tasks
  (connect → send → receive → close)

HTTPS reuses all of that:

1. **TLS leaf tasks** — four new internal IR instructions
   (`CreateTlsHandshakeTask`, `CreateTlsSendTask`, `CreateTlsReceiveTask`,
   `CreateTlsCloseTask`) emit dedicated runtime step functions
   (`ashes_step_tls_*_task`). These are **not** exposed via
   `BuiltinRegistry` in V1; they are emitted only from staged HTTPS
   lowering.
2. **Wait integration** — `SSL_get_error` returns
  `SSL_ERROR_WANT_READ` / `WANT_WRITE` are translated into the
  existing pending-wait path with the underlying socket fd, so the
  epoll/`WSAPoll` infrastructure is reused without modification.
3. **HTTP staging branch** — `EmitStepHttpTask` gets a
   `scheme = https` flag. When set, stages 2/3 (send/receive) use TLS
   leaf tasks instead of TCP leaf tasks, with an extra handshake stage
   inserted between connect and send, and an extra `SSL_shutdown`
   stage inserted before TCP close.
4. **OpenSSL loader** — a single process-global initializer
   (`ashes_tls_runtime_init`) `dlopen`s libssl, caches function
   pointers, builds an `SSL_CTX*` with TLS 1.2 minimum, peer verify
   on, default verify paths loaded, and (on Windows) the system
   `ROOT` certificate store imported via `CertOpenSystemStoreA`.

User-visible `Task(Str, Str)` semantics are unchanged.

------------------------------------------------------------------------

## Implementation Checklist

HTTPS/TLS should be treated as 100% complete only when every checklist
item below is done.

### Phase A — Finish the Current Shipped Path

- [ ] Add explicit Linux arm64 HTTPS runtime validation.
  The Linux TLS implementation path is shared across Linux x64 and
  Linux arm64, but this branch does not yet have dedicated arm64
  runtime fixture coverage comparable to the current Linux x64 and
  Windows x64 backend tests.
- [x] Add first-class HTTPS harness support to end-to-end `.ash` tests.
  The backend coverage tests already use loopback `SslStream` fixtures,
  but the `Ashes.Cli test` flow still lacks a built-in HTTPS fixture
  mode for successful HTTPS, trust-failure, and hostname-mismatch
  scenarios.
- [ ] Keep the current runtime-loaded OpenSSL path in bug-fix-only mode
  while the hermetic runtime is being built.

### Phase B — Build the Hermetic TLS Runtime

- [ ] Implement built-in hermetic TLS as the long-term runtime model.
  Embed the vendored TLS payload per executable rather than shipping a
  shared Ashes TLS runtime library.
- [ ] Use `rustls` behind a thin C ABI wrapper as the default hermetic
  TLS engine.
  This is the current recommended direction because it preserves the
  memory-safety goal better than vendored C TLS stacks while still
  supporting the shipped Linux and Windows targets. Binary size should
  still be minimized, but size is secondary to speed, memory safety,
  and functionality.
- [ ] Link the TLS payload only into compiled outputs that actually
  require HTTPS or `Ashes.Net.Tls`.
  Programs that never touch TLS should not pay the binary-size cost of
  the vendored runtime.
- [ ] Import system trust roots at runtime for hermetic TLS.
  The default policy should stay aligned with the host OS trust store
  rather than shipping a bundled CA set by default.
- [ ] Repoint the staged `Ashes.Http` TLS leaf tasks from the current
  OpenSSL ABI to the hermetic TLS ABI.
- [ ] Add backend/runtime coverage for the hermetic path on every
  shipped native backend.
  At minimum this should cover success, trust failure, hostname
  mismatch, EOF/close semantics, and async combinator behavior.

### Phase C — Expose Raw TLS Publicly

- [x] Expose a public `Ashes.Net.Tls` module.
  The current public surface is `connect/send/receive/close`, sharing the
  shipped TLS runtime foundation already used by `Ashes.Http`. Future
  hermetic-runtime work should re-point this module to the vendored path
  without changing the public API.
- [x] Document `Ashes.Net.Tls` in `docs/STANDARD_LIBRARY.md` and add
  examples that exercise connect, send, receive, and close.
- [x] Add end-to-end `.ash` coverage for the public raw TLS API.

### Phase D — Cut Over and Clean Up

- [ ] Make hermetic TLS the default shipping path for both
  `Ashes.Http` and `Ashes.Net.Tls` on `linux-x64`, `linux-arm64`, and
  `win-x64`.
- [ ] Remove the runtime OpenSSL requirement from user-facing docs once
  the hermetic path is landed.
- [ ] Delete the transitional OpenSSL loader/runtime path once the
  hermetic path is proven on all shipped native backends.
- [ ] Update `docs/future/FUTURE_FEATURES.md`,
  `docs/future/ASYNC_NETWORKING.md`, and related architecture docs to
  describe hermetic TLS as landed rather than planned.

## Definition of Complete

HTTPS/TLS is 100% complete only when all of the following are true:

- [ ] `Ashes.Http` HTTPS works on `linux-x64`, `linux-arm64`, and
  `win-x64` without any external OpenSSL installation.
- [x] `Ashes.Net.Tls` is public, documented, and tested.
- [ ] `Ashes.Tests`, `Ashes.Lsp.Tests`, the `.ash` suite, and
  formatting checks pass with the hermetic path enabled.
- [ ] The transitional OpenSSL path has been removed rather than kept
  as a default or silent fallback.

## Vendored Dependency Policy

The vendored TLS dependency should be maintained under the following
policy:

- exact version pinning rather than floating dependency ranges
- a scheduled dependency review once per quarter
- out-of-band updates for security or interoperability issues
- target patch window of 48 hours for critical or remotely exploitable
  TLS issues
- target patch window of 7 days for high-severity TLS issues
- medium- and low-severity updates batched into scheduled maintenance
  unless they affect correctness or interoperability
- the transitional system-OpenSSL path remains bug-fix only until it is
  removed

------------------------------------------------------------------------

## Testing Approach

The landed coverage uses the existing loopback fixture style:

1. `LinuxBackendCoverageTests` hosts a local `SslStream` listener,
  writes a temporary PEM trust bundle, and runs a Linux LLVM HTTPS
  program with `SSL_CERT_FILE` set for that child process only.
2. `WindowsBackendCoverageTests` hosts the same style of TLS listener,
  temporarily adds the loopback certificate to the current user's root
  store, and runs the Windows LLVM HTTPS program against that fixture.
3. `ExampleSocketFixtureTests` exercises `examples/https_get.ash`
  against the same kind of loopback TLS listener.
4. `tests/http_https_not_supported.ash` was updated to assert that
  HTTPS now routes into the networking stack instead of returning the
  old unsupported-feature error.

------------------------------------------------------------------------

## Standard Library and Spec Impact

- `docs/LANGUAGE_SPEC.md` — HTTP rules section now allows `https://`,
  documents default port 443, notes the current Linux x64 / Linux arm64 /
  Windows x64 OpenSSL 3 runtime dependency, and now documents the public
  `Ashes.Net.Tls` module plus the `TlsSocket` resource type. ASH012 unchanged.
- `docs/STANDARD_LIBRARY.md` — HTTP module section now notes HTTPS
  support and the current Linux x64 / Linux arm64 / Windows x64
  OpenSSL runtime dependency, and `Ashes.Net.Tls` is documented as a
  public built-in module.
- `docs/future/FUTURE_FEATURES.md` — HTTPS/TLS row links to this
  document and now marks the feature as partial rather than purely planned.
- New public builtins are exposed under `Ashes.Net.Tls` and lower through
  a staged TLS connect task plus dedicated TLS send/receive/close task states.
- No new diagnostics codes.

------------------------------------------------------------------------

## Ground Rules

This document follows the
[`FUTURE_FEATURES.md`](FUTURE_FEATURES.md) ground rules:

1. **Spec first.** `LANGUAGE_SPEC.md` updated before implementation.
2. **Layer discipline.** Frontend and Semantics carry no TLS logic;
   TLS lives entirely in the backend lowering and emitted runtime
   helpers.
3. **Test every invariant.** Handshake, hostname verification,
   default port, error propagation, and ASH012 enforcement all
   covered before this is considered landed.
4. **No user-visible `Drop`.** TLS sockets are cleaned up
   automatically; `SSL_free` is part of the leaf-task close path
   and the `Drop(Socket)` cleanup path.
5. **Purity preserved.** HTTPS calls return `Task(Str, Str)`; no
   mutation, no in-place updates.
6. **No GC.** TLS contexts and `SSL*` handles are freed
   deterministically by the leaf-task close stage and the existing
   resource cleanup path.

------------------------------------------------------------------------

## Post-Completion Considerations

These items are intentionally outside the base roadmap above. They are
follow-on features to consider only after HTTPS/TLS is complete under
the Definition of Complete section.

1. **Mutual TLS.** Client certificates remain a separate milestone.
2. **Custom trust configuration.** Per-call CA bundles, trust
   callbacks, and certificate pinning remain separate features.
3. **TLS server support.** Server-side accept/listen support is not
   part of the client-focused roadmap above.
4. **ALPN, HTTP/2, and HTTP/3.** Each is a separate protocol milestone.
5. **OpenSSL 1.1 transitional compatibility.** Only relevant if the
   temporary OpenSSL-backed path survives long enough to justify more
   compatibility work.
