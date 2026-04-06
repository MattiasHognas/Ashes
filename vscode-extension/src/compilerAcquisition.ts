/**
 * Compiler acquisition — thin wrapper around the generic tool acquisition module.
 *
 * Re-exports helpers that other modules (commands.ts) already import from here
 * so existing call-sites remain unchanged.
 */

import * as vscode from "vscode";
import {
  type ToolConfig,
  acquireTool,
  getRid,
  getExecutableName,
  getToolCachePath,
  getBundledToolPath,
} from "./toolAcquisition";

export { getRid };

const COMPILER_CONFIG: ToolConfig = {
  displayName: "Ashes compiler",
  assetPrefix: "ashes",
  executableBaseName: "ashes",
  cacheSubdir: "toolchain",
  bundledSubdir: "compiler",
};

/** Return the executable filename for a given RID. */
export function getCompilerExecutableName(rid: string): string {
  return getExecutableName(COMPILER_CONFIG.executableBaseName, rid);
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
  return getToolCachePath(context, COMPILER_CONFIG, version, rid);
}

/**
 * Return the optional compiler path bundled inside the extension.
 * Layout: <extension>/compiler/<rid>/<executable>
 */
export function getBundledCompilerPath(
  context: vscode.ExtensionContext,
  rid: string,
): string {
  return getBundledToolPath(context, COMPILER_CONFIG, rid);
}

/**
 * Acquire the Ashes compiler for the current platform.
 *
 * - If a bundled binary exists for the required version, returns it immediately.
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
  return acquireTool(context, COMPILER_CONFIG, requiredVersion);
}
