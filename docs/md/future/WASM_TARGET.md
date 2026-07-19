# WebAssembly Target: `wasm32`

## Goal

Add `wasm32` as a fifth backend target so Ashes programs can run in browsers and sandboxed plugin
hosts, alongside the existing native targets (`linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`).

## Why

- **Reach.** Browsers and WASM plugin hosts (Wasmtime, Wasmer, WASI runtimes) are a large deployment
  surface the native targets can't touch.
- **Fit.** The backend already lowers IR to LLVM, and LLVM emits `wasm32` — much of the codegen path
  is reusable.

## Caveats (be honest about this one)

WebAssembly is somewhat *off-brand*: the project's identity is "standalone native, zero runtime". A
WASM module is not standalone — it needs a host (a browser JS shim or a WASI runtime), and the
syscall-based IO the compiler emits today does not exist inside the sandbox. This is ranked below the
other roadmap items for that reason; pursue it when reach matters more than the native-standalone
story.

## Current state

No WASM target. IO is implemented with direct native syscalls per platform (see the ARM64/Windows
syscall notes in the backend); those have no WASM equivalent without a host ABI.

## What we should do

1. **Codegen.** Add the `wasm32` target triple to the backend and get pure, IO-free programs
   compiling and validating (`wasm-validate`) end to end.
2. **Host ABI.** Choose the runtime contract — **WASI** for server/CLI-style hosts, or a thin JS
   import shim for browsers. IO built-ins (`File`, `Console`, `Net`, `Process`) must be re-expressed
   as host imports instead of syscalls; some (raw sockets, `Process`) may be unsupported under WASI
   and should fail with a clear diagnostic.
3. **Memory model.** Map the arena/ownership memory model onto a single linear memory; no `mmap`, so
   the grow-on-demand arena must use `memory.grow`.
4. **Threads.** Structured parallelism relies on `clone`/futex; WASM threads are a separate,
   optional proposal — start single-threaded and gate parallelism behind a capability check.
5. **Toolchain + validation.** Structural validation on the CI hosts (validate the module, run it
   under a WASI runtime such as Wasmtime for the IO-capable subset), mirroring how win-arm64 is
   validated structurally.
6. **Docs.** Document the supported subset and the host requirements; update `cli.md` for the new
   `--target wasm32` value.

## Watch out for

- Don't promise the full native IO surface — scope the supported built-ins explicitly and diagnose
  the rest.
- Keep the target additive; it must not regress or complicate the four native targets.
