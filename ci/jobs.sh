#!/usr/bin/env bash
# ci/jobs.sh — CI/CD job implementations, each running inside a Podman runner.
#
# Invoked by the justfile, e.g. `ci/jobs.sh build` or `ci/jobs.sh release 1.2.3`.
# Mirrors the steps in .github/workflows/{pull-request,push-to-main,release}.yaml
# so local runs match GitHub CI. See docs/LOCAL_CI.md.
set -euo pipefail

JOBS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=ci/lib/run.sh
source "${JOBS_DIR}/lib/run.sh"
# shellcheck source=ci/lib/s3.sh
source "${JOBS_DIR}/lib/s3.sh"

# Network-dependent examples that the GH matrix skips (no outbound net in CI).
SKIP_EXAMPLES='http_get.ash|https_get.ash|tcp_close.ash|tcp_connect.ash|tcp_receive.ash|tcp_send.ash'

# --- Individual jobs -------------------------------------------------------

build() {
  run_in base "
    set -e
    dotnet restore Ashes.slnx
    dotnet build Ashes.slnx --configuration Release --no-restore
  "
}

fmt_check() {
  run_in base "dotnet format Ashes.slnx --verify-no-changes"
}

test() {
  run_in base "
    set -e
    dotnet run --project src/Ashes.Tests/Ashes.Tests.csproj --configuration Release -- --results-directory TestResults --report-trx
    dotnet run --project src/Ashes.Lsp.Tests/Ashes.Lsp.Tests.csproj --configuration Release -- --results-directory TestResults --report-trx
  "
}

# Test run with coverage (mirrors push-to-main.yaml).
coverage() {
  run_in base "
    dotnet run --project src/Ashes.Tests/Ashes.Tests.csproj --configuration Release -- \
      --results-directory TestResults \
      --coverage --coverage-output coverage.cobertura.xml --coverage-output-format cobertura
  "
}

# Dependency freshness + vulnerabilities (the local Dependabot stand-in).
# Gates on known-vulnerable NuGet packages and high+ pnpm advisories; the
# "outdated" listings are informational only (upstream releases shouldn't break
# the build). Needs network (NuGet advisory DB + npm registry).
deps_check() {
  run_in base "
    set -uo pipefail
    fail=0
    dotnet restore Ashes.slnx

    echo '--- NuGet: vulnerable packages (gating) ---'
    vuln=\$(dotnet list Ashes.slnx package --vulnerable --include-transitive 2>&1)
    echo \"\$vuln\"
    if echo \"\$vuln\" | grep -q 'has the following vulnerable packages'; then
      echo '::error:: NuGet vulnerabilities found.' >&2
      fail=1
    fi

    echo '--- NuGet: outdated packages (report only) ---'
    dotnet list Ashes.slnx package --outdated || true

    echo '--- pnpm: audit high+ (gating) ---'
    cd vscode-extension
    corepack enable
    pnpm install --frozen-lockfile --force
    if ! pnpm audit --audit-level high; then
      echo '::error:: pnpm high/critical advisories found.' >&2
      fail=1
    fi

    echo '--- pnpm: outdated packages (report only) ---'
    pnpm outdated || true

    exit \$fail
  "
}

# Static analysis / SAST via Semgrep (the local CodeQL stand-in). Scans C#, TS/JS
# and for leaked secrets; exits non-zero on findings. Needs network on first run
# to fetch the registry rule packs.
sast() {
  run_in base "
    set -euo pipefail
    git config --global --add safe.directory /work
    semgrep --version
    semgrep scan \
      --config p/security-audit \
      --config p/csharp \
      --config p/typescript \
      --config p/secrets \
      --error \
      --metrics off \
      --exclude artifacts --exclude dist --exclude publish --exclude staging \
      --exclude runtimes --exclude node_modules --exclude .ci-cache \
      --exclude '*.vsix'
  "
}

# VS Code extension: lint, format check, compile, and xvfb integration tests
# against freshly published cli/lsp/dap binaries.
ext() {
  run_in base "
    set -e
    dotnet publish src/Ashes.Cli/Ashes.Cli.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/extension-test/compiler
    dotnet publish src/Ashes.Lsp/Ashes.Lsp.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/extension-test/lsp
    dotnet publish src/Ashes.Dap/Ashes.Dap.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/extension-test/dap
    cd vscode-extension
    corepack enable
    pnpm install --frozen-lockfile --force
    pnpm run lint
    pnpm run format:check
    pnpm run compile
    pnpm run pretest:integration
    export ASHES_COMPILER_PATH=/work/artifacts/extension-test/compiler/ashes
    export ASHES_LSP_PATH=/work/artifacts/extension-test/lsp/ashes-lsp
    export ASHES_DAP_PATH=/work/artifacts/extension-test/dap/ashes-dap
    xvfb-run -a pnpm run test:integration
  "
}

# Publish the self-contained single-file CLI for all three RIDs into
# artifacts/ashes/<rid>, consumed by the matrix job.
publish_cli() {
  run_in base "
    set -e
    for rid in linux-x64 linux-arm64 win-x64; do
      dotnet publish src/Ashes.Cli/Ashes.Cli.csproj --configuration Release --runtime \$rid --self-contained true -p:PublishSingleFile=true -o artifacts/ashes/\$rid
    done
  "
}

# Shared body for one matrix arch: exercise examples and tests. fmt stability is
# checked separately, once, by _matrix_fmt (it's arch-independent).
# $1 = runner image, $2 = mode (run|compile), $3 = CLI invocation prefix (may
# contain emulator + path). In 'compile' mode the examples are only compiled, not
# executed, and the test suite is skipped — used when the runner can build but not
# execute the produced binaries (e.g. linux-arm64 without a binfmt_misc handler).
_matrix_one() {
  local runner="$1" mode="$2" cli="$3"
  run_in "$runner" "
    set -euo pipefail
    git config --global --add safe.directory /work
    chmod +x artifacts/ashes/*/ashes 2>/dev/null || true

    # The linux-arm64 cross sysroot has no libicu, so the self-contained binary
    # aborts on first globalization use under qemu. Run it in invariant mode (the
    # base/linux-x64 leg above still exercises full ICU). qemu propagates host env.
    #
    # Tiered compilation spins up background JIT threads whose on-stack
    # replacement / code-patching qemu-aarch64-user mis-emulates, producing a
    # deterministic SIGSEGV during the first real compile (codegen is fine under
    # -strace, which serializes threads). Disabling it makes the emulated
    # compiler stable; the base leg still exercises the tiered JIT natively.
    if [ '$runner' = arm64 ]; then
      export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
      export DOTNET_TieredCompilation=0
    fi

    CLI='$cli'

    if [ '$mode' = compile ]; then
      # Compile-only is the emulated (arm64/qemu) path: each compile is a fresh,
      # CPU-bound qemu process (~4s of emulation overhead, startup-dominated), so
      # a 70-example sweep is ~5min serially. The compiles are independent and
      # write only to /tmp, so fan them out across host cores (~5x faster). A
      # non-zero from any child propagates via xargs (exit 123) + pipefail.
      echo '--- Compiling examples ($runner, compile-only, parallel)...'
      export CLI
      compile_one() {
        local out
        if ! out=\$(\$CLI compile \"\$1\" -o \"\$(mktemp)\" < /dev/null 2>&1); then
          echo \"=== FAILED compiling \$1 ===\"
          echo \"\$out\"
          return 1
        fi
      }
      export -f compile_one
      examples=()
      for example in examples/*.ash; do
        case \"\$(basename \"\$example\")\" in
          $SKIP_EXAMPLES) continue ;;
        esac
        examples+=(\"\$example\")
      done
      printf '%s\\n' \"\${examples[@]}\" \
        | xargs -P \"\$(nproc)\" -I{} bash -c 'compile_one \"\$@\"' _ {}
    else
      echo '--- Running examples ($runner)...'
      for example in examples/*.ash; do
        case \"\$(basename \"\$example\")\" in
          $SKIP_EXAMPLES) continue ;;
        esac
        \$CLI run \"\$example\" < /dev/null
      done

      echo '--- Running tests ($runner)...'
      \$CLI test tests
    fi
  "
}

# Verify fmt stability once, on the base runner. The formatter is pure
# Ashes.Formatter C# with no arch dependence — running it under qemu/wine would
# produce byte-identical output — so there's no value in repeating it per arch,
# and doing so would race three legs writing the shared /work tree in place.
_matrix_fmt() {
  run_in base "
    set -euo pipefail
    git config --global --add safe.directory /work
    CLI='./artifacts/ashes/linux-x64/ashes'
    echo '--- Verifying fmt...'
    \$CLI fmt examples -w > /dev/null
    \$CLI fmt tests/imports -w > /dev/null
    for test in tests/*.ash; do
      if grep -Eq '^//[[:space:]]*fmt-skip:' \"\$test\"; then continue; fi
      \$CLI fmt \"\$test\" -w > /dev/null
    done
    git diff --exit-code -- examples tests
  "
}

# Run the example/test matrix across all three runners, then verify fmt once.
#
# The three legs are independent containers over the same read-only-ish /work
# mount (their per-example output goes to /tmp inside each container), so they
# run in parallel — mirroring the GitHub matrix (fail-fast: false) and cutting
# wall-clock to the slowest leg. Each leg's output is captured to a log and
# replayed grouped afterwards so the interleaved streams stay readable. fmt is
# verified afterwards, sequentially, by _matrix_fmt (writes the shared tree in
# place, so it must not race the legs).
matrix() {
  # arm64 is compile-only by default. Running (vs compiling) arm64 output requires
  # the emulated compiler to exec the arm64 binaries it produces — nested foreign-arch
  # execution. That does NOT work in the rootless-podman runner: host binfmt_misc is
  # not propagated into rootless containers, it can't be registered inside one (the
  # userns denies the mount), and qemu-user does not transparently re-exec for .NET's
  # process spawning — so run/test fail with "Exec format error". Compile-only still
  # validates the full arm64 backend (IR -> LLVM -> arm64 codegen -> link).
  #
  # On a runner that CAN exec arm64 (a rootful engine that inherits the host
  # binfmt_misc handler, or a native arm64 host), set ASHES_MATRIX_ARM64_RUN=1 to run
  # the full suite. See docs/LOCAL_CI.md.
  local arm64_mode=compile
  if [[ -n "${ASHES_MATRIX_ARM64_RUN:-}" ]]; then
    arm64_mode=run
  fi

  local logdir
  logdir="$(mktemp -d)"
  local -a names=(linux-x64 linux-arm64 win-x64)
  local -a pids=()

  _matrix_one base "run" "./artifacts/ashes/linux-x64/ashes" \
    >"$logdir/linux-x64.log" 2>&1 &
  pids+=("$!")
  _matrix_one arm64 "$arm64_mode" "qemu-aarch64-static -L / ./artifacts/ashes/linux-arm64/ashes" \
    >"$logdir/linux-arm64.log" 2>&1 &
  pids+=("$!")
  _matrix_one win "run" "wine ./artifacts/ashes/win-x64/ashes.exe" \
    >"$logdir/win-x64.log" 2>&1 &
  pids+=("$!")

  local failed=() i
  for i in "${!names[@]}"; do
    wait "${pids[$i]}" || failed+=("${names[$i]}")
  done

  for i in "${!names[@]}"; do
    echo "==================== ${names[$i]} ===================="
    cat "$logdir/${names[$i]}.log"
  done
  rm -rf "$logdir"

  # fmt stability — once, sequentially (writes the shared /work tree in place).
  _matrix_fmt || failed+=("fmt")

  if (( ${#failed[@]} )); then
    echo "Matrix failed for: ${failed[*]}" >&2
    return 1
  fi
}

# --- Composite pipelines ---------------------------------------------------

# Fast inner loop for pre-commit.
ci_quick() {
  build
  test
}

# Full PR-equivalent pipeline (pull-request.yaml).
ci() {
  build
  fmt_check
  test
  deps_check
  sast
  ext
  publish_cli
  matrix
}

# --- Release (release.yml) -------------------------------------------------

# release <version>: publish CLI/LSP/DAP for 3 RIDs, build the vsix, zip every
# artifact into dist/, then upload dist/ to S3 under releases/<version>/.
release() {
  local version="${1:?usage: release <version>}"
  if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.]+)?$ ]]; then
    echo "release: invalid version '$version' (expected semver, e.g. 1.2.3)" >&2
    return 1
  fi

  run_in base "
    set -euo pipefail
    VERSION='$version'
    rm -rf dist publish && mkdir -p dist

    for rid in linux-x64 linux-arm64 win-x64; do
      dotnet publish src/Ashes.Cli/Ashes.Cli.csproj --configuration Release --runtime \$rid --self-contained true -p:PublishSingleFile=true -p:Version=\$VERSION -o publish/cli/\$rid
      dotnet publish src/Ashes.Lsp/Ashes.Lsp.csproj --configuration Release --runtime \$rid --self-contained true -p:PublishSingleFile=true -p:Version=\$VERSION -o publish/lsp/\$rid
      dotnet publish src/Ashes.Dap/Ashes.Dap.csproj --configuration Release --runtime \$rid --self-contained true -p:PublishSingleFile=true -p:Version=\$VERSION -o publish/dap/\$rid
    done

    stage_compiler() { # <rid> <binary> <llvm> <rustls>
      local rid=\$1 binary=\$2 llvm=\$3 rustls=\$4
      local s=staging/ashes-\$rid
      rm -rf \"\$s\" && mkdir -p \"\$s/runtimes/\$rid\"
      cp \"publish/cli/\$rid/\$binary\" \"\$s/\"
      cp \"publish/cli/\$rid/\$llvm\" \"\$s/\"
      cp \"publish/cli/\$rid/runtimes/\$rid/\$rustls\" \"\$s/runtimes/\$rid/\"
      cp \"publish/cli/\$rid/runtimes/\$rid/rustls.version\" \"\$s/runtimes/\$rid/\"
      [ -f LICENSE ] && cp LICENSE \"\$s/\" || true
      cp README.md \"\$s/\"
      (cd \"\$s\" && zip -r \"\$OLDPWD/dist/ashes-\$rid.zip\" . > /dev/null)
    }
    stage_tool() { # <publish-subdir> <rid> <binary> <artifact>
      local sub=\$1 rid=\$2 binary=\$3 artifact=\$4
      local s=staging/\$artifact
      rm -rf \"\$s\" && mkdir -p \"\$s\"
      cp \"publish/\$sub/\$rid/\$binary\" \"\$s/\"
      [ -f LICENSE ] && cp LICENSE \"\$s/\" || true
      cp README.md \"\$s/\"
      (cd \"\$s\" && zip \"\$OLDPWD/dist/\$artifact.zip\" * > /dev/null)
    }

    stage_compiler linux-x64   ashes     libLLVM.so  librustls.so
    stage_compiler linux-arm64 ashes     libLLVM.so  librustls.so
    stage_compiler win-x64     ashes.exe libLLVM.dll rustls.dll

    stage_tool lsp linux-x64   ashes-lsp     ashes-lsp-linux-x64
    stage_tool lsp linux-arm64 ashes-lsp     ashes-lsp-linux-arm64
    stage_tool lsp win-x64     ashes-lsp.exe ashes-lsp-win-x64
    stage_tool dap linux-x64   ashes-dap     ashes-dap-linux-x64
    stage_tool dap linux-arm64 ashes-dap     ashes-dap-linux-arm64
    stage_tool dap win-x64     ashes-dap.exe ashes-dap-win-x64

    cd vscode-extension
    corepack enable
    pnpm install --frozen-lockfile --force
    pnpm version --no-git-tag-version \$VERSION
    pnpm run compile
    pnpm dlx --config.ignoredBuiltDependencies[]=@vscode/vsce-sign --config.ignoredBuiltDependencies[]=keytar @vscode/vsce@3.9.1 package --no-dependencies --allow-missing-repository --skip-license --out ../dist/ashes-language-\$VERSION.vsix
  "

  echo "--- Uploading dist/ to S3 (releases/${version}/)..."
  s3_upload "dist" "releases/${version}"
}

# --- Dispatcher ------------------------------------------------------------

cmd="${1:?usage: jobs.sh <job> [args]}"
shift
case "$cmd" in
  build | fmt_check | test | coverage | deps_check | sast | ext | publish_cli | matrix | ci_quick | ci | release) "$cmd" "$@" ;;
  *)
    echo "jobs.sh: unknown job '$cmd'" >&2
    exit 1
    ;;
esac
