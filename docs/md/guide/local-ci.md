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
./scripts/init-local-ci.sh          # install deps + build images + provision
./scripts/init-local-ci.sh --all    # also install git hooks
```

This installs the host prerequisites (Podman + rootless plumbing, `just`),
ensures rootless subuid/subgid mappings, builds the runner images, and
provisions the LLVM libs. It is idempotent — safe to re-run. Once `just` is
installed you can also re-run it as `just init` (e.g. `just init --skip-deps`).

> Prefer Docker? Set `CI_ENGINE=docker` (env var).

## Manual setup (what init does)

Install once (CachyOS/Arch):

```sh
sudo pacman -S podman just slirp4netns fuse-overlayfs shadow \
    qemu-user-static qemu-user-static-binfmt   # last two: arm64 leg
podman info >/dev/null && echo OK     # verify rootless works
cat /proc/sys/fs/binfmt_misc/qemu-aarch64   # want: flags: ...F
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

| Command                  | What it does                                                                                     | Mirrors               |
| ------------------------ | ------------------------------------------------------------------------------------------------ | --------------------- |
| `just ci-quick`          | build + .NET/LSP tests (fast inner loop)                                                         | —                     |
| `just ci`                | build, format check, tests, deps, sast, VS Code ext + integration, publish, 3-arch matrix        | `pull-request.yaml`   |
| `just build`             | `dotnet restore` + `build -c Release`                                                            | Restore/Build         |
| `just fmt-check`         | `dotnet format --verify-no-changes`                                                              | Verify formatting     |
| `just test`              | `Ashes.Tests` + `Ashes.Lsp.Tests`                                                                | Run tests             |
| `just coverage`          | tests with cobertura coverage                                                                    | `push-to-main.yaml`   |
| `just deps-check`        | NuGet + pnpm vulnerability/outdated check (local Dependabot)                                     | —                     |
| `just sast`              | Semgrep static analysis: C#, TS, secrets (local CodeQL)                                          | —                     |
| `just ext`               | extension lint/format/compile + xvfb integration tests                                           | extension steps       |
| `just docs`              | documentation site build (`docs/builder` → `docs/site`); fails on dead links                     | `docs.yml` build      |
| `just publish-cli`       | self-contained CLI for all 3 RIDs into `artifacts/ashes/<rid>`                                   | Publish CLI           |
| `just matrix`            | run examples + tests + fmt-verify on x64 / arm64(qemu) / win(wine)                               | `test-matrix`         |
| `just matrix-one <arch>` | run the matrix for a single arch (`linux-x64`/`linux-arm64`/`win-x64`), publishing just that RID | one `test-matrix` leg |
| `just release VER`       | build release artifacts into `artifacts/release/` (local disk only)                              | `release.yml` build   |
| `just release-github`    | interactive: branch + build + tag + GitHub Release                                               | `release.yml`         |

The matrix skips the network examples (`http_get`, `https_get`, `tcp_*`), same as
GitHub. It is fail-fast:false — all three arches run, then it fails if any did.

To iterate on a single target, `just matrix-one <arch>` runs just that leg
(`linux-x64`, `linux-arm64`, or `win-x64`) and publishes only that RID first. It
runs the same per-leg body as the full matrix (including the arm64 emulation
env); fmt stability is arch-independent and is only verified by the full
`just matrix`.

### linux-arm64 runs the full suite under emulation

All three legs run the **full** suite (examples + tests) — there is no compile-only
mode. The arm64 leg mirrors GitHub's native `ubuntu-24.04-arm` leg, only emulated:
its runner image (`ashes-ci-arm64`) is a **genuine aarch64 image** (built
`--platform linux/arm64` from .NET's `runtime-deps`), so every binary inside it —
`bash`, `git`, the arm64 `ashes`, and the arm64 programs `ashes` itself compiles and
execs — is aarch64 and is run transparently by the host's **qemu-user-static
binfmt_misc handler**.

This works only because that handler is registered with the **`F` (fix-binary)**
flag: the kernel opens `/usr/bin/qemu-aarch64-static` at registration time and holds
the fd, so emulation reaches into the container (which ships no qemu of its own) and —
crucially — survives nested `exec`. An explicit `qemu-aarch64 <bin>` wrapper, by
contrast, can't follow the compiler's `exec` of its output — which is why the earlier
design was compile-only.

`scripts/init-local-ci.sh` installs `qemu-user-static` + binfmt and verifies the
handler. Check it yourself:

```sh
cat /proc/sys/fs/binfmt_misc/qemu-aarch64    # want: flags: ...F
```

The arm64 leg disables tiered compilation (`DOTNET_TieredCompilation=0`): qemu-user
mis-emulates the JIT's multi-threaded on-stack replacement, which would SIGSEGV the
emulated compiler. The native x64 leg exercises the tiered JIT.

## Triggers

```sh
just install-hooks     # core.hooksPath = ci/hooks
```

- **pre-commit** → `just ci-quick` (keeps commits fast)
- **pre-push** → `just ci` (full gate before code leaves the machine)

Bypass either with `SKIP_CI=1 git commit ...` / `git push --no-verify`. Revert
with `just uninstall-hooks`.

## Release / CD

Artifacts are always built to **local disk**; only a GitHub release additionally
pushes them to GitHub. Both paths share the same build (`ci/jobs.sh release_build`),
which stages and zips the 9 binary artifacts plus the `.vsix` into
`artifacts/release/` (`dist/` is reserved for `scripts/publish.sh`'s per-target copies).

### Build locally

```sh
just release 1.2.3     # build CLI/LSP/DAP (3 RIDs) + vsix into artifacts/release/
```

Nothing is pushed anywhere — inspect or hand-distribute the zips from
`artifacts/release/`.

### GitHub Release (interactive)

```sh
just release-github          # prompt for the version, then release
just release-github 1.2.3    # pre-fill the version (still confirms)
```

`just release-github` (the `release_github` job in `ci/jobs.sh`) is the local
stand-in for the disabled `.github/workflows/release.yml`. It:

1. prompts for and validates a semver version (suggesting the next patch),
2. cuts a `release/X.Y.Z` branch from `origin/main`,
3. stamps the version into `vscode-extension/package.json` and **commits** it on
   the release branch ("Update version to X.Y.Z"), so the tag points at source
   matching the released version,
4. builds the 9 binary artifacts + `.vsix` into `artifacts/release/` with the version embedded,
5. tags `vX.Y.Z`, pushes the branch and tag,
6. publishes a GitHub Release with the same artifact set `release.yml` attaches
   (release notes are left empty for now),
7. **if `VSCE_PAT` is set**, publishes the just-built `.vsix` to the VS Code
   Marketplace (publisher `mattiashognas`) as the final step.

**Resume:** re-running `just release-github X.Y.Z` for a version whose release is
already complete (tag on the remote + GitHub Release) does not fail — it detects
the existing release and **resumes the Marketplace publish alone**, using the
local `artifacts/release/*.vsix` if present or downloading the exact asset from
the Release. `VSCE_PAT` is required in this mode (that is the only step left).
Partial states (a local-only tag, a remote tag without a Release) still fail
loudly and need manual cleanup.

The version-bump commit lives on the `release/X.Y.Z` branch only; `main` picks it
up when that branch is merged back (e.g. a PR). The branch, commit, and tag are
created locally and only pushed after a successful build, so a failed build never
leaves dangling `release/*` refs — or a premature version bump — on the remote.
Requires `git`, `gh` (authenticated, `gh auth status`), `pnpm`, and a provisioned
build env (`just images && just provision`).

### Publishing to the VS Code Marketplace

Marketplace publish is the last step and is **off unless `VSCE_PAT` is set**, so a
publish failure can never strand the already-live tag/release. It uploads the
exact `.vsix` attached to the GitHub Release — no rebuild. The extension
manifest's `publisher` (`mattiashognas`) must match your Marketplace publisher.

Get a token from <https://dev.azure.com> → User settings → **Personal access
tokens** → New token, scope **Marketplace ▸ Manage** (the org can be any; the
token is org-agnostic for Marketplace). Then:

```sh
VSCE_PAT=xxxxxxxx just release-github 1.2.3
```

To publish an already-released `.vsix` after the fact (e.g. the release ran
without a token, or the publish failed), just re-run the release command — it
resumes the Marketplace step alone:

```sh
VSCE_PAT=xxxxxxxx just release-github 1.2.3
```

## Layout

```text
justfile                 # entrypoint: just <recipe>
ci/
  images/Containerfile.* # base (linux-x64) + arm64 + win runner images
  lib/run.sh             # run_in <runner> <cmd> — podman run with repo mounted
  jobs.sh                # job implementations (build/test/ext/matrix/release_*)
  hooks/{pre-commit,pre-push}
scripts/
  init-local-ci.sh       # one-command bootstrap (just init)
```

## Dependencies & static analysis

These replace the GitHub-hosted Dependabot/CodeQL checks with local equivalents
that run in the same Podman runner. Both need network access (advisory DBs /
Semgrep rule packs), so they're part of `just ci` (pre-push) but not the offline
`just ci-quick` (pre-commit).

- `just deps-check` — the local **Dependabot** stand-in. **Gates** on
  known-vulnerable NuGet packages (`dotnet list package --vulnerable
--include-transitive`) and pnpm advisories of any severity (`pnpm audit
--audit-level low`), so both ecosystems fail the build alike — the NuGet
  gate has no severity floor and the pnpm gate matches it. Both pnpm
  projects are covered: `vscode-extension/` and `docs/builder/`. Outdated
  listings (`dotnet list package --outdated`,
  `pnpm outdated`) are printed for information only — they don't fail the build.
  Dependabot is still useful on GitHub for _opening_ update PRs; this only
  _checks_. For local auto-PRs, self-hosted Renovate is the next step.
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
- **Documentation site deployment** is the one GitHub-hosted piece:
  `.github/workflows/docs.yml` builds `docs/builder` with `DOCS_BASE=/Ashes/` and
  publishes to **GitHub Pages** on pushes to `main` that touch `docs/**` or the
  Ashes TextMate grammar. Pages must be enabled once in the repo settings
  (Settings → Pages → Source: *GitHub Actions*). `just docs` is the local build
  gate for the same site (without the base path or deployment).

## Troubleshooting

- **`run_in: unknown runner` / image not found** — run `just images` first.
- **arm64/win matrix fails to start the binary** — ensure `just provision` ran so
  the published binaries bundle the right `libLLVM`.
- **arm64 leg fails with `Exec format error`** — the host's `qemu-aarch64`
  binfmt_misc handler is missing or lacks the `F` flag. Run `scripts/init-local-ci.sh`
  (or `just init`) and check `cat /proc/sys/fs/binfmt_misc/qemu-aarch64` shows
  `flags: ...F`.
- **`gh` release fails to authenticate** — run `gh auth status`; `just release-github`
  needs an authenticated `gh`.
- **Permission/ownership oddities in the repo** — Podman runs with
  `--userns=keep-id`; with Docker set `CI_ENGINE=docker` and expect root-owned
  build outputs, or run Docker rootless.
