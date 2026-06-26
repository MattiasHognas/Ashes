#!/usr/bin/env bash
# ci/lib/s3.sh — upload release artifacts to an S3-compatible target via `mc`.
#
# Defaults target the local MinIO container (`just minio-up`). To switch to AWS
# S3 later, point CI_S3_ENDPOINT at the AWS endpoint and supply real credentials
# in ci/.ci.env — no code change required.
#
# Provides:
#   s3_upload <local_dir> <dest_prefix>   recursively upload into $BUCKET/$prefix
set -euo pipefail

S3_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=ci/lib/run.sh
source "${S3_LIB_DIR}/run.sh"

# Optional local secrets file (git-ignored): exports CI_S3_* overrides.
if [[ -f "${CI_REPO_ROOT}/ci/.ci.env" ]]; then
  # shellcheck disable=SC1091
  source "${CI_REPO_ROOT}/ci/.ci.env"
fi

CI_S3_ENDPOINT="${CI_S3_ENDPOINT:-http://localhost:9000}"
CI_S3_BUCKET="${CI_S3_BUCKET:-ashes}"
CI_S3_ACCESS_KEY="${CI_S3_ACCESS_KEY:-minioadmin}"
CI_S3_SECRET_KEY="${CI_S3_SECRET_KEY:-minioadmin}"
CI_S3_ALIAS="${CI_S3_ALIAS:-ci}"

# s3_upload <local_dir> <dest_prefix>
# local_dir is interpreted relative to the repo root (mounted at /work).
s3_upload() {
  local local_dir="$1" dest_prefix="$2"
  local image
  image="$(ci_image_for base)"

  # --network=host so the default http://localhost:9000 reaches the MinIO
  # container's published port. Harmless for a public AWS endpoint too.
  "$CI_ENGINE" run --rm \
    --network=host \
    -v "${CI_REPO_ROOT}:/work:Z" \
    -w /work \
    -e MC_HOST_${CI_S3_ALIAS}="${CI_S3_ENDPOINT/:\/\//:\/\/${CI_S3_ACCESS_KEY}:${CI_S3_SECRET_KEY}@}" \
    "$image" \
    bash -lc "
      set -euo pipefail
      mc mb --ignore-existing '${CI_S3_ALIAS}/${CI_S3_BUCKET}'
      mc cp --recursive '${local_dir%/}/' '${CI_S3_ALIAS}/${CI_S3_BUCKET}/${dest_prefix%/}/'
      echo 'Uploaded ${local_dir} -> ${CI_S3_ENDPOINT}/${CI_S3_BUCKET}/${dest_prefix}'
    "
}
