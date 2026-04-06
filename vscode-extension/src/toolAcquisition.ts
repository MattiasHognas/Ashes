import * as fs from "fs";
import * as http from "http";
import * as https from "https";
import * as os from "os";
import * as path from "path";
import { execFileSync } from "child_process";

import * as vscode from "vscode";

const GITHUB_OWNER = "MattiasHognas";
const GITHUB_REPO = "Ashes";

/**
 * Maps { process.platform: { process.arch: .NET runtime identifier } }.
 * To add a new platform, extend this map only — no other code changes needed.
 */
const RID_MAP: Readonly<Record<string, Record<string, string>>> = {
  win32: { x64: "win-x64" },
  linux: { x64: "linux-x64", arm64: "linux-arm64" },
};

/** Resolve the .NET runtime identifier for the current platform/arch. */
export function getRid(): string {
  const rid = RID_MAP[process.platform]?.[process.arch];
  if (!rid) {
    throw new Error(
      `Unsupported platform: ${process.platform}/${process.arch}`,
    );
  }
  return rid;
}

/** Configuration for a downloadable tool. */
export interface ToolConfig {
  /** Human-readable name shown in progress notifications (e.g. "Ashes compiler"). */
  displayName: string;
  /** Release asset prefix — the zip is named `${assetPrefix}-${rid}.zip`. */
  assetPrefix: string;
  /** Base name of the executable (without `.exe`). */
  executableBaseName: string;
  /** Subdirectory under globalStorageUri for cached binaries. */
  cacheSubdir: string;
  /** Subdirectory under extensionPath for locally-bundled binaries. */
  bundledSubdir: string;
  /** VS Code setting key that overrides the tool path (e.g. "ashes.lspServerPath"). */
  settingKey: string;
}

/** Return the executable filename for a given base name and RID. */
export function getExecutableName(baseName: string, rid: string): string {
  return rid.startsWith("win") ? `${baseName}.exe` : baseName;
}

/**
 * Return the versioned cache path for a tool executable.
 * Layout: <globalStorage>/<cacheSubdir>/<version>/<rid>/<executable>
 */
export function getToolCachePath(
  context: vscode.ExtensionContext,
  config: ToolConfig,
  version: string,
  rid: string,
): string {
  return path.join(
    context.globalStorageUri.fsPath,
    config.cacheSubdir,
    version,
    rid,
    getExecutableName(config.executableBaseName, rid),
  );
}

/**
 * Return the optional path for a tool bundled inside the extension.
 * Layout: <extension>/<bundledSubdir>/<rid>/<executable>
 */
export function getBundledToolPath(
  context: vscode.ExtensionContext,
  config: ToolConfig,
  rid: string,
): string {
  return path.join(
    context.extensionPath,
    config.bundledSubdir,
    rid,
    getExecutableName(config.executableBaseName, rid),
  );
}

/** Follow redirects and download a URL to a local file. */
export function downloadToFile(url: string, destPath: string): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const follow = (u: string): void => {
      const lib = u.startsWith("https") ? https : http;
      lib
        .get(u, { headers: { "User-Agent": "vscode-ashes" } }, (res) => {
          const { statusCode, headers } = res;
          if (
            statusCode === 301 ||
            statusCode === 302 ||
            statusCode === 307 ||
            statusCode === 308
          ) {
            if (!headers.location) {
              reject(new Error("Redirect with no Location header"));
              return;
            }
            const redirectUrl = new URL(headers.location, u).toString();
            follow(redirectUrl);
            return;
          }
          if (statusCode !== 200) {
            reject(new Error(`HTTP ${statusCode ?? "?"} fetching ${u}`));
            return;
          }
          fs.mkdirSync(path.dirname(destPath), { recursive: true });
          const file = fs.createWriteStream(destPath);
          res.on("error", (err) => {
            file.close(() => {
              reject(err);
            });
          });
          res.pipe(file);
          file.on("finish", () =>
            file.close((err) => {
              if (err) {
                reject(err);
              } else {
                resolve();
              }
            }),
          );
          file.on("error", (err) => {
            file.close(() => {
              reject(err);
            });
          });
        })
        .on("error", reject);
    };
    follow(url);
  });
}

/**
 * Extract a zip archive into a destination directory.
 * On Linux/macOS: uses `unzip -j` to flatten any directory structure.
 * On Windows: uses PowerShell `Expand-Archive`.
 */
export function extractZip(zipPath: string, destDir: string): void {
  fs.mkdirSync(destDir, { recursive: true });
  if (process.platform === "win32") {
    const psEscape = (p: string): string => p.replace(/'/g, "''");
    execFileSync(
      "powershell.exe",
      [
        "-NoProfile",
        "-Command",
        `Expand-Archive -Force -LiteralPath '${psEscape(zipPath)}' -DestinationPath '${psEscape(destDir)}'`,
      ],
      { stdio: "pipe" },
    );
  } else {
    execFileSync("unzip", ["-j", "-o", zipPath, "-d", destDir], {
      stdio: "pipe",
    });
  }
}

/**
 * Run `<executable> --version` and verify the output matches the expected version.
 * Throws if the version does not match or the executable fails to run.
 */
export function verifyToolVersion(
  executablePath: string,
  expectedVersion: string,
  displayName: string,
): void {
  let output: string;
  try {
    output = execFileSync(executablePath, ["--version"], {
      encoding: "utf8",
      stdio: "pipe",
    }).trim();
  } catch (err) {
    throw new Error(
      `Failed to run ${displayName} for version check: ${(err as Error).message}`,
    );
  }
  if (output !== expectedVersion) {
    throw new Error(
      `${displayName} version mismatch: expected ${expectedVersion}, got ${output}`,
    );
  }
}

/**
 * Acquire a tool for the current platform.
 *
 * Resolution order:
 * 0. User-configured setting override (e.g. `ashes.lspServerPath`).
 * 1. Bundled binary inside the extension directory (local dev / offline).
 * 2. Cached binary in globalStorage from a previous download.
 * 3. Download from GitHub Releases, extract, verify, and cache.
 *
 * Returns the absolute path to the ready-to-use executable.
 */
export async function acquireTool(
  context: vscode.ExtensionContext,
  config: ToolConfig,
  requiredVersion: string,
): Promise<string> {
  // 0. Check user-configured setting override.
  const overridePath = vscode.workspace
    .getConfiguration()
    .get<string>(config.settingKey, "")
    .trim();
  if (overridePath) {
    if (!fs.existsSync(overridePath)) {
      throw new Error(
        `${config.displayName} override path does not exist: ${overridePath}`,
      );
    }
    return overridePath;
  }

  const rid = getRid();
  const bundledPath = getBundledToolPath(context, config, rid);
  const cachedPath = getToolCachePath(context, config, requiredVersion, rid);

  // 1. Try bundled binary (local dev).
  if (fs.existsSync(bundledPath)) {
    try {
      if (process.platform !== "win32") {
        fs.accessSync(bundledPath, fs.constants.X_OK);
      }
      verifyToolVersion(bundledPath, requiredVersion, config.displayName);
      return bundledPath;
    } catch {
      // Ignore invalid bundled artifacts and continue.
    }
  }

  // 2. Try previously-downloaded cached binary.
  if (fs.existsSync(cachedPath)) {
    try {
      if (process.platform !== "win32") {
        fs.accessSync(cachedPath, fs.constants.X_OK);
      }
      verifyToolVersion(cachedPath, requiredVersion, config.displayName);
      return cachedPath;
    } catch {
      // Stale or broken cache — re-download below.
    }
  }

  // 3. Download from GitHub Releases.
  const asset = `${config.assetPrefix}-${rid}.zip`;
  const downloadUrl = `https://github.com/${GITHUB_OWNER}/${GITHUB_REPO}/releases/download/v${requiredVersion}/${asset}`;
  const tmpZip = path.join(
    os.tmpdir(),
    `${config.assetPrefix}-${rid}-${requiredVersion}.zip`,
  );
  const destDir = path.dirname(cachedPath);

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: `Downloading ${config.displayName} v${requiredVersion}…`,
      cancellable: false,
    },
    async () => {
      try {
        await downloadToFile(downloadUrl, tmpZip).catch((err: unknown) => {
          const msg = (err as Error).message ?? String(err);
          throw new Error(
            `Failed to download ${asset} for v${requiredVersion} from ${downloadUrl}: ${msg}`,
          );
        });
        extractZip(tmpZip, destDir);
        if (process.platform !== "win32") {
          fs.chmodSync(cachedPath, 0o755);
        }
        verifyToolVersion(cachedPath, requiredVersion, config.displayName);
      } catch (err) {
        // Remove partial artifacts so a future attempt retries cleanly.
        try {
          fs.unlinkSync(tmpZip);
        } catch {
          // ignore
        }
        try {
          fs.rmSync(destDir, { recursive: true, force: true });
        } catch {
          // ignore
        }
        throw err;
      } finally {
        try {
          fs.unlinkSync(tmpZip);
        } catch {
          // ignore
        }
      }
    },
  );

  return cachedPath;
}
