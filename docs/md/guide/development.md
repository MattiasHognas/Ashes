# Development

This guide covers everything needed to build, test, and develop Ashes
locally.

---

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| .NET SDK | 10.0+ | Specified in `global.json` |
| Node.js | 20.19+, 22.13+, or 24+ | VS Code extension only |
| pnpm | (managed via corepack) | VS Code extension only |
| bash | any | Scripts and LLVM provisioning |

---

## Repository Layout

```text
Ashes.slnx                 Solution file
src/
  Ashes.Frontend/           Lexer, parser, AST
  Ashes.Semantics/          Binding, type inference, IR, optimizer
  Ashes.Backend/            LLVM codegen and native linker
  Ashes.Formatter/          Canonical source formatting
  Ashes.Cli/                CLI orchestration (compile, run, repl, test, fmt)
  Ashes.Lsp/                Language server
  Ashes.Dap/                Debug adapter
  Ashes.TestRunner/         End-to-end .ash test execution
  Ashes.Tests/              Compiler unit tests
  Ashes.Lsp.Tests/          LSP unit tests
docs/                       Specifications and references
examples/                   Example .ash programs
tests/                      End-to-end .ash test suite
scripts/                    Development and CI helper scripts
vscode-extension/           VS Code extension source
```

---

## Building

Build the entire solution:

```sh
dotnet build Ashes.slnx
```

Release build:

```sh
dotnet build Ashes.slnx --configuration Release
```

---

## Native Runtime Libraries

The native backend requires LLVM libraries, and HTTPS/TLS workloads also
require rustls-ffi shared libraries. LLVM must be provisioned before
running backend or end-to-end tests; the rustls-ffi payloads are
vendored under `runtimes/{linux-x64,linux-arm64,win-x64}/` and only need
to be refreshed when bumping `RustlsFfiVersion`:

```sh
# LLVM: all platforms (linux-x64, linux-arm64, win-x64):
bash scripts/download-llvm-native.sh --all

# LLVM: current architecture only:
bash scripts/download-llvm-native.sh

# LLVM: specific architecture:
bash scripts/download-llvm-native.sh 22 arm64

# rustls-ffi: refresh all vendored payloads (linux-x64, linux-arm64, win-x64):
bash scripts/download-rustls-ffi.sh --all

# rustls-ffi: refresh selected vendored payloads:
bash scripts/download-rustls-ffi.sh --linux-x64 --win-x64
```

For rustls-ffi, `--linux-arm64` builds the arm64 payload from source
because upstream does not publish a prebuilt Linux arm64 shared
library. The default rustls-ffi version is read from
`Directory.Build.props`.

On Windows, run the bash scripts from WSL. A normal checkout already
includes the rustls-ffi payloads; only LLVM must be downloaded for test
setup unless you are intentionally refreshing the vendored rustls files:

```sh
# Download all supported LLVM payloads:
bash scripts/download-llvm-native.sh --all

# Optional: refresh the vendored rustls payloads:
bash scripts/download-rustls-ffi.sh --all
```

The scripts stage LLVM payloads and refreshed rustls payloads under
`runtimes/{linux-x64,linux-arm64,win-x64}/`. `Ashes.Backend.csproj`
validates the vendored rustls-ffi version and copies both LLVM and
rustls assets into build output.

### Optional linux-arm64 execution from linux-x64 hosts

The repo can also execute `linux-arm64` backend outputs from an x64 Linux
host when all of the following are available:

- `qemu-aarch64` or `qemu-aarch64-static`
- an arm64 sysroot containing `lib/ld-linux-aarch64.so.1`
   (for example `/usr/aarch64-linux-gnu`)
- arm64 runtime support libraries including `libgcc_s.so.1` and
  `libstdc++.so.6`

`src/Ashes.Tests/LinuxArm64BackendCoverageTests.cs` auto-detects emulator
binaries from both `PATH` and the rootless Arch-style unpack location
`~/.local/share/ashes-tools/qemu-user-static/root/usr/bin`.

For manual runs outside the test helper, add the rootless install to
`PATH` if needed:

```sh
export PATH="$HOME/.local/share/ashes-tools/qemu-user-static/root/usr/bin:$PATH"
qemu-aarch64-static -L /usr/aarch64-linux-gnu ./hello-arm64
```

### Optional win-x64 execution from linux-x64 hosts

`win-x64` backend outputs can also be executed from an x64 Linux host
when a Wine launcher is available, such as `wine64`, `wine`, or
`wine-stable` in `PATH`. On Ubuntu 24.04, installing the `wine` package
provides `wine-stable`, while `wine64` alone lives at
`/usr/lib/wine/wine64`.

`src/Ashes.Tests/TestProcessHelper.cs` auto-detects Wine for test-time PE
execution, so `EndToEndWindowsBackendTests` and
`WindowsBackendCoverageTests` can run from Linux hosts once Wine is
installed.

The Windows TLS runtime still defaults to the platform verifier. For
loopback TLS tests and other controlled overrides, the coverage helper
passes `SSL_CERT_FILE` to the compiled PE program using a Wine-visible
path so the vendored `rustls.dll` can load PEM roots without touching a
host Windows certificate store.

---

## Running the Compiler

```sh
# Compile and run a program:
dotnet run --project src/Ashes.Cli -- run hello.ash

# Run an inline expression:
dotnet run --project src/Ashes.Cli -- run --expr "Ashes.IO.print(40 + 2)"

# Compile to a native executable:
dotnet run --project src/Ashes.Cli -- compile hello.ash -o hello

# Cross-compile:
dotnet run --project src/Ashes.Cli -- compile --target linux-arm64 hello.ash -o hello

# REPL:
dotnet run --project src/Ashes.Cli -- repl
```

---

## Testing

### Compiler Unit Tests

```sh
dotnet run --project src/Ashes.Tests -- --no-progress
```

Filter by test class:

```sh
dotnet run --project src/Ashes.Tests -- --no-progress --treenode-filter "/*/*/ClassName/**"
```

### LSP Unit Tests

```sh
dotnet run --project src/Ashes.Lsp.Tests -- --no-progress
```

### End-to-End Tests

```sh
dotnet run --project src/Ashes.Cli -- test tests
```

### Full Verification

The `scripts/verify.sh` script runs the complete validation pipeline:
build, format check, unit tests, publish, format examples/tests, run
examples, and run end-to-end tests:

```sh
bash scripts/verify.sh
```

---

## Formatting

### `.ash` Files

All `.ash` source files must be canonically formatted. After creating or
modifying any `.ash` file:

```sh
# Format a file or directory (prints formatted output):
dotnet run --project src/Ashes.Cli -- fmt path/to/file.ash

# Format in place:
dotnet run --project src/Ashes.Cli -- fmt examples -w
```

### C# Code

```sh
dotnet format Ashes.slnx
```

---

## VS Code Extension

### Local Development

Build and install the extension locally:

```sh
bash scripts/install-vscode-extension-local.sh
```

By default this publishes the compiler, language server, and debug
adapter for the current RID only, then packages and installs the VSIX.
Use `--skip-install` to package without installing:

```sh
bash scripts/install-vscode-extension-local.sh --skip-install
```

Useful options:

```sh
# Publish all supported bundled RIDs (win-x64, linux-x64, linux-arm64):
bash scripts/install-vscode-extension-local.sh --all-rids

# Force a clean pnpm dependency reinstall before building:
bash scripts/install-vscode-extension-local.sh --force-install-dependencies

# Override the VS Code CLI to use for install:
bash scripts/install-vscode-extension-local.sh --code-command code-insiders

# When running from bash on Windows/WSL and targeting the Windows VS Code build:
bash scripts/install-vscode-extension-local.sh --target-rid win-x64
```

On Windows, run the same script from WSL. Use `--target-rid win-x64` when
you need to bundle binaries for the Windows VS Code build.

### Extension Only (No Publish)

To work on the extension TypeScript code without re-publishing .NET
projects:

```sh
cd vscode-extension
pnpm install --frozen-lockfile
pnpm run compile
pnpm run lint
pnpm run format:check
```

---

## Publishing

Build self-contained executables for distribution:

```sh
bash scripts/publish.sh
```

On Windows, run the same command from WSL.

---

## Development Rules

1. **Spec first.** Update the [Language Reference](../reference/language.md) before
   implementing any new syntax or semantic rule.
2. **Layer discipline.** Respect the project dependency graph
   (Frontend → Semantics → Backend). Runtime behaviour never goes in
   Frontend; the LSP must not depend on Backend.
3. **Test every invariant.** Ship tests that prove new guarantees.
4. **Format `.ash` files.** Run `ashes fmt` after creating or modifying
   any `.ash` source.
5. **No `[NotInParallel]` in tests.** This attribute is banned. Fix the
   root cause instead.

See [Architecture](../internals/architecture.md) for the full dependency graph
and compiler internals.
