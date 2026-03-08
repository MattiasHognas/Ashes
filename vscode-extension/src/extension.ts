import * as path from "path";
import * as fs from "fs";
import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";
import { acquireCompiler } from "./compilerAcquisition";
import {
  compileCommand,
  runCommand,
  testCommand,
  disposeCommands,
} from "./commands";

let client: LanguageClient | undefined;

function getServerExecutable(context: vscode.ExtensionContext): string {
  if (process.platform === "win32") {
    return path.join(
      context.extensionPath,
      "server",
      "win-x64",
      "Ashes.Lsp.exe",
    );
  }

  if (process.platform === "linux") {
    return path.join(context.extensionPath, "server", "linux-x64", "Ashes.Lsp");
  }

  throw new Error(`Unsupported platform: ${process.platform}`);
}

function getRequiredCompilerVersion(context: vscode.ExtensionContext): string {
  return (
    (context.extension.packageJSON as { version?: string }).version ?? "0.0.1"
  );
}

export async function activate(
  context: vscode.ExtensionContext,
): Promise<void> {
  const executable = getServerExecutable(context);

  if (!fs.existsSync(executable)) {
    vscode.window.showErrorMessage(
      `Ashes language server not found at ${executable}. Run \"npm run build-server\" in vscode-extension.`,
    );
    return;
  }

  // Acquire the Ashes CLI compiler in the background.
  // Failures show a notification but do not block the language server.
  const requiredVersion = getRequiredCompilerVersion(context);
  acquireCompiler(context, requiredVersion)
    .then((compilerPath: string) => {
      // Store the acquired compiler path so it can be used by other extension features.
      void context.workspaceState.update("ashes.compilerPath", compilerPath);
    })
    .catch((err: unknown) => {
      vscode.window.showErrorMessage(
        `Failed to acquire Ashes compiler v${requiredVersion}: ${(err as Error).message}`,
      );
    });

  const serverOptions: ServerOptions = {
    command: executable,
    transport: TransportKind.stdio,
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: "file", language: "ashes" }],
  };

  client = new LanguageClient(
    "ashes-lsp",
    "Ashes Language Server",
    serverOptions,
    clientOptions,
  );
  context.subscriptions.push(client);
  await client.start();

  // Register compiler commands
  context.subscriptions.push(
    vscode.commands.registerCommand("ashes.compile", () =>
      compileCommand(context),
    ),
  );
  context.subscriptions.push(
    vscode.commands.registerCommand("ashes.run", () => runCommand(context)),
  );
  context.subscriptions.push(
    vscode.commands.registerCommand("ashes.test", () => testCommand(context)),
  );
}

export async function deactivate(): Promise<void> {
  if (client) {
    await client.stop();
    client = undefined;
  }
  disposeCommands();
}
