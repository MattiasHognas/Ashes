# Future Features

Planned features and future work for the Ashes language and ecosystem. **Shipped** features are
documented in the normative docs under [`docs/md/`](../index.md) — syntax/semantics in
[LANGUAGE_SPEC.md](../reference/language.md), library APIs in [STANDARD_LIBRARY.md](../reference/standard-library.md),
and runtime/backend behavior in [ARCHITECTURE.md](../internals/architecture.md) — not here.

| Feature | Status | Description |
|---------|--------|-------------|
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Largely complete | LLVM passes, memory management, codegen. The audit roadmap and the whole 1BRC-driven optimization arc (Perceus-style in-place reuse + move/linearity elision, deterministic resource safety, structured parallelism on all three targets, byte-level parsing, SIMD `memchr` scan, zero-copy `mmap` input, data-parallel chunked fold, loop-invariant reset-safety) have **landed** — the full 1e9-row 1BRC now runs (~2m36s / 15.9 GB). See *Completed Work* there; a few concrete follow-up tasks (`CO-15`…`CO-18`) remain in its *Roadmap*. One server follow-up also lands here: **per-fd wakeup targeting** for the run-queue scheduler's aggregate wait (re-queue only ready leaves instead of all parked leaves) — attempted and reverted as too delicate; the three hazards found the hard way (arch-dependent packed `epoll_event` layout, a stack-alloca-in-scheduler-loop overflow, and the drain-timer interaction in the parked-list partition) are recorded in the commit that reverted it |
| Server Support | **Delivered** | First-class TCP / HTTP / HTTPS servers ship in the standard library (`Ashes.Net.Tcp.Server`, `Ashes.Http.Server`, `Ashes.Net.Tls.Server`) on all three targets: multi-reactor prefork, cooperative per-reactor concurrency with bounded per-connection memory, keep-alive with incremental Content-Length and chunked request buffering, response streaming, graceful shutdown (drain + `Stop.stop` capability), and `NetListen`/`NetConnect` capabilities. See the [standard library reference](../reference/standard-library.md) for the API and [architecture](../internals/architecture.md#server-runtime-multi-reactor-and-graceful-shutdown) for the runtime. One piece is deferred: streaming a request body *into* a handler as a resource, which waits on the resource-safety / affine-ownership work |
| [Self-Hosting](SELF_HOSTING.md) | Exploratory | Rewrite the compiler in Ashes |

---

## Ground Rules

1. **Spec first.** Update `LANGUAGE_SPEC.md` before implementing any new
   syntax or semantic rule.
2. **Layer discipline.** Respect the project dependency graph
   (Frontend → Semantics → Backend). Runtime behaviour never goes in
   Frontend.
3. **Test every invariant.** Each feature must ship with tests that prove
   the new guarantees.
4. **No user-visible `Drop`.** `Drop` is a compiler concept. Users see
   automatic cleanup.
5. **Purity preserved.** All values are immutable. There is no mutation.
   All APIs — standard library and user-defined — are pure: they return
   new values and never modify their arguments. There are no in-place
   updates visible to user code.
6. **No GC.** All resource and memory management is deterministic and
   compile-time verified.
