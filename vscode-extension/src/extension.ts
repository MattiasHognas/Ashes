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
    if (process.arch !== "x64" && process.arch !== "arm64") {
      throw new Error(`Unsupported Linux architecture: ${process.arch}`);
    }
    const rid = process.arch === "arm64" ? "linux-arm64" : "linux-x64";
    return path.join(context.extensionPath, "server", rid, "Ashes.Lsp");
  }

  throw new Error(`Unsupported platform: ${process.platform}`);
}

function getDapServerExecutable(context: vscode.ExtensionContext): string {
  if (process.platform === "win32") {
    return path.join(
      context.extensionPath,
      "dap-server",
      "win-x64",
      "ashes-dap.exe",
    );
  }

  if (process.platform === "linux") {
    if (process.arch !== "x64" && process.arch !== "arm64") {
      throw new Error(`Unsupported Linux architecture: ${process.arch}`);
    }
    const rid = process.arch === "arm64" ? "linux-arm64" : "linux-x64";
    return path.join(
      context.extensionPath,
      "dap-server",
      rid,
      "ashes-dap",
    );
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
      `Ashes language server not found at ${executable}. Run \"pnpm run build-lsp-server\" in vscode-extension.`,
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

  // Register debug adapter — uses the bundled DAP server binary
  const dapFactory = new AshesDebugAdapterFactory(context);
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory("ashes", dapFactory),
  );

  const debugConfigProvider = new AshesDebugConfigurationProvider();
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider("ashes", debugConfigProvider),
  );
}

export async function deactivate(): Promise<void> {
  if (client) {
    await client.stop();
    client = undefined;
  }
  disposeCommands();
}

class AshesDebugAdapterFactory
  implements vscode.DebugAdapterDescriptorFactory
{
  private readonly context: vscode.ExtensionContext;

  constructor(context: vscode.ExtensionContext) {
    this.context = context;
  }

  createDebugAdapterDescriptor(
    _session: vscode.DebugSession,
    _executable: vscode.DebugAdapterExecutable | undefined,
  ): vscode.ProviderResult<vscode.DebugAdapterDescriptor> {
    const dapExecutable = getDapServerExecutable(this.context);

    if (!fs.existsSync(dapExecutable)) {
      vscode.window.showErrorMessage(
        `Ashes DAP server not found at ${dapExecutable}. ` +
          'Run "pnpm run build-dap-server" in the vscode-extension directory.',
      );
      return undefined;
    }

    return new vscode.DebugAdapterExecutable(dapExecutable, []);
  }
}

class AshesDebugConfigurationProvider
  implements vscode.DebugConfigurationProvider
{
  resolveDebugConfiguration(
    _folder: vscode.WorkspaceFolder | undefined,
    config: vscode.DebugConfiguration,
    _token?: vscode.CancellationToken,
  ): vscode.ProviderResult<vscode.DebugConfiguration> {
    // If no launch config provided, create a default one
    if (!config.type && !config.request && !config.name) {
      const editor = vscode.window.activeTextEditor;
      if (editor && editor.document.languageId === "ashes") {
        config.type = "ashes";
        config.name = "Ashes: Launch";
        config.request = "launch";
        config.program =
          "${workspaceFolder}/out/${workspaceFolderBasename}";
        config.cwd = "${workspaceFolder}";
        config.stopOnEntry = false;
      }
    }

    if (!config.program) {
      return vscode.window
        .showInformationMessage(
          "Cannot start debugging: no program specified in launch configuration. " +
            'Add a "program" property to your launch.json pointing to the compiled binary.',
        )
        .then((_) => undefined);
    }

    // Inject the debuggerType from the extension setting when not
    // explicitly provided in the launch configuration.
    if (!config.debuggerType) {
      const setting = vscode.workspace
        .getConfiguration("ashes")
        .get<string>("debugger", "gdb");
      config.debuggerType = setting;
    }

    return config;
  }
}
