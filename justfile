# Local containerized CI/CD for Ashes — see docs/LOCAL_CI.md.
#
# Quick start:
#   just images          # build the three runner images (once)
#   just provision       # fetch LLVM native libs into runtimes/ (once / on bump)
#   just ci-quick        # fast build + test
#   just ci              # full PR-equivalent pipeline
#   just release 1.2.3   # build release artifacts into artifacts/release/ (local)
#   just release-github  # build + tag + publish a GitHub Release
#   just install-hooks   # wire commit-msg (ban trailers) + pre-commit (ci-quick) + pre-push (ci)

engine     := env_var_or_default("CI_ENGINE", "podman")
node_major := env_var_or_default("NODE_MAJOR", "26")

# List available recipes.
default:
    @just --list

# Bootstrap a fresh machine: install deps, build images, provision runtimes.
# Pass flags through, e.g. `just init --all`. See scripts/init-local-ci.sh.
init *args:
    scripts/init-local-ci.sh {{args}}

# --- Container images ------------------------------------------------------

# Build the base, arm64, and win runner images. The arm64 image is a genuine
# aarch64 image (emulated via the host qemu-user-static binfmt handler), so it is
# built --platform linux/arm64 and does not derive from the x64 base.
images:
    {{engine}} build -t ashes-ci-base:latest --build-arg NODE_MAJOR={{node_major}} -f ci/images/Containerfile.base ci/images
    {{engine}} build -t ashes-ci-arm64:latest --platform=linux/arm64 -f ci/images/Containerfile.arm64 ci/images
    {{engine}} build -t ashes-ci-win:latest --build-arg BASE_IMAGE=ashes-ci-base:latest -f ci/images/Containerfile.win ci/images

# Populate runtimes/ with LLVM native libs (run after `just images`; re-run on LLVM bump).
# Caches the downloaded apt archives (incl. the large libLLVM .deb) in
# .ci-cache/apt so re-running provision reuses them instead of re-downloading.
provision:
    mkdir -p {{justfile_directory()}}/.ci-cache/apt/archives/partial
    {{engine}} run --rm --user root \
        -v {{justfile_directory()}}:/work:Z -w /work \
        -v {{justfile_directory()}}/.ci-cache/apt:/var/cache/apt:Z \
        ashes-ci-base:latest \
        bash -lc "scripts/download-llvm-native.sh --all"

# --- CI jobs ---------------------------------------------------------------

build:
    ci/jobs.sh build

fmt-check:
    ci/jobs.sh fmt_check

test:
    ci/jobs.sh test

coverage:
    ci/jobs.sh coverage

# Dependency freshness + vulnerabilities (local Dependabot stand-in). Gates on
# vulnerable NuGet packages and high+ pnpm advisories; reports outdated packages.
deps-check:
    ci/jobs.sh deps_check

# Static analysis / SAST via Semgrep (local CodeQL stand-in). C#, TS, secrets.
sast:
    ci/jobs.sh sast

ext:
    ci/jobs.sh ext

publish-cli:
    ci/jobs.sh publish_cli

matrix:
    ci/jobs.sh matrix

# Run the matrix for a single arch only (linux-x64 | linux-arm64 | win-x64);
# publishes just that RID first. Inner loop for iterating on one target.
matrix-one arch:
    ci/jobs.sh matrix_one {{arch}}

# Fast inner loop (build + test); used by the pre-commit hook.
ci-quick:
    ci/jobs.sh ci_quick

# Full PR-equivalent pipeline; used by the pre-push hook.
ci:
    ci/jobs.sh ci

# --- Release / CD ----------------------------------------------------------

# Build all release artifacts for version VERSION into artifacts/release/ (local).
release version:
    ci/jobs.sh release_build {{version}}

# Interactive GitHub release: cut release/X.Y.Z from origin/main, build the
# artifacts, tag vX.Y.Z, and publish a GitHub Release. Pass a version to
# pre-fill, e.g. `just release-github 1.2.3`. Requires gh (authenticated).
release-github *args:
    ci/jobs.sh release_github {{args}}

# --- Git hooks -------------------------------------------------------------

# Route git hooks to ci/hooks (commit-msg -> ban trailers, pre-commit -> ci-quick, pre-push -> ci).
install-hooks:
    git config core.hooksPath ci/hooks
    @echo "Hooks installed. Bypass with SKIP_CI=1 or 'git push --no-verify'."

# Revert to the default .git/hooks.
uninstall-hooks:
    git config --unset core.hooksPath
