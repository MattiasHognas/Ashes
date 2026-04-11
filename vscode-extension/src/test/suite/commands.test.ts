import * as assert from "assert";
import * as fs from "fs";
import * as os from "os";
import * as vscode from "vscode";
import * as path from "path";

const fixturesPath = path.resolve(__dirname, "../../../src/test/fixtures");
const mockToolsPath = path.join(fixturesPath, "mock-tools");

/**
 * Detect whether real toolchain binaries are available via workspace settings
 * (configured by the test runner from CI environment variables).
 */
function hasRealBinary(settingKey: string): boolean {
  const value = vscode.workspace
    .getConfiguration("ashes")
    .get<string>(settingKey, "")
    .trim();
  return value.length > 0 && !value.includes("mock-");
}

/**
 * Create a temporary sentinel file path for the mock compiler to write to.
 * The mock writes the subcommand name (e.g. "compile") into this file,
 * letting the test verify that the command was actually dispatched.
 */
function createSentinelPath(): string {
  return path.join(
    os.tmpdir(),
    `ashes-test-sentinel-${Date.now()}-${Math.random().toString(36).slice(2)}`,
  );
}

suite("Command Execution — mock tools", () => {
  let savedCompilerPath: string | undefined;
  let savedLspPath: string | undefined;
  let savedDapPath: string | undefined;
  let sentinelPath: string | undefined;

  suiteSetup(async () => {
    const config = vscode.workspace.getConfiguration("ashes");
    savedCompilerPath = config.get<string>("compilerPath");
    savedLspPath = config.get<string>("lspServerPath");
    savedDapPath = config.get<string>("dapServerPath");

    // Override at Workspace level so they take precedence over
    // any paths configured in the .code-workspace file by the test runner.
    await config.update(
      "compilerPath",
      path.join(mockToolsPath, "mock-compiler"),
      vscode.ConfigurationTarget.Workspace,
    );
    await config.update(
      "lspServerPath",
      path.join(mockToolsPath, "mock-lsp"),
      vscode.ConfigurationTarget.Workspace,
    );
    await config.update(
      "dapServerPath",
      path.join(mockToolsPath, "mock-dap"),
      vscode.ConfigurationTarget.Workspace,
    );

    // Ensure an .ash file is open for compile/test commands
    const uri = vscode.Uri.file(path.join(fixturesPath, "hello.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);
  });

  setup(() => {
    sentinelPath = createSentinelPath();
    process.env.ASHES_MOCK_SENTINEL = sentinelPath;
  });

  teardown(() => {
    if (sentinelPath && fs.existsSync(sentinelPath)) {
      fs.unlinkSync(sentinelPath);
    }
    delete process.env.ASHES_MOCK_SENTINEL;
    sentinelPath = undefined;
  });

  suiteTeardown(async () => {
    const config = vscode.workspace.getConfiguration("ashes");
    await config.update(
      "compilerPath",
      savedCompilerPath || undefined,
      vscode.ConfigurationTarget.Workspace,
    );
    await config.update(
      "lspServerPath",
      savedLspPath || undefined,
      vscode.ConfigurationTarget.Workspace,
    );
    await config.update(
      "dapServerPath",
      savedDapPath || undefined,
      vscode.ConfigurationTarget.Workspace,
    );
  });

  test("ashes.compile dispatches the compile subcommand", async () => {
    await vscode.commands.executeCommand("ashes.compile");
    assert.ok(sentinelPath, "sentinelPath should be set");
    assert.ok(
      fs.existsSync(sentinelPath),
      "Mock compiler should have written a sentinel file",
    );
    assert.strictEqual(
      fs.readFileSync(sentinelPath, "utf8"),
      "compile",
      "Sentinel should contain 'compile'",
    );
  });

  test("ashes.test dispatches the test subcommand", async () => {
    await vscode.commands.executeCommand("ashes.test");
    assert.ok(sentinelPath, "sentinelPath should be set");
    assert.ok(
      fs.existsSync(sentinelPath),
      "Mock compiler should have written a sentinel file",
    );
    assert.strictEqual(
      fs.readFileSync(sentinelPath, "utf8"),
      "test",
      "Sentinel should contain 'test'",
    );
  });

  test("ashes.installToolchain acquires all tools via overrides", async () => {
    await vscode.commands.executeCommand("ashes.installToolchain");
  });

  test("setting overrides point to mock tools", () => {
    const config = vscode.workspace.getConfiguration("ashes");
    assert.ok(
      config.get<string>("compilerPath")?.endsWith("mock-compiler"),
      "Compiler path should point to mock-compiler",
    );
    assert.ok(
      config.get<string>("lspServerPath")?.endsWith("mock-lsp"),
      "LSP path should point to mock-lsp",
    );
    assert.ok(
      config.get<string>("dapServerPath")?.endsWith("mock-dap"),
      "DAP path should point to mock-dap",
    );
  });
});

suite("Command Execution — real compiler", () => {
  let realCompilerPath: string | undefined;

  suiteSetup(function () {
    // The real compiler path is configured by the test runner from
    // the ASHES_COMPILER_PATH environment variable.
    realCompilerPath = vscode.workspace
      .getConfiguration("ashes")
      .get<string>("compilerPath", "")
      .trim();

    if (!realCompilerPath || realCompilerPath.includes("mock-")) {
      this.skip();
    }
  });

  test("real compiler is configured via workspace settings", () => {
    assert.ok(
      realCompilerPath && realCompilerPath.length > 0,
      "ASHES_COMPILER_PATH should be set",
    );
    assert.ok(
      !realCompilerPath?.includes("mock-"),
      "Should use the real compiler, not a mock",
    );
  });

  test("ashes.compile compiles a valid .ash file", async () => {
    const uri = vscode.Uri.file(path.join(fixturesPath, "hello.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);
    await vscode.commands.executeCommand("ashes.compile");
  });

  test("ashes.compile compiles an ADT/match file", async () => {
    const uri = vscode.Uri.file(path.join(fixturesPath, "types.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);
    await vscode.commands.executeCommand("ashes.compile");
  });

  test("ashes.installToolchain succeeds with real binaries", async function () {
    if (!hasRealBinary("lspServerPath") || !hasRealBinary("dapServerPath")) {
      this.skip();
    }
    await vscode.commands.executeCommand("ashes.installToolchain");
  });
});
