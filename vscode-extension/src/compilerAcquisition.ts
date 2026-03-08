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
  linux: { x64: "linux-x64" },
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

/** Return the executable filename for a given RID. */
export function getExecutableName(rid: string): string {
  return rid.startsWith("win") ? "ashes.exe" : "ashes";
}

/**
 * Return the versioned cache path for the compiler executable.
 * Layout: <globalStorage>/toolchain/<version>/<rid>/<executable>
 */
export function getCompilerPath(
  context: vscode.ExtensionContext,
  version: string,
  rid: string,
): string {
  return path.join(
    context.globalStorageUri.fsPath,
    "toolchain",
    version,
    rid,
    getExecutableName(rid),
  );
}

/**
 * Return the optional compiler path bundled inside the extension.
 * Layout: <extension>/compiler/<rid>/<executable>
 */
export function getBundledCompilerPath(
  context: vscode.ExtensionContext,
  rid: string,
): string {
  return path.join(
    context.extensionPath,
    "compiler",
    rid,
    getExecutableName(rid),
  );
}

/** Follow redirects and download a URL to a local file. */
function downloadToFile(url: string, destPath: string): Promise<void> {
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
            follow(headers.location);
            return;
          }
          if (statusCode !== 200) {
            reject(new Error(`HTTP ${statusCode ?? "?"} fetching ${u}`));
            return;
          }
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
 * Extract the compiler executable from the release zip.
 * The zip places executables at the root (preferred layout per spec).
 * On Linux/macOS: uses `unzip -j` to flatten any directory structure.
 * On Windows: uses PowerShell `Expand-Archive`.
 */
function extractZip(zipPath: string, destDir: string): void {
  fs.mkdirSync(destDir, { recursive: true });
  if (process.platform === "win32") {
    // Single-quote PowerShell paths and escape embedded single quotes
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
    // -j: junk directory names (flatten), -o: overwrite without prompt
    // execFileSync avoids shell interpretation of paths
    execFileSync("unzip", ["-j", "-o", zipPath, "-d", destDir], {
      stdio: "pipe",
    });
  }
}

/**
 * Run `ashes --version` and verify the output matches the expected version.
 * Throws if the version does not match or the executable fails to run.
 */
function verifyCompilerVersion(
  executablePath: string,
  expectedVersion: string,
): void {
  let output: string;
  try {
    output = execFileSync(executablePath, ["--version"], {
      encoding: "utf8",
      stdio: "pipe",
    }).trim();
  } catch (err) {
    throw new Error(
      `Failed to run compiler for version check: ${(err as Error).message}`,
    );
  }
  if (output !== expectedVersion) {
    throw new Error(
      `Compiler version mismatch: expected ${expectedVersion}, got ${output}`,
    );
  }
}

/**
 * Acquire the Ashes compiler for the current platform.
 *
 * - If a cached binary exists for the required version, returns it immediately.
 * - Otherwise downloads the matching release asset from GitHub Releases,
 *   extracts it, verifies the version, and caches it.
 *
 * Returns the absolute path to the ready-to-use compiler executable.
 */
export async function acquireCompiler(
  context: vscode.ExtensionContext,
  requiredVersion: string,
): Promise<string> {
  const rid = getRid();
  const bundledCompilerPath = getBundledCompilerPath(context, rid);
  const compilerPath = getCompilerPath(context, requiredVersion, rid);

  if (fs.existsSync(bundledCompilerPath)) {
    try {
      if (process.platform !== "win32") {
        fs.accessSync(bundledCompilerPath, fs.constants.X_OK);
      }
      verifyCompilerVersion(bundledCompilerPath, requiredVersion);
      return bundledCompilerPath;
    } catch {
      // Ignore invalid bundled artifacts and continue to cached/downloaded acquisition.
    }
  }

  if (fs.existsSync(compilerPath)) {
    try {
      if (process.platform !== "win32") {
        fs.accessSync(compilerPath, fs.constants.X_OK);
      }
      verifyCompilerVersion(compilerPath, requiredVersion);
      return compilerPath;
    } catch {
      // If the cached compiler is not executable or has the wrong version,
      // ignore it and fall through to re-download.
    }
  }

  const asset = `ashes-${rid}.zip`;
  const downloadUrl = `https://github.com/${GITHUB_OWNER}/${GITHUB_REPO}/releases/download/v${requiredVersion}/${asset}`;
  const tmpZip = path.join(os.tmpdir(), `ashes-${rid}-${requiredVersion}.zip`);
  const destDir = path.dirname(compilerPath);

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: `Downloading Ashes compiler v${requiredVersion}…`,
      cancellable: false,
    },
    async () => {
      try {
        await downloadToFile(downloadUrl, tmpZip).catch((err: unknown) => {
          // Wrap with context so users can diagnose missing releases/assets
          const msg = (err as Error).message ?? String(err);
          throw new Error(
            `Failed to download ${asset} for v${requiredVersion}: ${msg}`,
          );
        });
        extractZip(tmpZip, destDir);
        if (process.platform !== "win32") {
          fs.chmodSync(compilerPath, 0o755);
        }
        verifyCompilerVersion(compilerPath, requiredVersion);
      } catch (err) {
        // Remove partial artifacts so a future attempt retries cleanly
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

  return compilerPath;
}
