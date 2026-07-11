#!/usr/bin/env bash
# ci/lib/run.sh — shared helpers for running CI jobs inside Podman containers.
#
# Source this file; it provides:
#   run_in <runner> <cmd...>   run a command in a runner image with the repo mounted
#   CI_REPO_ROOT               absolute path to the repo root
#
# Runners: base | arm64 | win  (map to the ashes-ci-<runner> images).
set -euo pipefail

CI_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CI_REPO_ROOT="$(cd "${CI_LIB_DIR}/../.." && pwd)"

# Container engine — Podman by default; overridable (e.g. CI_ENGINE=docker).
CI_ENGINE="${CI_ENGINE:-podman}"

# Image name prefix and tag.
CI_IMAGE_PREFIX="${CI_IMAGE_PREFIX:-ashes-ci}"
CI_IMAGE_TAG="${CI_IMAGE_TAG:-latest}"

# Host-side cache dirs so NuGet and pnpm caches survive between runs (fast inner
# loop). Bind mounts (not named volumes) inherit the caller's UID under
# --userns=keep-id, avoiding root-owned/unwritable cache permission issues.
CI_CACHE_DIR="${CI_CACHE_DIR:-${CI_REPO_ROOT}/.ci-cache}"

ci_image_for() {
  case "$1" in
    base | arm64 | win) printf '%s-%s:%s' "$CI_IMAGE_PREFIX" "$1" "$CI_IMAGE_TAG" ;;
    *)
      echo "run_in: unknown runner '$1' (expected base|arm64|win)" >&2
      return 1
      ;;
  esac
}

# run_in <runner> <cmd...>
# Executes the command (a single string, run via bash -lc) inside the runner
# image with the repo bind-mounted at /work and the caller's UID preserved.
run_in() {
  local runner="$1"
  shift
  local image
  image="$(ci_image_for "$runner")"

  local userns_args=()
  if [[ "$CI_ENGINE" == "podman" ]]; then
    userns_args=(--userns=keep-id)
  fi

  mkdir -p "${CI_CACHE_DIR}/nuget" "${CI_CACHE_DIR}/pnpm"

  # LD_LIBRARY_PATH points the native loader at the mounted runtimes/ so
  # framework-dependent `dotnet run` (tests/coverage, no RID) can resolve
  # libLLVM.so. On a host it comes from system packages; in the container it
  # only exists under runtimes/linux-x64. Self-contained publishes
  # (matrix/ext/release) bundle their own copies, so this is harmless for them.
  "$CI_ENGINE" run --rm \
    "${userns_args[@]}" \
    -v "${CI_REPO_ROOT}:/work:Z" \
    -v "${CI_CACHE_DIR}/nuget:/home/ci/.nuget:Z" \
    -v "${CI_CACHE_DIR}/pnpm:/home/ci/.local/share/pnpm:Z" \
    -w /work \
    -e HOME=/home/ci \
    -e PNPM_HOME=/home/ci/.local/share/pnpm \
    -e CI=1 \
    -e LD_LIBRARY_PATH=/work/runtimes/linux-x64 \
    "$image" \
    bash -lc "$*"
}
