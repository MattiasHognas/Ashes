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

  fs.writeFileSync(
    testWorkspaceFile,
    JSON.stringify(
      {
        folders: [{ path: testWorkspace }],
        settings: {
          "ashes.autoStartLanguageServer": false,
        },
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
