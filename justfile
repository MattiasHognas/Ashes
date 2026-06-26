# Local containerized CI/CD for Ashes — see docs/LOCAL_CI.md.
#
# Quick start:
#   just images          # build the three runner images (once)
#   just provision       # fetch LLVM native libs into runtimes/ (once / on bump)
#   just ci-quick        # fast build + test
#   just ci              # full PR-equivalent pipeline
#   just minio-up        # start local S3 (MinIO)
#   just release 1.2.3   # publish artifacts and upload to S3
#   just install-hooks   # wire pre-commit (ci-quick) + pre-push (ci)

engine     := env_var_or_default("CI_ENGINE", "podman")
node_major := env_var_or_default("NODE_MAJOR", "26")
minio_user := env_var_or_default("CI_S3_ACCESS_KEY", "minioadmin")
minio_pass := env_var_or_default("CI_S3_SECRET_KEY", "minioadmin")

# List available recipes.
default:
    @just --list

# Bootstrap a fresh machine: install deps, build images, provision runtimes.
# Pass flags through, e.g. `just init --all`. See scripts/init-local-ci.sh.
init *args:
    scripts/init-local-ci.sh {{args}}

# --- Container images ------------------------------------------------------

# Build the base, arm64, and win runner images.
images:
    {{engine}} build -t ashes-ci-base:latest --build-arg NODE_MAJOR={{node_major}} -f ci/images/Containerfile.base ci/images
    {{engine}} build -t ashes-ci-arm64:latest --build-arg BASE_IMAGE=ashes-ci-base:latest -f ci/images/Containerfile.arm64 ci/images
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

ext:
    ci/jobs.sh ext

publish-cli:
    ci/jobs.sh publish_cli

matrix:
    ci/jobs.sh matrix

# Fast inner loop (build + test); used by the pre-commit hook.
ci-quick:
    ci/jobs.sh ci_quick

# Full PR-equivalent pipeline; used by the pre-push hook.
ci:
    ci/jobs.sh ci

# --- Release / CD ----------------------------------------------------------

# Publish all artifacts for version VERSION and upload to S3 (releases/VERSION/).
release version:
    ci/jobs.sh release {{version}}

# --- MinIO (local S3) ------------------------------------------------------

# Start MinIO (S3 API :9000, console :9001) with a persistent data volume.
minio-up:
    {{engine}} volume create ashes-minio-data >/dev/null
    -{{engine}} rm -f ashes-minio >/dev/null 2>&1
    {{engine}} run -d --name ashes-minio \
        -p 9000:9000 -p 9001:9001 \
        -v ashes-minio-data:/data \
        -e MINIO_ROOT_USER={{minio_user}} \
        -e MINIO_ROOT_PASSWORD={{minio_pass}} \
        quay.io/minio/minio server /data --console-address ":9001"
    @echo "MinIO API:     http://localhost:9000"
    @echo "MinIO console: http://localhost:9001  ({{minio_user}} / {{minio_pass}})"

# Stop and remove the MinIO container (data volume is kept).
minio-down:
    -{{engine}} rm -f ashes-minio

# --- Git hooks -------------------------------------------------------------

# Route git hooks to ci/hooks (pre-commit -> ci-quick, pre-push -> ci).
install-hooks:
    git config core.hooksPath ci/hooks
    @echo "Hooks installed. Bypass with SKIP_CI=1 or 'git push --no-verify'."

# Revert to the default .git/hooks.
uninstall-hooks:
    git config --unset core.hooksPath
