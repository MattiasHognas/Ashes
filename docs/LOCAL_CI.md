# Local CI/CD

Run the full Ashes CI/CD pipeline locally in containers — no GitHub required.
Jobs run in rootless **Podman** images, driven by a `justfile`, and reproduce the
steps in `.github/workflows/{pull-request,push-to-main,release}.yaml`.

## Why

- GitHub Actions runs are slow and tie CI to GitHub.
- Local runs give a fast inner loop and decouple CI from the git host.
- Each architecture runs in its own image: **linux-x64** natively, **linux-arm64**
  under `qemu`, **win-x64** under `wine`.

## Fresh machine — one command

```sh
./scripts/init-local-ci.sh        # install deps + build images + provision
./scripts/init-local-ci.sh --all  # also install git hooks + start MinIO
```

This installs the host prerequisites (Podman + rootless plumbing, `just`),
ensures rootless subuid/subgid mappings, builds the runner images, and
provisions the LLVM libs. It is idempotent — safe to re-run. Once `just` is
installed you can also re-run it as `just init` (e.g. `just init --skip-deps`).

> Prefer Docker? Set `CI_ENGINE=docker` (env or `ci/.ci.env`).

## Manual setup (what init does)

Install once (CachyOS/Arch):

```sh
sudo pacman -S podman just slirp4netns fuse-overlayfs shadow
podman info >/dev/null && echo OK     # verify rootless works
```

Then build the images and fetch the native libs:

```sh
just images       # build ashes-ci-{base,arm64,win}
just provision    # download LLVM native libs into runtimes/
```

`just provision` runs `scripts/download-llvm-native.sh --all` inside the base
image (Debian, so `apt` works) and writes the libs into the bind-mounted
`runtimes/`. Re-run it only when the LLVM version changes. The build copies
`runtimes/<rid>/libLLVM.*` + `librustls.*` into publish output at publish time
(see `Directory.Build.targets`), so these must be present before building.

## Everyday commands

| Command            | What it does                                                        | Mirrors |
|--------------------|---------------------------------------------------------------------|---------|
| `just ci-quick`    | build + .NET/LSP tests (fast inner loop)                            | —       |
| `just ci`          | build, format check, tests, deps, sast, VS Code ext + integration, publish, 3-arch matrix | `pull-request.yaml` |
| `just build`       | `dotnet restore` + `build -c Release`                              | Restore/Build |
| `just fmt-check`   | `dotnet format --verify-no-changes`                               | Verify formatting |
| `just test`        | `Ashes.Tests` + `Ashes.Lsp.Tests`                                 | Run tests |
| `just coverage`    | tests with cobertura coverage                                      | `push-to-main.yaml` |
| `just deps-check`  | NuGet + pnpm vulnerability/outdated check (local Dependabot)       | — |
| `just sast`        | Semgrep static analysis: C#, TS, secrets (local CodeQL)           | — |
| `just ext`         | extension lint/format/compile + xvfb integration tests            | extension steps |
| `just publish-cli` | self-contained CLI for all 3 RIDs into `artifacts/ashes/<rid>`    | Publish CLI |
| `just matrix`      | run examples + tests + fmt-verify on x64 / arm64(qemu) / win(wine) | `test-matrix` |

The matrix skips the network examples (`http_get`, `https_get`, `tcp_*`), same as
GitHub. It is fail-fast:false — all three arches run, then it fails if any did.

## Triggers

```sh
just install-hooks     # core.hooksPath = ci/hooks
```

- **pre-commit** → `just ci-quick` (keeps commits fast)
- **pre-push** → `just ci` (full gate before code leaves the machine)

Bypass either with `SKIP_CI=1 git commit ...` / `git push --no-verify`. Revert
with `just uninstall-hooks`.

## Release / CD

```sh
just minio-up          # start local S3 (MinIO): API :9000, console :9001
just release 1.2.3     # publish CLI/LSP/DAP (3 RIDs) + vsix, zip into dist/, upload
```

`just release` reproduces `release.yml`: it stages and zips the 9 binary
artifacts plus the `.vsix` into `dist/`, then uploads them to
`s3://<bucket>/releases/<version>/`. Browse them at http://localhost:9001
(default `minioadmin` / `minioadmin`).

### Switching to AWS S3 later

Copy `ci/.ci.env.example` → `ci/.ci.env` (git-ignored) and set:

```sh
CI_S3_ENDPOINT=https://s3.<region>.amazonaws.com
CI_S3_BUCKET=your-bucket
CI_S3_ACCESS_KEY=...
CI_S3_SECRET_KEY=...
```

No code changes — the same `mc`-based uploader (`ci/lib/s3.sh`) is used.

## Layout

```
justfile                 # entrypoint: just <recipe>
ci/
  images/Containerfile.* # base (linux-x64) + arm64 + win runner images
  lib/run.sh             # run_in <runner> <cmd> — podman run with repo mounted
  lib/s3.sh              # mc-based S3/MinIO uploader
  jobs.sh                # job implementations (build/test/ext/matrix/release)
  hooks/{pre-commit,pre-push}
  .ci.env.example        # template for local S3 overrides
```

## Dependencies & static analysis

These replace the GitHub-hosted Dependabot/CodeQL checks with local equivalents
that run in the same Podman runner. Both need network access (advisory DBs /
Semgrep rule packs), so they're part of `just ci` (pre-push) but not the offline
`just ci-quick` (pre-commit).

- `just deps-check` — the local **Dependabot** stand-in. **Gates** on
  known-vulnerable NuGet packages (`dotnet list package --vulnerable
  --include-transitive`) and high/critical pnpm advisories (`pnpm audit
  --audit-level high`). Outdated listings (`dotnet list package --outdated`,
  `pnpm outdated`) are printed for information only — they don't fail the build.
  Dependabot is still useful on GitHub for *opening* update PRs; this only
  *checks*. For local auto-PRs, self-hosted Renovate is the next step.
- `just sast` — the local **CodeQL** stand-in, via **Semgrep**. Scans C#, TS/JS,
  and for leaked secrets (registry packs `p/security-audit`, `p/csharp`,
  `p/typescript`, `p/secrets`) and **fails on findings** (`--error`). Build
  outputs (`artifacts/`, `dist/`, `runtimes/`, `node_modules/`, …) are excluded.

## Notes

- **CodeQL** (`codeql.yml`) is disabled; `just sast` (Semgrep) is the local
  replacement. Re-enable the workflow if you want results in GitHub's Security tab.
- Windows binaries are smoke-tested under **Wine**; true native Windows runs
  remain on GitHub if ever needed.
- The GitHub workflows are left intact, so you can run both during the transition.

## Troubleshooting

- **`run_in: unknown runner` / image not found** — run `just images` first.
- **arm64/win matrix fails to start the binary** — ensure `just provision` ran so
  the published binaries bundle the right `libLLVM`.
- **Upload can't reach MinIO** — `just minio-up` must be running; `s3.sh` uses
  `--network=host` so `http://localhost:9000` resolves.
- **Permission/ownership oddities in the repo** — Podman runs with
  `--userns=keep-id`; with Docker set `CI_ENGINE=docker` and expect root-owned
  build outputs, or run Docker rootless.
```
