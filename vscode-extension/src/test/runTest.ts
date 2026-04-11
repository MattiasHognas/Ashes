import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { randomUUID } from "crypto";
import { runTests } from "@vscode/test-electron";

async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, "../../");
  const extensionTestsPath = path.resolve(__dirname, "./suite/index");
  const testWorkspace = path.resolve(__dirname, "../../src/test/fixtures");
  const testWorkspaceFile = path.join(
    os.tmpdir(),
    `ashes-test-workspace-${randomUUID()}.code-workspace`,
  );

  // Build workspace settings from environment variables when available.
  // CI publishes real compiler/LSP/DAP binaries and exposes their paths
  // via ASHES_COMPILER_PATH, ASHES_LSP_PATH, and ASHES_DAP_PATH.
  const settings: Record<string, unknown> = {
    "ashes.autoStartLanguageServer": false,
  };

  const compilerPath = process.env.ASHES_COMPILER_PATH;
  const lspPath = process.env.ASHES_LSP_PATH;
  const dapPath = process.env.ASHES_DAP_PATH;

  if (compilerPath) {
    settings["ashes.compilerPath"] = compilerPath;
  }
  if (lspPath) {
    settings["ashes.lspServerPath"] = lspPath;
    settings["ashes.autoStartLanguageServer"] = true;
  }
  if (dapPath) {
    settings["ashes.dapServerPath"] = dapPath;
  }

  fs.writeFileSync(
    testWorkspaceFile,
    JSON.stringify(
      {
        folders: [{ path: testWorkspace }],
        settings,
      },
      null,
      2,
    ),
    "utf8",
  );

  try {
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      launchArgs: [
        testWorkspaceFile,
        "--disable-extensions",
        "--disable-gpu",
        "--no-sandbox",
        "--disable-workspace-trust",
      ],
    });
  } finally {
    if (fs.existsSync(testWorkspaceFile)) {
      fs.unlinkSync(testWorkspaceFile);
    }
  }
}

void main().catch((err) => {
  console.error("Failed to run tests:", err);
  process.exit(1);
});
