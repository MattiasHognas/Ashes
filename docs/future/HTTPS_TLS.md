# HTTPS/TLS — Status & Roadmap

Transparent `https://` in `Ashes.Http.get` / `Ashes.Http.post` is now
landed. The active native-backend implementation is now the hermetic
`rustls` path shared by Linux x64, Linux arm64, and Windows x64,
and the base roadmap in this document is now complete.
This document records the completed work and the follow-up items that
remain intentionally outside the base HTTPS/TLS milestone.

The implementation shipped in this branch prioritizes:

1. **Step 1:** transparent `https://` in `Ashes.Http.get` / `Ashes.Http.post`
2. **Step 2:** raw TLS sockets via the public `Ashes.Net.Tls` module
3. **Step 3:** hermetic TLS (vendored implementation, no runtime
   dependency)

The long-term direction is now decided, and Step 3 is now the active
runtime path. The remaining work is no longer about selecting or
building a second default TLS implementation; follow-on work now lives
under the post-completion considerations at the end of this document.

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
HTTPS/TLS roadmap. That base roadmap is now implemented and validated.

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
| **Language specification** | `docs/LANGUAGE_SPEC.md` now documents `Ashes.Http.get` / `Ashes.Http.post` as accepting `http://` and `https://` URLs, with default port 443 for HTTPS, and documents the public `Ashes.Net.Tls` surface plus `TlsSocket` on the hermetic runtime path. |
| **Standard library docs** | `docs/STANDARD_LIBRARY.md` now describes HTTPS support in `Ashes.Http` and documents the public `Ashes.Net.Tls` module on the hermetic runtime path. |
| **Linux dynamic-import foundation** | The Linux x64 ELF linker now emits the dynamic loader/interpreter metadata and import tables needed for runtime `dlopen` / `dlsym` calls from generated executables. |
| **Windows dynamic-import foundation** | The Windows PE linker now emits the import tables needed for runtime `LoadLibraryA` / `GetProcAddress` calls plus Crypt32 root-store access from generated executables. |
| **Hermetic TLS runtime initialization** | The generated runtime now writes and loads a vendored `rustls-ffi` payload on first TLS use, resolves the `rustls_*` ABI, and builds a shared client configuration used by both `Ashes.Http` and `Ashes.Net.Tls`. |
| **System trust integration** | The hermetic runtime now uses system trust via the platform verifier by default, with `SSL_CERT_FILE` PEM-root override support on Linux for loopback tests and other controlled overrides. |
| **TLS leaf tasks** | Dedicated internal TLS handshake/send/receive/close leaf tasks now exist in IR, backend dispatch, wait integration, and generated runtime helpers. |
| **HTTP staging integration** | The staged HTTP client now accepts `https://`, defaults to port 443, persists the secure stage across resumes, inserts a TLS handshake stage, and routes send/receive/close through TLS task states on Linux x64, Linux arm64, and Windows x64. |
| **Public raw TLS API** | `Ashes.Net.Tls.connect/send/receive/close` now ships as a public built-in module using the same current TLS runtime path as `Ashes.Http`, with `TlsSocket` as a first-class resource type. |
| **Cross-backend runtime coverage** | Linux x64, Linux arm64, and Windows x64 backend coverage now all exercise HTTPS success, trust failure, hostname mismatch, async race behavior, and close-notify EOF semantics on the hermetic runtime path. Linux arm64 coverage runs natively on arm64 Linux or through qemu on x64 Linux, and Windows x64 coverage can run natively on Windows or through Wine-backed Linux CI by supplying `SSL_CERT_FILE` PEM roots to the compiled program. |
| **Examples and tests** | Added `examples/https_get.ash`, Linux/backend, Windows/backend, and CLI loopback TLS fixture coverage, updated ASH012 coverage for HTTPS, replaced the old `.ash` expectation that HTTPS is unsupported, and aligned end-to-end TLS failure expectations with the more specific rustls certificate diagnostics. |

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

- A broader `Ashes.Net.Tls` surface beyond the current
  `connect/send/receive/close` module.
- Mutual TLS (client certificates).
- Custom trust callbacks, custom CA bundles per call, or certificate
  pinning.
- TLS server acceptance.
- ALPN, HTTP/2, HTTP/3.
- Alternative TLS engines or a second non-hermetic default path.
- Bundling a CA set with produced executables by default. Distribution
  polish and trust customization are tracked separately.

------------------------------------------------------------------------

## Runtime Dependency

The active native-backend implementation uses a hermetic `rustls`
runtime payload embedded per TLS-using executable:

- **Linux x64 / Linux arm64**: the generated program writes and loads
  the vendored `librustls.so` payload on first TLS use. By default it
  uses the platform verifier for system trust; when `SSL_CERT_FILE` is
  set, it loads that PEM bundle instead.
- **Windows x64**: the generated program writes and loads the vendored
  `rustls.dll` payload on first TLS use. By default it uses the
  platform verifier against the Windows trust store; when
  `SSL_CERT_FILE` is set, it loads that PEM bundle instead.

The compiler itself does **not** link against OpenSSL for the active
HTTPS/TLS path, and user programs no longer require an external
OpenSSL installation. Programs that never touch `https://` or
`Ashes.Net.Tls` do not link the TLS payload at all.

If the embedded TLS payload or verifier initialization cannot be
loaded, the call returns `Error(...)` rather than crashing or
panicking.

The transitional OpenSSL loader/runtime path is gone; the shipped
runtime contract is the hermetic `rustls` path described above.

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
2. **Wait integration** — `rustls_connection_wants_read` /
  `rustls_connection_wants_write` plus socket callback would-block
  results are translated into the existing pending-wait path with the
  underlying socket fd, so the epoll/`WSAPoll` infrastructure is
  reused without modification.
3. **HTTP staging branch** — `EmitStepHttpTask` gets a
   `scheme = https` flag. When set, stages 2/3 (send/receive) use TLS
   leaf tasks instead of TCP leaf tasks, with an extra handshake stage
  inserted between connect and send, and an extra TLS close stage
  inserted before TCP close.
4. **Hermetic rustls loader** — a single process-global initializer
  (`ashes_tls_runtime_init`) writes and loads the vendored `rustls`
  payload, resolves the `rustls_*` ABI, builds a client config with a
  system-trust verifier by default, and supports PEM trust overrides
  via `SSL_CERT_FILE` when explicitly configured.

User-visible `Task(Str, Str)` semantics are unchanged.

------------------------------------------------------------------------

## Implementation Checklist

HTTPS/TLS should be treated as 100% complete only when every checklist
item below is done.

### Phase A — Finish the Current Shipped Path

- [x] Add explicit Linux arm64 HTTPS runtime validation.
  `src/Ashes.Tests/LinuxArm64BackendCoverageTests.cs` now runs the
  dedicated arm64 HTTPS loopback test natively on arm64 Linux and on
  x64 Linux hosts through `qemu-aarch64` / `qemu-aarch64-static` when
  an arm64 sysroot is available.
- [x] Add first-class HTTPS harness support to end-to-end `.ash` tests.
  The backend coverage tests and `Ashes.Cli test` flow now both have
  built-in HTTPS fixture coverage for successful HTTPS, trust-failure,
  and hostname-mismatch scenarios.
- [x] Clean up remaining transitional OpenSSL-specific code and docs
  that still described the old runtime path.
  The active runtime path is hermetic `rustls`; dead OpenSSL-only
  helper code and stale user-facing OpenSSL requirement wording have
  been removed from the shipped path.

### Phase B — Build the Hermetic TLS Runtime

- [x] Implement built-in hermetic TLS as the long-term runtime model.
  Embed the vendored TLS payload per executable rather than shipping a
  shared Ashes TLS runtime library.
- [x] Use `rustls` behind a thin C ABI wrapper as the default hermetic
  TLS engine.
  This is the current recommended direction because it preserves the
  memory-safety goal better than vendored C TLS stacks while still
  supporting the shipped Linux and Windows targets. Binary size should
  still be minimized, but size is secondary to speed, memory safety,
  and functionality.
- [x] Link the TLS payload only into compiled outputs that actually
  require HTTPS or `Ashes.Net.Tls`.
  Programs that never touch TLS should not pay the binary-size cost of
  the vendored runtime.
- [x] Import system trust roots at runtime for hermetic TLS.
  The default policy should stay aligned with the host OS trust store
  rather than shipping a bundled CA set by default. The current
  implementation uses the platform verifier by default and falls back
  to `SSL_CERT_FILE` PEM roots when explicitly supplied.
- [x] Repoint the staged `Ashes.Http` TLS leaf tasks from the current
  OpenSSL ABI to the hermetic TLS ABI.
- [x] Add backend/runtime coverage for the hermetic path on every
  shipped native backend.
  At minimum this should cover success, trust failure, hostname
  mismatch, EOF/close semantics, and async combinator behavior.
  `LinuxBackendCoverageTests`, `LinuxArm64BackendCoverageTests`, and
  `WindowsBackendCoverageTests` now cover that matrix, with qemu-backed
  arm64 execution on x64 Linux hosts and Wine-backed Windows backend
  execution in Linux CI.

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

- [x] Make hermetic TLS the default shipping path for both
  `Ashes.Http` and `Ashes.Net.Tls` on `linux-x64`, `linux-arm64`, and
  `win-x64`.
- [x] Remove the runtime OpenSSL requirement from user-facing docs once
  the hermetic path is landed.
- [x] Delete the transitional OpenSSL loader/runtime path once the
  hermetic path is proven on all shipped native backends.
  The old OpenSSL loader/fallback path is no longer part of the shipped
  runtime; TLS programs now load only the embedded `rustls-ffi` payload.
- [x] Update `docs/future/FUTURE_FEATURES.md`,
  `docs/future/ASYNC_NETWORKING.md`, and related architecture docs to
  describe hermetic TLS as landed rather than planned.

## Definition of Complete

HTTPS/TLS is 100% complete only when all of the following are true:

- [x] `Ashes.Http` HTTPS works on `linux-x64`, `linux-arm64`, and
  `win-x64` without any external OpenSSL installation.
- [x] `Ashes.Net.Tls` is public, documented, and tested.
- [x] `Ashes.Tests`, `Ashes.Lsp.Tests`, the `.ash` suite, and
  formatting checks pass with the hermetic path enabled.
- [x] The transitional OpenSSL path has been removed rather than kept
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
- no transitional system-OpenSSL runtime remains in the shipped path

------------------------------------------------------------------------

## Testing Approach

The landed coverage uses the existing loopback fixture style:

1. `LinuxBackendCoverageTests` hosts a local `SslStream` listener,
  writes a temporary PEM trust bundle, and runs a Linux LLVM HTTPS
  program with `SSL_CERT_FILE` set for that child process only.
2. `LinuxArm64BackendCoverageTests` mirrors the same HTTPS success,
  trust-failure, hostname-mismatch, async-race, and close-notify EOF
  matrix on the Linux arm64 backend, running either natively on arm64
  Linux or through `qemu-aarch64` / `qemu-aarch64-static` on x64 Linux.
3. `WindowsBackendCoverageTests` mirrors the same HTTPS matrix on the
  Windows backend and can run natively on Windows or through Wine-backed
  Linux CI by passing a Windows-visible `SSL_CERT_FILE` PEM bundle into
  the compiled program.
4. `ExampleSocketFixtureTests` exercises `examples/https_get.ash`
  against the same kind of loopback TLS listener.
5. `TestRunnerFixtureTests` and `Ashes.Cli test` now exercise built-in
  HTTPS fixture modes for success, trust failure, and hostname
  mismatch.
6. End-to-end `.ash` coverage now includes success and certificate
  failure expectations for both `Ashes.Http` and `Ashes.Net.Tls` on the
  hermetic path.

------------------------------------------------------------------------

## Standard Library and Spec Impact

- `docs/LANGUAGE_SPEC.md` — HTTP rules section now allows `https://`,
  documents default port 443, documents the public `Ashes.Net.Tls`
  module plus the `TlsSocket` resource type, and now describes the
  hermetic TLS runtime instead of the old OpenSSL dependency.
- `docs/STANDARD_LIBRARY.md` — HTTP module section now notes HTTPS
  support, and `Ashes.Net.Tls` is documented as a public built-in
  module on the hermetic TLS runtime.
- `docs/future/FUTURE_FEATURES.md` — HTTPS/TLS row links to this
  document and now marks the feature as landed on the hermetic runtime
  path rather than partial.
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
  automatically; the TLS close-notify/connection-free path is part of
  leaf-task cleanup and the existing resource cleanup path.
5. **Purity preserved.** HTTPS calls return `Task(Str, Str)`; no
   mutation, no in-place updates.
6. **No GC.** TLS configs and connection handles are freed
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
5. **TLS/runtime maintenance.** Vendored runtime upgrades, verifier
  compatibility updates, and related interoperability work remain
  normal maintenance tasks even though the base HTTPS/TLS roadmap is
  complete.
