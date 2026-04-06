/**
 * Compiler acquisition — thin wrapper around the generic tool acquisition module.
 */

import * as vscode from "vscode";
import { type ToolConfig, acquireTool } from "./toolAcquisition";

const COMPILER_CONFIG: ToolConfig = {
  displayName: "Ashes compiler",
  assetPrefix: "ashes",
  executableBaseName: "ashes",
  cacheSubdir: "toolchain",
  bundledSubdir: "compiler",
  settingKey: "ashes.compilerPath",
};

/**
 * Acquire the Ashes compiler for the current platform.
 *
 * - If a user-configured path override is set, returns it immediately.
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
