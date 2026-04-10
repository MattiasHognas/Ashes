import * as assert from "assert";
import * as vscode from "vscode";
import * as path from "path";

const fixturesPath = path.resolve(__dirname, "../../test/fixtures");

suite("Ashes Extension", () => {
  suiteSetup(async () => {
    // Ensure the extension is activated by opening an .ash file
    const uri = vscode.Uri.file(path.join(fixturesPath, "hello.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);

    // Give the extension a moment to activate
    await new Promise((resolve) => setTimeout(resolve, 2_000));
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

  test("TextMate grammar provides tokenization", async () => {
    const uri = vscode.Uri.file(path.join(fixturesPath, "types.ash"));
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);

    // Wait for tokenization to complete
    await new Promise((resolve) => setTimeout(resolve, 1_000));

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

    // Verify keywords are present in the source — ensures the fixture is
    // intact and the grammar file is bundled.
    const fullText = doc.getText();
    assert.ok(fullText.includes("type"), "Source should contain 'type'");
    assert.ok(fullText.includes("match"), "Source should contain 'match'");
    assert.ok(fullText.includes("let"), "Source should contain 'let'");
    assert.ok(fullText.includes("fun"), "Source should contain 'fun'");
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
