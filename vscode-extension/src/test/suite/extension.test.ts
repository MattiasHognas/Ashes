import * as assert from "assert";
import * as vscode from "vscode";
import * as path from "path";

const fixturesPath = path.resolve(__dirname, "../../../src/test/fixtures");

/** Poll until the extension is active, with a timeout. */
async function waitForActivation(timeoutMs = 10_000): Promise<void> {
  const extensionId = "mattiashognas.ashes-vscode";
  const ext = vscode.extensions.getExtension(extensionId);
  assert.ok(ext, `Extension ${extensionId} should be installed`);

  if (!ext.isActive) {
    await ext.activate();
  }

  const start = Date.now();
  while (!ext.isActive && Date.now() - start < timeoutMs) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }

  assert.strictEqual(
    ext.isActive,
    true,
    `Extension ${extensionId} did not activate within ${timeoutMs}ms`,
  );
}

suite("Ashes Extension", () => {
  suiteSetup(async () => {
    // Ensure the extension is activated by opening an .ash file
    const uri = vscode.Uri.file(path.join(fixturesPath, "hello.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);

    // Wait for the extension to finish activating
    await waitForActivation();
  });

  test("Extension is present", () => {
    const ext = vscode.extensions.getExtension("mattiashognas.ashes-vscode");
    assert.ok(ext, "Extension should be installed");
  });

  test("Extension activates", () => {
    const ext = vscode.extensions.getExtension("mattiashognas.ashes-vscode");
    assert.ok(ext, "Extension should be installed");
    assert.strictEqual(ext.isActive, true, "Extension should be active");
  });

  test(".ash files are recognized as ashes language", async () => {
    const uri = vscode.Uri.file(path.join(fixturesPath, "hello.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    assert.strictEqual(
      doc.languageId,
      "ashes",
      'Language ID should be "ashes"',
    );
  });

  test("ashes.compile command is registered", async () => {
    const commands = await vscode.commands.getCommands(true);
    assert.ok(
      commands.includes("ashes.compile"),
      "ashes.compile should be registered",
    );
  });

  test("ashes.run command is registered", async () => {
    const commands = await vscode.commands.getCommands(true);
    assert.ok(commands.includes("ashes.run"), "ashes.run should be registered");
  });

  test("ashes.test command is registered", async () => {
    const commands = await vscode.commands.getCommands(true);
    assert.ok(
      commands.includes("ashes.test"),
      "ashes.test should be registered",
    );
  });

  test("ashes.installToolchain command is registered", async () => {
    const commands = await vscode.commands.getCommands(true);
    assert.ok(
      commands.includes("ashes.installToolchain"),
      "ashes.installToolchain should be registered",
    );
  });

  test("Grammar and language registration", async () => {
    const uri = vscode.Uri.file(path.join(fixturesPath, "types.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);

    // Verify the grammar is registered by checking that "ashes" is among
    // the known languages.  If the grammar were missing, VS Code would not
    // list "ashes" as a recognized language.
    const languages = await vscode.languages.getLanguages();
    assert.ok(
      languages.includes("ashes"),
      '"ashes" should be a registered language',
    );

    // Verify the file is recognized as Ashes (grammar association works)
    assert.strictEqual(
      doc.languageId,
      "ashes",
      "types.ash should be recognized as ashes language",
    );

    // Verify the extension contributes a grammar entry for "ashes" and
    // that the referenced grammar file actually exists in the extension.
    const ext = vscode.extensions.getExtension("mattiashognas.ashes-vscode");
    assert.ok(ext, "Extension should be installed");

    const contributes = (
      ext.packageJSON as {
        contributes?: {
          grammars?: Array<{
            language: string;
            scopeName: string;
            path: string;
          }>;
        };
      }
    ).contributes;
    assert.ok(contributes?.grammars, "Extension should contribute grammars");

    const ashesGrammar = contributes.grammars.find(
      (g) => g.language === "ashes",
    );
    assert.ok(
      ashesGrammar,
      'Extension should contribute a grammar for language "ashes"',
    );
    assert.strictEqual(
      ashesGrammar.scopeName,
      "source.ashes",
      "Grammar scopeName should be source.ashes",
    );

    // Verify the grammar file referenced in the contribution actually
    // exists within the installed extension directory.
    const grammarAbsPath = path.join(ext.extensionPath, ashesGrammar.path);
    const grammarExists = await vscode.workspace.fs
      .stat(vscode.Uri.file(grammarAbsPath))
      .then(
        () => true,
        () => false,
      );
    assert.ok(
      grammarExists,
      `Grammar file should exist at ${ashesGrammar.path} (resolved: ${grammarAbsPath})`,
    );
  });

  test("ashes debugger type is registered", () => {
    // Verify the extension contributes a debug adapter of type "ashes"
    // by inspecting the extension's package.json contributions.
    const ext = vscode.extensions.getExtension("mattiashognas.ashes-vscode");
    assert.ok(ext, "Extension should be installed");

    const contributes = (
      ext.packageJSON as {
        contributes?: { debuggers?: Array<{ type: string }> };
      }
    ).contributes;
    assert.ok(contributes?.debuggers, "Extension should contribute debuggers");

    const ashesDebugger = contributes.debuggers.find(
      (d: { type: string }) => d.type === "ashes",
    );
    assert.ok(
      ashesDebugger,
      'Extension should contribute a debugger of type "ashes"',
    );
  });
});
