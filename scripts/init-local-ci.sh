#!/usr/bin/env bash
# init-local-ci.sh — bootstrap local CI/CD on a fresh machine.
#
# Installs the host prerequisites (Podman + rootless plumbing, just), ensures
# rootless user-namespace mappings exist, builds the runner images, and
# provisions the LLVM native libs. Optionally installs the git hooks and starts
# MinIO. Idempotent — safe to re-run. Also runnable as `just init`.
#
# Usage:
#   ./scripts/init-local-ci.sh                # deps + images + provision
#   ./scripts/init-local-ci.sh --with-hooks   # also wire pre-commit/pre-push
#   ./scripts/init-local-ci.sh --with-minio   # also start local S3 (MinIO)
#   ./scripts/init-local-ci.sh --all          # everything
#   ./scripts/init-local-ci.sh --skip-deps    # assume podman/just already installed
#
# Flags: --skip-deps --skip-images --skip-provision --with-hooks --with-minio --all -h
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

CI_ENGINE="${CI_ENGINE:-podman}"

# --- options ---------------------------------------------------------------
do_deps=1 do_images=1 do_provision=1 do_hooks=0 do_minio=0
for arg in "$@"; do
  case "$arg" in
    --skip-deps) do_deps=0 ;;
    --skip-images) do_images=0 ;;
    --skip-provision) do_provision=0 ;;
    --with-hooks) do_hooks=1 ;;
    --with-minio) do_minio=1 ;;
    --all) do_hooks=1; do_minio=1 ;;
    -h | --help)
      sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) echo "init.sh: unknown option '$arg' (try --help)" >&2; exit 1 ;;
  esac
done

# --- logging ---------------------------------------------------------------
if [[ -t 1 ]]; then B=$'\033[1m'; G=$'\033[32m'; Y=$'\033[33m'; R=$'\033[31m'; N=$'\033[0m'; else B= G= Y= R= N=; fi
step() { echo "${B}==>${N} $*"; }
ok()   { echo "  ${G}ok${N} $*"; }
warn() { echo "  ${Y}warn${N} $*" >&2; }
die()  { echo "${R}error${N} $*" >&2; exit 1; }

have() { command -v "$1" >/dev/null 2>&1; }

# sudo wrapper (no-op when already root).
SUDO=""
if [[ "$(id -u)" -ne 0 ]]; then
  if have sudo; then SUDO="sudo"; else SUDO=""; fi
fi
need_root() { [[ -n "$SUDO" || "$(id -u)" -eq 0 ]]; }

# --- host dependencies -----------------------------------------------------
install_deps() {
  step "Installing host prerequisites (podman, just, rootless plumbing)"

  if have pacman; then
    need_root || die "need root/sudo to install packages"
    $SUDO pacman -S --needed --noconfirm podman just slirp4netns fuse-overlayfs shadow || die "pacman install failed"
  elif have apt-get; then
    need_root || die "need root/sudo to install packages"
    $SUDO apt-get update
    $SUDO apt-get install -y --no-install-recommends podman slirp4netns fuse-overlayfs uidmap || die "apt install failed"
    # `just` is often absent from apt; fall back to the official installer below.
  elif have dnf; then
    need_root || die "need root/sudo to install packages"
    $SUDO dnf install -y podman slirp4netns fuse-overlayfs shadow-utils just || warn "dnf install partial"
  else
    warn "no supported package manager (pacman/apt/dnf) found; install podman + just manually, then re-run with --skip-deps"
    return
  fi

  # Ensure `just` exists (cross-distro fallback to the official install script).
  if ! have just; then
    step "Installing 'just' to ~/.local/bin via just.systems"
    mkdir -p "$HOME/.local/bin"
    curl --proto '=https' --tlsv1.2 -sSf https://just.systems/install.sh | bash -s -- --to "$HOME/.local/bin" \
      || die "could not install just; install it manually and re-run with --skip-deps"
    have just || warn "'$HOME/.local/bin' is not on PATH — add it (e.g. fish: fish_add_path ~/.local/bin)"
  fi
  ok "host prerequisites present"
}

# --- rootless user namespace mappings --------------------------------------
ensure_subid() {
  step "Checking rootless subuid/subgid mappings for $USER"
  if grep -q "^${USER}:" /etc/subuid 2>/dev/null && grep -q "^${USER}:" /etc/subgid 2>/dev/null; then
    ok "subuid/subgid already configured"
    return
  fi
  warn "no subuid/subgid range for $USER — adding 100000-165535"
  need_root || die "need root/sudo to add subuid/subgid ranges"
  $SUDO usermod --add-subuids 100000-165535 --add-subgids 100000-165535 "$USER" \
    || die "failed to add subuid/subgid ranges"
  $CI_ENGINE system migrate >/dev/null 2>&1 || true
  ok "subuid/subgid ranges added"
}

# --- verify engine ---------------------------------------------------------
verify_engine() {
  step "Verifying rootless $CI_ENGINE"
  have "$CI_ENGINE" || die "$CI_ENGINE not found on PATH (re-run without --skip-deps?)"
  "$CI_ENGINE" info >/dev/null 2>&1 || die "$CI_ENGINE info failed — rootless setup is incomplete"
  ok "$CI_ENGINE works rootless"
}

# --- main ------------------------------------------------------------------
[[ "$do_deps" == 1 ]] && install_deps
ensure_subid
verify_engine

if [[ "$do_images" == 1 ]]; then
  step "Building runner images (just images)"
  just images
  ok "images built"
fi

if [[ "$do_provision" == 1 ]]; then
  step "Provisioning LLVM native libs (just provision)"
  just provision
  ok "runtimes/ provisioned"
fi

if [[ "$do_hooks" == 1 ]]; then
  step "Installing git hooks (just install-hooks)"
  just install-hooks
fi

if [[ "$do_minio" == 1 ]]; then
  step "Starting MinIO (just minio-up)"
  just minio-up
fi

echo
echo "${G}${B}Done.${N} Try:  ${B}just ci-quick${N}   (fast)   or   ${B}just ci${N}   (full pipeline)"
[[ "$do_hooks" == 0 ]] && echo "      Enable git triggers with: ${B}just install-hooks${N}"
[[ "$do_minio" == 0 ]] && echo "      Start local S3 with:       ${B}just minio-up${N}  then  ${B}just release <ver>${N}"
