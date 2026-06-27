#!/usr/bin/env bash
# ci/jobs.sh — CI/CD job implementations. Most run inside a Podman runner; the
# release_github job is host-side orchestration (git + gh) around the container build.
#
# Invoked by the justfile, e.g. `ci/jobs.sh build` or `ci/jobs.sh release_github 1.2.3`.
# Mirrors the steps in .github/workflows/{pull-request,push-to-main,release}.yaml
# so local runs match GitHub CI. See docs/LOCAL_CI.md.
set -euo pipefail

JOBS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=ci/lib/run.sh
source "${JOBS_DIR}/lib/run.sh"

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
# $1 = runner image, $2 = CLI invocation (path to the ashes binary, possibly with
# an emulator prefix). Compiles and runs every (non-network) example, then runs the
# test suite. All three legs execute their produced binaries directly: x64 natively,
# arm64 via the host qemu-user-static binfmt handler (the arm64 image is a real
# aarch64 image), win-x64 via Wine.
_matrix_one() {
  local runner="$1" cli="$2"
  run_in "$runner" "
    set -euo pipefail
    git config --global --add safe.directory /work
    chmod +x artifacts/ashes/*/ashes 2>/dev/null || true

    # The arm64 leg runs under qemu-user (TCG) emulation. Tiered compilation spins
    # up background JIT threads whose on-stack replacement / code-patching qemu
    # mis-emulates, producing a deterministic SIGSEGV during the first real compile
    # (codegen is fine under -strace, which serializes threads). Disabling it makes
    # the emulated compiler stable; the native x64 leg still exercises the tiered
    # JIT. Globalization runs in invariant mode to keep the emulated leg focused on
    # codegen (the x64 leg exercises full ICU).
    if [ '$runner' = arm64 ]; then
      export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
      export DOTNET_TieredCompilation=0
    fi

    CLI='$cli'

    echo '--- Running examples ($runner)...'
    for example in examples/*.ash; do
      case \"\$(basename \"\$example\")\" in
        $SKIP_EXAMPLES) continue ;;
      esac
      \$CLI run \"\$example\" < /dev/null
    done

    echo '--- Running tests ($runner)...'
    \$CLI test tests
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
  # All three legs run the full suite. The arm64 leg executes its binaries inside a
  # genuine aarch64 container (ashes-ci-arm64), emulated transparently by the host's
  # qemu-user-static binfmt_misc handler — which must be registered with the F
  # (fix-binary) flag so emulation crosses into the container and survives the
  # compiler's nested exec of its output. `scripts/init-local-ci.sh` sets this up;
  # see docs/LOCAL_CI.md. If the handler is missing, the arm64 leg fails with
  # "Exec format error".
  local logdir
  logdir="$(mktemp -d)"
  local -a names=(linux-x64 linux-arm64 win-x64)
  local -a pids=()

  _matrix_one base "./artifacts/ashes/linux-x64/ashes" \
    >"$logdir/linux-x64.log" 2>&1 &
  pids+=("$!")
  _matrix_one arm64 "./artifacts/ashes/linux-arm64/ashes" \
    >"$logdir/linux-arm64.log" 2>&1 &
  pids+=("$!")
  _matrix_one win "wine ./artifacts/ashes/win-x64/ashes.exe" \
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

# release_build <version>: publish CLI/LSP/DAP for 3 RIDs, build the vsix, and
# zip every artifact into artifacts/release/ on local disk (dist/ is reserved for
# scripts/publish.sh's per-target copies). Publishing those artifacts to a GitHub
# Release is done by the release_github job (`just release-github`).
release_build() {
  local version="${1:?usage: release_build <version>}"
  if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.]+)?$ ]]; then
    echo "release_build: invalid version '$version' (expected semver, e.g. 1.2.3)" >&2
    return 1
  fi

  run_in base "
    set -euo pipefail
    VERSION='$version'
    OUT=artifacts/release
    rm -rf \$OUT publish && mkdir -p \$OUT

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
      (cd \"\$s\" && zip -r \"\$OLDPWD/\$OUT/ashes-\$rid.zip\" . > /dev/null)
    }
    stage_tool() { # <publish-subdir> <rid> <binary> <artifact>
      local sub=\$1 rid=\$2 binary=\$3 artifact=\$4
      local s=staging/\$artifact
      rm -rf \"\$s\" && mkdir -p \"\$s\"
      cp \"publish/\$sub/\$rid/\$binary\" \"\$s/\"
      [ -f LICENSE ] && cp LICENSE \"\$s/\" || true
      cp README.md \"\$s/\"
      (cd \"\$s\" && zip \"\$OLDPWD/\$OUT/\$artifact.zip\" * > /dev/null)
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
    pnpm dlx --config.ignoredBuiltDependencies[]=@vscode/vsce-sign --config.ignoredBuiltDependencies[]=keytar @vscode/vsce@3.9.1 package --no-dependencies --allow-missing-repository --skip-license --out ../\$OUT/ashes-language-\$VERSION.vsix
  "
}

# --- Release helpers (host-side; used by release_github) -------------------
if [[ -t 1 ]]; then _B=$'\033[1m'; _G=$'\033[32m'; _Y=$'\033[33m'; _R=$'\033[31m'; _N=$'\033[0m'; else _B= _G= _Y= _R= _N=; fi
_step() { echo "${_B}==>${_N} $*"; }
_ok()   { echo "  ${_G}ok${_N} $*"; }
_warn() { echo "  ${_Y}warn${_N} $*" >&2; }
_die()  { echo "${_R}error${_N} $*" >&2; exit 1; }
_have() { command -v "$1" >/dev/null 2>&1; }
_valid_semver() { [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.]+)?$ ]]; }

# release_github [version] [-y]: cut a release/X.Y.Z branch from origin/main,
# build the artifacts (release_build → artifacts/release/), tag vX.Y.Z, and
# publish a GitHub Release with that artifact set. Interactive: prompts for and
# validates the version (pass it as an arg to pre-fill; -y/--yes skips confirms).
# Host-side orchestration (git + gh); the build itself runs in the base runner.
# Local stand-in for the disabled .github/workflows/release.yml.
release_github() {
  cd "$CI_REPO_ROOT"

  local base_branch="${RELEASE_BASE_BRANCH:-main}"
  local remote="${RELEASE_REMOTE:-origin}"
  local version="" assume_yes=0
  for arg in "$@"; do
    case "$arg" in
      -y | --yes) assume_yes=1 ;;
      -*) _die "release_github: unknown option '$arg'" ;;
      *) version="$arg" ;;
    esac
  done

  _confirm() { # <prompt>
    [[ "$assume_yes" == 1 ]] && return 0
    local reply
    read -r -p "$1 [y/N] " reply
    [[ "$reply" =~ ^[Yy]$ ]]
  }

  # --- preflight ---
  _step "Preflight checks"
  _have git || _die "git not found on PATH"
  _have gh || _die "gh (GitHub CLI) not found on PATH — install it and run 'gh auth login'"
  gh auth status >/dev/null 2>&1 || _die "gh is not authenticated — run 'gh auth login'"
  [[ -z "$(git status --porcelain)" ]] || _die "working tree is not clean — commit or stash changes before releasing"
  _ok "git, gh authenticated, working tree clean"

  local orig_ref
  orig_ref="$(git rev-parse --abbrev-ref HEAD)"
  _restore_branch() { git switch "$orig_ref" >/dev/null 2>&1 || true; }

  _step "Fetching ${remote}/${base_branch}"
  git fetch --quiet "$remote" "$base_branch" --tags
  git rev-parse --verify --quiet "refs/remotes/${remote}/${base_branch}" >/dev/null \
    || _die "${remote}/${base_branch} not found"
  _ok "${remote}/${base_branch} is at $(git rev-parse --short "${remote}/${base_branch}")"

  # --- choose version ---
  local latest_tag
  latest_tag="$(git tag --list 'v[0-9]*' --sort=-v:refname | head -n1)"
  [[ -n "$latest_tag" ]] && echo "  latest released tag: ${_B}${latest_tag}${_N}" || echo "  no previous release tags found"

  if [[ -z "$version" ]]; then
    local suggestion=""
    if [[ "$latest_tag" =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
      suggestion="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.$(( BASH_REMATCH[3] + 1 ))"
    fi
    read -r -p "Release version (semver, e.g. ${suggestion:-1.2.3}): " version
    version="${version:-$suggestion}"
  fi
  [[ -n "$version" ]] || _die "no version provided"
  version="${version#v}" # tolerate a leading 'v'
  _valid_semver "$version" || _die "invalid version '$version' (expected semver like 1.2.3)"

  local tag="v$version" branch="release/$version"

  # --- collision checks ---
  git rev-parse --verify --quiet "refs/tags/$tag" >/dev/null \
    && _die "tag $tag already exists locally"
  git ls-remote --exit-code --tags "$remote" "$tag" >/dev/null 2>&1 \
    && _die "tag $tag already exists on $remote"
  git ls-remote --exit-code --heads "$remote" "$branch" >/dev/null 2>&1 \
    && _die "branch $branch already exists on $remote"
  gh release view "$tag" >/dev/null 2>&1 \
    && _die "a GitHub release for $tag already exists"

  echo
  echo "About to release ${_B}Ashes ${tag}${_N}:"
  echo "  • branch  ${_B}${branch}${_N}  (from ${remote}/${base_branch})"
  echo "  • tag     ${_B}${tag}${_N}"
  echo "  • build   CLI + LSP + DAP for linux-x64 / linux-arm64 / win-x64, plus the .vsix"
  echo "  • publish a GitHub Release with the artifacts attached"
  echo
  _confirm "Proceed?" || _die "aborted"

  # --- create release branch locally ---
  # Built and tagged locally; pushed only after a successful build so a failed
  # build never leaves a dangling release/* branch on the remote.
  _step "Creating local branch $branch from ${remote}/${base_branch}"
  git branch -f "$branch" "${remote}/${base_branch}"
  git switch "$branch"
  _ok "on $branch"

  _cleanup_on_fail() {
    _warn "release failed — cleaning up local refs"
    _restore_branch
    git branch -D "$branch" >/dev/null 2>&1 || true
    git tag -d "$tag" >/dev/null 2>&1 || true
  }
  trap _cleanup_on_fail ERR

  # --- build artifacts ---
  local out="artifacts/release"
  _step "Building release artifacts (version $version) → $out/"
  release_build "$version"

  # The exact artifact set published by release.yml (filenames match release_build).
  local artifacts=(
    "$out/ashes-win-x64.zip"
    "$out/ashes-linux-x64.zip"
    "$out/ashes-linux-arm64.zip"
    "$out/ashes-lsp-win-x64.zip"
    "$out/ashes-lsp-linux-x64.zip"
    "$out/ashes-lsp-linux-arm64.zip"
    "$out/ashes-dap-win-x64.zip"
    "$out/ashes-dap-linux-x64.zip"
    "$out/ashes-dap-linux-arm64.zip"
    "$out/ashes-language-$version.vsix"
  )
  local f
  for f in "${artifacts[@]}"; do
    [[ -f "$f" ]] || _die "expected artifact missing: $f"
  done
  _ok "built ${#artifacts[@]} artifacts"

  # --- tag, push, publish ---
  _step "Tagging $tag"
  git tag -a "$tag" -m "Ashes $tag"

  trap - ERR  # past the point where local-only cleanup is safe

  _step "Pushing $branch and $tag to $remote"
  git push "$remote" "$branch"
  git push "$remote" "$tag"

  _step "Creating GitHub Release $tag"
  gh release create "$tag" "${artifacts[@]}" \
    --title "Ashes $tag" \
    --target "$branch" \
    --notes ""

  _restore_branch

  echo
  echo "${_G}${_B}Released Ashes ${tag}.${_N}"
  echo "  branch:  ${_B}${branch}${_N}"
  echo "  release: $(gh release view "$tag" --json url --jq .url 2>/dev/null || echo "$tag")"
}

# --- Dispatcher ------------------------------------------------------------

cmd="${1:?usage: jobs.sh <job> [args]}"
shift
case "$cmd" in
  build | fmt_check | test | coverage | deps_check | sast | ext | publish_cli | matrix | ci_quick | ci | release_build | release_github) "$cmd" "$@" ;;
  *)
    echo "jobs.sh: unknown job '$cmd'" >&2
    exit 1
    ;;
esac
