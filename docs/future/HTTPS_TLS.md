# HTTPS/TLS — Status & Roadmap

Transparent `https://` in `Ashes.Http.get` / `Ashes.Http.post` is now
part of the shipped language surface. The landed work completed in
three steps: transparent HTTPS in `Ashes.Http`, a public raw TLS
surface in `Ashes.Net.Tls`, and a hermetic vendored TLS runtime with no
external OpenSSL dependency.

The active native-backend implementation is the hermetic `rustls` path
shared by Linux x64, Linux arm64, and Windows x64. HTTPS support layers
on top of the async TCP runtime that already landed in
[`ASYNC_NETWORKING.md`](ASYNC_NETWORKING.md) and does not require new
user-visible syntax or changes to the `Task(E, A)` discipline.

------------------------------------------------------------------------

## Completed Work

All original HTTPS/TLS roadmap items have been completed:

| Area | What was done |
|------|---------------|
| **Language and library surface** | `docs/LANGUAGE_SPEC.md` and `docs/STANDARD_LIBRARY.md` now document `Ashes.Http.get` / `Ashes.Http.post` as accepting both `http://` and `https://` URLs with default HTTPS port 443, and document the public `Ashes.Net.Tls` module with `TlsSocket`. No new user-visible syntax was introduced, and the existing `Task(E, A)` discipline remains unchanged. |
| **Shipped TLS contract** | Hermetic TLS is embedded per executable rather than shipped as a shared Ashes runtime library. The TLS payload is linked only into programs that use `https://` or `Ashes.Net.Tls`; `rustls` behind a thin C ABI wrapper is the chosen engine; system trust roots are imported at runtime by default; and `Ashes.Net.Tls` shares the same runtime foundation as `Ashes.Http`. |
| **Hermetic runtime model** | The shipped TLS path is a hermetic `rustls` runtime payload embedded per TLS-using executable on Linux x64, Linux arm64, and Windows x64. The generated program writes and loads the vendored `librustls.so` or `rustls.dll` on first TLS use, resolves the `rustls_*` ABI, and builds shared client configuration used by both `Ashes.Http` and `Ashes.Net.Tls`. Programs that never touch `https://` or `Ashes.Net.Tls` do not link the TLS payload at all. |
| **Trust and failure behavior** | Certificate validation and hostname verification are mandatory. The runtime uses the platform verifier against system trust by default and supports `SSL_CERT_FILE` as an explicit PEM-root override for controlled scenarios such as loopback tests. If payload loading or verifier initialization fails, the call returns `Error(...)` rather than crashing or panicking. The transitional OpenSSL loader/runtime path has been removed. |
| **Backend and runtime plumbing** | The Linux x64 ELF linker now emits the loader/import metadata needed for runtime `dlopen` / `dlsym`, and the Windows PE linker emits the import tables needed for `LoadLibraryA` / `GetProcAddress` plus Crypt32 root-store access. The backend now lowers dedicated internal TLS handshake/send/receive/close leaf tasks, integrates their wait behavior with existing epoll / `WSAPoll` readiness handling, and extends staged HTTP lowering with secure connect, handshake, send, receive, and close stages. |
| **Public TLS API** | `Ashes.Net.Tls.connect/send/receive/close` ships as a public built-in module backed by the same hermetic runtime path as `Ashes.Http`, with `TlsSocket` as a first-class resource type. This gives Ashes both transparent HTTPS in the HTTP client and an explicit raw TLS client surface on the same runtime foundation. |
| **Validation and coverage** | Coverage now exercises HTTPS success, trust failure, hostname mismatch, async-race behavior, and close-notify EOF semantics across Linux x64, Linux arm64, and Windows x64. Linux arm64 coverage runs natively on arm64 Linux or through `qemu-aarch64` / `qemu-aarch64-static` on x64 Linux, and Windows x64 coverage can run natively on Windows or through Wine-backed Linux CI with `SSL_CERT_FILE` passed into the compiled program. `ExampleSocketFixtureTests`, `TestRunnerFixtureTests`, `Ashes.Cli test`, and end-to-end `.ash` coverage all include HTTPS/TLS fixture coverage, including `examples/https_get.ash`, success paths, and certificate-failure expectations aligned with rustls diagnostics. |
| **Documentation and cleanup** | `docs/future/FUTURE_FEATURES.md`, `docs/future/ASYNC_NETWORKING.md`, and related documentation now describe HTTPS/TLS as landed on the hermetic runtime path rather than partial or planned. User-facing OpenSSL requirements and dead transitional helper code have been removed. |

Remaining work is outside this completed base milestone: mutual TLS /
client certificates, custom trust configuration such as per-call CA
bundles, trust callbacks, or certificate pinning, TLS server-side
accept/listen support, ALPN, HTTP/2, HTTP/3, and routine vendored TLS
maintenance including exact version pinning, quarterly review, and
expedited security updates.
