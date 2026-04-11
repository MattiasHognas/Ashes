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

/**
 * Wait for diagnostic stability on a URI: watch for diagnostic change events
 * and return the diagnostics once they have not changed for `quietMs`.
 * Falls back to the current diagnostics after `timeoutMs`.
 */
async function waitForStableDiagnostics(
  uri: vscode.Uri,
  quietMs = 2_000,
  timeoutMs = 15_000,
): Promise<readonly vscode.Diagnostic[]> {
  return new Promise((resolve) => {
    let timer: ReturnType<typeof setTimeout>;
    const deadline = setTimeout(() => {
      disposable.dispose();
      clearTimeout(timer);
      resolve(vscode.languages.getDiagnostics(uri));
    }, timeoutMs);

    const resetQuietTimer = (): void => {
      clearTimeout(timer);
      timer = setTimeout(() => {
        clearTimeout(deadline);
        disposable.dispose();
        resolve(vscode.languages.getDiagnostics(uri));
      }, quietMs);
    };

    const disposable = vscode.languages.onDidChangeDiagnostics((e) => {
      if (e.uris.some((u) => u.toString() === uri.toString())) {
        resetQuietTimer();
      }
    });

    // Start the initial quiet timer in case the LSP already processed
    // this document before we subscribed.
    resetQuietTimer();
  });
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

    // Now open a valid file and wait for diagnostics to stabilise
    const validUri = vscode.Uri.file(path.join(fixturesPath, "hello.ash"));
    const doc = await vscode.workspace.openTextDocument(validUri);
    await vscode.window.showTextDocument(doc);

    const diags = await waitForStableDiagnostics(validUri);
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
