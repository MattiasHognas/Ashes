import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { spawn } from "child_process";
import { acquireCompiler } from "./compilerAcquisition";
import { acquireTool } from "./toolAcquisition";
import { LSP_CONFIG, DAP_CONFIG, getRequiredVersion } from "./toolConfigs";

let outputChannel: vscode.OutputChannel | undefined;

function getOutputChannel(): vscode.OutputChannel {
  if (!outputChannel) {
    outputChannel = vscode.window.createOutputChannel("Ashes Compiler");
  }
  return outputChannel;
}

/**
 * Guard that checks workspace trust before downloading binaries.
 * Returns true when the workspace is trusted, false otherwise.
 */
function requireTrustedWorkspace(): boolean {
  if (!vscode.workspace.isTrusted) {
    void vscode.window.showWarningMessage(
      "This command requires a trusted workspace. Open the workspace trust editor to grant trust.",
    );
    return false;
  }
  return true;
}

/**
 * Get the compiler path, ensuring it matches the required version.
 * Always resolves via compiler acquisition (which may use its own cache).
 */
async function ensureCompiler(
  context: vscode.ExtensionContext,
): Promise<string | undefined> {
  if (!requireTrustedWorkspace()) {
    return undefined;
  }

  const requiredVersion =
    (context.extension.packageJSON as { version?: string }).version ?? "0.0.1";

  try {
    const compilerPath = await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: "Downloading Ashes compiler...",
        cancellable: false,
      },
      async () => acquireCompiler(context, requiredVersion),
    );

    await context.workspaceState.update("ashes.compilerPath", compilerPath);
    return compilerPath;
  } catch (err) {
    vscode.window.showErrorMessage(
      `Failed to acquire Ashes compiler: ${(err as Error).message}`,
    );
    return undefined;
  }
}

/**
 * Search upward from a starting directory for a file.
 * Returns the file path if found, undefined otherwise.
 */
function findFileUpward(
  startDir: string,
  filename: string,
): string | undefined {
  let currentDir = startDir;
  const root = path.parse(currentDir).root;

  while (true) {
    const candidate = path.join(currentDir, filename);
    if (fs.existsSync(candidate)) {
      return candidate;
    }

    if (currentDir === root) {
      break;
    }

    currentDir = path.dirname(currentDir);
  }

  return undefined;
}

/**
 * Execute the compiler with the given arguments.
 * Streams output to the output channel and returns exit code.
 */
async function executeCompiler(
  compilerPath: string,
  args: string[],
  cwd: string,
): Promise<number> {
  const channel = getOutputChannel();
  channel.clear();
  channel.show(true);

  const finalArgs = [...args];

  channel.appendLine(`$ ashes ${finalArgs.join(" ")}`);
  channel.appendLine("");

  return new Promise<number>((resolve) => {
    const proc = spawn(compilerPath, finalArgs, {
      cwd,
      shell: false,
      windowsHide: true,
    });

    proc.stdout.on("data", (data: Buffer) => {
      channel.append(data.toString());
    });

    proc.stderr.on("data", (data: Buffer) => {
      channel.append(data.toString());
    });

    proc.on("close", (code: number | null) => {
      channel.appendLine("");
      if (code === 0) {
        channel.appendLine("✓ Command completed successfully");
      } else {
        channel.appendLine(
          `✗ Command failed with exit code ${code ?? "unknown"}`,
        );
      }
      resolve(code ?? 1);
    });

    proc.on("error", (err: Error) => {
      channel.appendLine("");
      channel.appendLine(`✗ Failed to execute compiler: ${err.message}`);
      resolve(1);
    });
  });
}

function getActiveAshEditor(): vscode.TextEditor | undefined {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== "ashes") {
    return undefined;
  }
  return editor;
}

/**
 * Compile the active .ash file or project.
 */
export async function compileCommand(
  context: vscode.ExtensionContext,
): Promise<void> {
  const compilerPath = await ensureCompiler(context);
  if (!compilerPath) {
    return;
  }

  const editor = getActiveAshEditor();
  if (!editor) {
    vscode.window.showErrorMessage(
      "No active .ash file. Open an Ashes file and try again.",
    );
    return;
  }

  if (editor.document.isDirty) {
    await editor.document.save();
  }

  const filePath = editor.document.uri.fsPath;
  const fileDir = path.dirname(filePath);
  const projectFile = findFileUpward(fileDir, "ashes.json");

  let args: string[];
  let cwd: string;

  if (projectFile) {
    args = ["compile", "--project", projectFile];
    cwd = path.dirname(projectFile);
    vscode.window.showInformationMessage(
      `Compiling project: ${path.basename(path.dirname(projectFile))}`,
    );
  } else {
    args = ["compile", filePath];
    cwd = fileDir;
    vscode.window.showInformationMessage(
      `Compiling: ${path.basename(filePath)}`,
    );
  }

  const exitCode = await executeCompiler(compilerPath, args, cwd);

  if (exitCode === 0) {
    vscode.window.showInformationMessage("Compilation successful!");
  } else {
    vscode.window.showErrorMessage(
      "Compilation failed. See output for details.",
    );
  }
}

/**
 * Compile and run the active .ash file or project.
 */
export async function runCommand(
  context: vscode.ExtensionContext,
): Promise<void> {
  const compilerPath = await ensureCompiler(context);
  if (!compilerPath) {
    return;
  }

  const editor = getActiveAshEditor();
  if (!editor) {
    vscode.window.showErrorMessage(
      "No active .ash file. Open an Ashes file and try again.",
    );
    return;
  }

  if (editor.document.isDirty) {
    await editor.document.save();
  }

  const filePath = editor.document.uri.fsPath;
  const fileDir = path.dirname(filePath);
  const projectFile = findFileUpward(fileDir, "ashes.json");

  let args: string[];
  let cwd: string;

  if (projectFile) {
    args = ["run", "--project", projectFile];
    cwd = path.dirname(projectFile);
    vscode.window.showInformationMessage(
      `Running project: ${path.basename(path.dirname(projectFile))}`,
    );
  } else {
    args = ["run", filePath];
    cwd = fileDir;
    vscode.window.showInformationMessage(`Running: ${path.basename(filePath)}`);
  }

  const input = await vscode.window.showInputBox({
    prompt: "Program arguments (optional)",
    placeHolder: "arg1 arg2 arg3",
  });

  if (input === undefined) {
    return;
  }

  if (input.trim()) {
    args.push("--");
    args.push(...input.trim().split(/\s+/));
  }

  await executeCompiler(compilerPath, args, cwd);
}

/**
 * Run tests in the workspace or active file.
 */
export async function testCommand(
  context: vscode.ExtensionContext,
): Promise<void> {
  const compilerPath = await ensureCompiler(context);
  if (!compilerPath) {
    return;
  }

  const workspaceFolders = vscode.workspace.workspaceFolders;
  if (!workspaceFolders || workspaceFolders.length === 0) {
    vscode.window.showErrorMessage("No workspace folder open.");
    return;
  }

  const workspaceRoot = workspaceFolders[0].uri.fsPath;

  const editor = vscode.window.activeTextEditor;
  let args: string[];
  let cwd: string;

  if (editor && editor.document.languageId === "ashes") {
    if (editor.document.isDirty) {
      const saved = await editor.document.save();
      if (!saved) {
        vscode.window.showWarningMessage(
          "Save was canceled or failed. Tests were not run.",
        );
        return;
      }
    }
    const filePath = editor.document.uri.fsPath;
    args = ["test", filePath];
    cwd = path.dirname(filePath);
    vscode.window.showInformationMessage(
      `Running tests: ${path.basename(filePath)}`,
    );
  } else {
    args = ["test"];
    cwd = workspaceRoot;
    vscode.window.showInformationMessage("Running all tests in workspace...");
  }

  const exitCode = await executeCompiler(compilerPath, args, cwd);

  if (exitCode === 0) {
    vscode.window.showInformationMessage("All tests passed!");
  } else {
    vscode.window.showErrorMessage("Tests failed. See output for details.");
  }
}

/**
 * Explicitly install all Ashes toolchain binaries (compiler, LSP, DAP).
 */
export async function installToolchainCommand(
  context: vscode.ExtensionContext,
): Promise<void> {
  if (!requireTrustedWorkspace()) {
    return;
  }

  const requiredVersion = getRequiredVersion(context);
  let compilerInstalled = false;
  let languageServerInstalled = false;
  let dapServerInstalled = false;

  try {
    await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: "Installing Ashes toolchain…",
        cancellable: false,
      },
      async (progress) => {
        progress.report({ message: "Acquiring compiler…" });
        const compilerPath = await acquireCompiler(context, requiredVersion);
        await context.workspaceState.update("ashes.compilerPath", compilerPath);
        compilerInstalled = true;

        progress.report({ message: "Acquiring language server…" });
        await acquireTool(context, LSP_CONFIG, requiredVersion);
        languageServerInstalled = true;

        progress.report({ message: "Acquiring DAP server…" });
        await acquireTool(context, DAP_CONFIG, requiredVersion);
        dapServerInstalled = true;
      },
    );
  } catch (error: unknown) {
    const installedComponents = [
      compilerInstalled ? "compiler" : undefined,
      languageServerInstalled ? "language server" : undefined,
      dapServerInstalled ? "DAP server" : undefined,
    ].filter((component): component is string => component !== undefined);

    const partialSuccessMessage =
      installedComponents.length > 0
        ? ` Installed before the failure: ${installedComponents.join(", ")}.`
        : "";
    const errorMessage = error instanceof Error ? error.message : String(error);

    void vscode.window.showErrorMessage(
      `Failed to install the Ashes toolchain: ${errorMessage}.${partialSuccessMessage}`,
    );
    return;
  }
  void vscode.window.showInformationMessage(
    "Ashes toolchain installed successfully.",
  );
}

/**
 * Dispose the output channel when the extension deactivates.
 */
export function disposeCommands(): void {
  outputChannel?.dispose();
  outputChannel = undefined;
}
