# HTTPS/TLS — Status & Roadmap

Transparent `https://` in `Ashes.Http.get` / `Ashes.Http.post` is now
landed on the Linux x64 backend via OpenSSL 3 loaded at runtime.
This document records the completed work and the remaining follow-up
items.

The implementation shipped in this branch prioritizes:

1. **Step 1:** transparent `https://` in `Ashes.Http.get` / `Ashes.Http.post`
2. **Step 2:** raw TLS sockets via a future `Ashes.Net.Tls` module
3. **Step 3:** hermetic TLS (vendored implementation, no runtime
   dependency)

HTTPS support layers on top of the async TCP runtime that already
landed in [`ASYNC_NETWORKING.md`](ASYNC_NETWORKING.md). It does not
require new user-visible syntax, a new module, or changes to the
`Task(E, A)` discipline. The landed milestone is intentionally narrow:
make `https://` URLs work in the existing HTTP client on Linux x64,
and leave broader TLS surface area for follow-on work.

------------------------------------------------------------------------

## Completed Work

| Area | What was done |
|------|---------------|
| **Language specification** | `docs/LANGUAGE_SPEC.md` now documents `Ashes.Http.get` / `Ashes.Http.post` as accepting `http://` and `https://` URLs, with default port 443 for HTTPS and the current Linux x64 runtime caveat. |
| **Standard library docs** | `docs/STANDARD_LIBRARY.md` now describes HTTPS support in `Ashes.Http` and the OpenSSL 3 runtime dependency used on Linux x64. |
| **Linux dynamic-import foundation** | The Linux x64 ELF linker now emits the dynamic loader/interpreter metadata and import tables needed for runtime `dlopen` / `dlsym` calls from generated executables. |
| **OpenSSL runtime initialization** | The generated runtime now lazily loads `libssl.so.3` / `libcrypto.so.3`, initializes a shared `SSL_CTX*`, enables peer verification, and loads default verify paths on first HTTPS use. |
| **TLS leaf tasks** | Dedicated internal TLS handshake/send/receive/close leaf tasks now exist in IR, backend dispatch, wait integration, and generated runtime helpers. |
| **HTTP staging integration** | The staged HTTP client now accepts `https://`, defaults to port 443, persists the secure stage across resumes, inserts a TLS handshake stage, and routes send/receive/close through TLS task states on Linux x64. |
| **Examples and tests** | Added `examples/https_get.ash`, Linux/backend and CLI loopback TLS fixture coverage, updated ASH012 coverage for HTTPS, and replaced the old `.ash` expectation that HTTPS is unsupported. |

------------------------------------------------------------------------

## Why This Is Needed

The modern web is HTTPS by default. Today `Ashes.Http.get("https://...")`
returns `Error("https not supported")`, which makes the HTTP client
useless for almost every real-world endpoint.

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

The landed Linux x64 implementation dynamically loads OpenSSL 3 at
runtime:

- **Linux x64**: `dlopen("libssl.so.3", RTLD_NOW)`. Most modern distros
  (Debian 12+, Ubuntu 22.04+, Fedora 36+) ship this by default;
  otherwise install via `apt install libssl3`, `dnf install openssl`,
  or equivalent.
- **Windows**: still deferred. The future implementation is expected to
  load OpenSSL dynamically rather than link it at compiler build time.

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
- per-platform readiness waits (Linux `epoll`, Windows IOCP)
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
   epoll/IOCP infrastructure is reused without modification.
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

## Remaining Work

The Linux x64 HTTP-over-TLS path is landed, but the broader roadmap is
not complete yet.

- Add Windows TLS runtime parity so `https://` works beyond the Linux
  x64 backend.
- Decide whether to expose a future raw `Ashes.Net.Tls` module instead
  of keeping TLS as an HTTP-internal transport detail.
- Evaluate hermetic/vendored TLS only after the runtime-loaded OpenSSL
  path has enough real-world bake time.
- Extend end-to-end `.ash` coverage further if the test runner gains a
  first-class HTTPS fixture harness.

------------------------------------------------------------------------

## Testing Approach

The landed coverage uses the existing loopback fixture style:

1. `LinuxBackendCoverageTests` now hosts a local `SslStream` listener,
  writes a temporary PEM trust bundle, and runs a Linux LLVM HTTPS
  program with `SSL_CERT_FILE` set for that child process only.
2. `ExampleSocketFixtureTests` exercises `examples/https_get.ash`
  against the same kind of loopback TLS listener.
3. `tests/http_https_not_supported.ash` was updated to assert that
  HTTPS now routes into the networking stack instead of returning the
  old unsupported-feature error.

------------------------------------------------------------------------

## Standard Library and Spec Impact

- `docs/LANGUAGE_SPEC.md` — HTTP rules section now allows `https://`,
  documents default port 443, and notes the current Linux x64 OpenSSL 3
  runtime dependency. ASH012 unchanged.
- `docs/STANDARD_LIBRARY.md` — HTTP module section now notes HTTPS
  support and the current Linux x64 OpenSSL runtime dependency.
- `docs/future/FUTURE_FEATURES.md` — HTTPS/TLS row links to this
  document and now marks the feature as partial rather than purely planned.
- No new builtins. No new IR instructions exposed to users.
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

## Future Considerations

These items are deliberately out of scope for V1 and are listed only
to record where the design can grow:

1. **`Ashes.Net.Tls` raw module.** Once the TLS IR instructions
   exist, exposing them via `BuiltinRegistry` + bindings + ASH012
   enforcement is mechanical. Track as a separate milestone.
2. **OpenSSL 1.1 fallback.** V1 rejects `libssl.so.1.1`. Adding a
   1.1 ABI table is straightforward if older RHEL-class distros
   become a target.
3. **Vendoring TLS.** A hermetic build (rustls behind a C ABI, or
   BoringSSL) removes the runtime OpenSSL dependency. Cost is
   roughly 3 MB per platform plus a build pipeline. Track under
   distribution / `COMPILER_OPTIMIZATION.md`.
4. **Windows DLL bundling.** Optionally copy
   `libssl-3-x64.dll` + `libcrypto-3-x64.dll` next to produced
   `.exe` files to remove the user install step on Windows. Smaller
   footprint than full vendoring; same license posture.
5. **mTLS, ALPN, HTTP/2.** Each is a separate milestone with its
   own spec entry.
