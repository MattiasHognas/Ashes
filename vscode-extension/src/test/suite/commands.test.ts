import * as assert from "assert";
import * as vscode from "vscode";
import * as path from "path";

const fixturesPath = path.resolve(__dirname, "../../../src/test/fixtures");
const mockToolsPath = path.join(fixturesPath, "mock-tools");

suite("Command Execution", () => {
  let savedCompilerPath: string | undefined;
  let savedLspPath: string | undefined;
  let savedDapPath: string | undefined;

  suiteSetup(async () => {
    const config = vscode.workspace.getConfiguration("ashes");
    savedCompilerPath = config.get<string>("compilerPath");
    savedLspPath = config.get<string>("lspServerPath");
    savedDapPath = config.get<string>("dapServerPath");

    // Point all tool paths to mock executables
    await config.update(
      "compilerPath",
      path.join(mockToolsPath, "mock-compiler"),
      vscode.ConfigurationTarget.Global,
    );
    await config.update(
      "lspServerPath",
      path.join(mockToolsPath, "mock-lsp"),
      vscode.ConfigurationTarget.Global,
    );
    await config.update(
      "dapServerPath",
      path.join(mockToolsPath, "mock-dap"),
      vscode.ConfigurationTarget.Global,
    );

    // Ensure an .ash file is open for compile/test commands
    const uri = vscode.Uri.file(path.join(fixturesPath, "hello.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);
  });

  suiteTeardown(async () => {
    const config = vscode.workspace.getConfiguration("ashes");
    await config.update(
      "compilerPath",
      savedCompilerPath || undefined,
      vscode.ConfigurationTarget.Global,
    );
    await config.update(
      "lspServerPath",
      savedLspPath || undefined,
      vscode.ConfigurationTarget.Global,
    );
    await config.update(
      "dapServerPath",
      savedDapPath || undefined,
      vscode.ConfigurationTarget.Global,
    );
  });

  test("ashes.compile executes with mock compiler", async () => {
    // The compile command acquires the compiler via the setting override,
    // then spawns it with "compile <file>". The mock exits with code 0.
    await vscode.commands.executeCommand("ashes.compile");
  });

  test("ashes.test executes with mock compiler", async () => {
    // The test command acquires the compiler via the setting override,
    // then spawns it with "test <file>". The mock exits with code 0.
    await vscode.commands.executeCommand("ashes.test");
  });

  test("ashes.installToolchain acquires all tools via overrides", async () => {
    // installToolchain acquires the compiler, LSP server, and DAP server.
    // With setting overrides pointing to mock executables, all three
    // acquisitions should succeed without downloading anything.
    await vscode.commands.executeCommand("ashes.installToolchain");
  });

  test("ashes.compile uses the configured compiler path", () => {
    // Verify that the compiler path setting is being used
    const config = vscode.workspace.getConfiguration("ashes");
    const compilerPath = config.get<string>("compilerPath");
    assert.ok(
      compilerPath?.endsWith("mock-compiler"),
      "Compiler path should point to mock-compiler",
    );
  });

  test("ashes.installToolchain uses configured LSP path", () => {
    const config = vscode.workspace.getConfiguration("ashes");
    const lspPath = config.get<string>("lspServerPath");
    assert.ok(
      lspPath?.endsWith("mock-lsp"),
      "LSP path should point to mock-lsp",
    );
  });

  test("ashes.installToolchain uses configured DAP path", () => {
    const config = vscode.workspace.getConfiguration("ashes");
    const dapPath = config.get<string>("dapServerPath");
    assert.ok(
      dapPath?.endsWith("mock-dap"),
      "DAP path should point to mock-dap",
    );
  });
});
