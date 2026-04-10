import * as path from "path";
import { runTests } from "@vscode/test-electron";

async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, "../../");
  const extensionTestsPath = path.resolve(__dirname, "./suite/index");
  const testWorkspace = path.resolve(__dirname, "../../src/test/fixtures");

  await runTests({
    extensionDevelopmentPath,
    extensionTestsPath,
    launchArgs: [
      testWorkspace,
      "--disable-extensions",
      "--disable-gpu",
      "--no-sandbox",
    ],
  });
}

void main().catch((err) => {
  console.error("Failed to run tests:", err);
  process.exit(1);
});
