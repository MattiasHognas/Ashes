import * as assert from "assert";
import * as vscode from "vscode";
import * as path from "path";

const fixturesPath = path.resolve(__dirname, "../../../src/test/fixtures");

/**
 * Wait until diagnostics appear for the given URI, or timeout.
 * Returns the diagnostic array (may be empty on timeout).
 */
async function waitForDiagnostics(
  uri: vscode.Uri,
  timeoutMs = 15_000,
): Promise<readonly vscode.Diagnostic[]> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const diags = vscode.languages.getDiagnostics(uri);
    if (diags.length > 0) {
      return diags;
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  return vscode.languages.getDiagnostics(uri);
}

/**
 * Wait for the LSP server to be ready by watching for diagnostic activity.
 * Opens a file with a known error and waits for the LSP to report it.
 */
async function waitForLspReady(timeoutMs = 20_000): Promise<boolean> {
  const errorUri = vscode.Uri.file(path.join(fixturesPath, "error.ash"));
  const doc = await vscode.workspace.openTextDocument(errorUri);
  await vscode.window.showTextDocument(doc);

  const diags = await waitForDiagnostics(errorUri, timeoutMs);
  return diags.length > 0;
}

suite("LSP Activation — real server", () => {
  suiteSetup(function () {
    // Only run when a real LSP server is configured via CI env vars.
    const lspPath = vscode.workspace
      .getConfiguration("ashes")
      .get<string>("lspServerPath", "")
      .trim();

    if (!lspPath || lspPath.includes("mock-")) {
      this.skip();
    }
  });

  test("LSP server starts and produces diagnostics for error file", async function () {
    this.timeout(30_000);

    const ready = await waitForLspReady();
    assert.ok(
      ready,
      "LSP server should produce diagnostics for a file with errors",
    );
  });

  test("LSP server reports no errors for valid file", async function () {
    this.timeout(30_000);

    // First ensure LSP is active by opening the error file
    await waitForLspReady();

    // Now open a valid file and give the LSP time to process it
    const validUri = vscode.Uri.file(path.join(fixturesPath, "hello.ash"));
    const doc = await vscode.workspace.openTextDocument(validUri);
    await vscode.window.showTextDocument(doc);

    // Wait a bit for the LSP to process the document
    await new Promise((resolve) => setTimeout(resolve, 3_000));

    const diags = vscode.languages.getDiagnostics(validUri);
    assert.strictEqual(
      diags.length,
      0,
      `Expected no diagnostics for hello.ash, got: ${diags.map((d) => d.message).join(", ")}`,
    );
  });

  test("LSP server reports diagnostics for type error file", async function () {
    this.timeout(30_000);

    const errorUri = vscode.Uri.file(path.join(fixturesPath, "error.ash"));
    const doc = await vscode.workspace.openTextDocument(errorUri);
    await vscode.window.showTextDocument(doc);

    const diags = await waitForDiagnostics(errorUri);
    assert.ok(diags.length > 0, "Should report at least one diagnostic");
    assert.ok(
      diags.some((d) => d.severity === vscode.DiagnosticSeverity.Error),
      "Should include an error-level diagnostic",
    );
  });
});
