import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";
import { type ToolConfig, acquireTool } from "./toolAcquisition";
import {
  compileCommand,
  runCommand,
  testCommand,
  installToolchainCommand,
  disposeCommands,
} from "./commands";

let client: LanguageClient | undefined;
let clientStarting = false;

export const LSP_CONFIG: ToolConfig = {
  displayName: "Ashes language server",
  assetPrefix: "ashes-lsp",
  executableBaseName: "Ashes.Lsp",
  cacheSubdir: "lsp",
  bundledSubdir: "lsp-server",
  settingKey: "ashes.lspServerPath",
};

export const DAP_CONFIG: ToolConfig = {
  displayName: "Ashes DAP server",
  assetPrefix: "ashes-dap",
  executableBaseName: "ashes-dap",
  cacheSubdir: "dap",
  bundledSubdir: "dap-server",
  settingKey: "ashes.dapServerPath",
};

export function getRequiredVersion(
  context: vscode.ExtensionContext,
): string {
  return (
    (context.extension.packageJSON as { version?: string }).version ?? "0.0.1"
  );
}

/**
 * Acquire the LSP binary on demand, start the LanguageClient once, and
 * reuse it on subsequent calls.  Does nothing in untrusted workspaces.
 */
async function ensureLanguageClientStarted(
  context: vscode.ExtensionContext,
): Promise<void> {
  if (client || clientStarting) {
    return;
  }

  if (!vscode.workspace.isTrusted) {
    void vscode.window.showWarningMessage(
      "Ashes language server requires a trusted workspace.",
    );
    return;
  }

  clientStarting = true;
  try {
    const requiredVersion = getRequiredVersion(context);
    const lspPath = await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: `Downloading ${LSP_CONFIG.displayName}…`,
        cancellable: false,
      },
      () => acquireTool(context, LSP_CONFIG, requiredVersion),
    );

    const serverOptions: ServerOptions = {
      command: lspPath,
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
  } catch (err) {
    void vscode.window.showErrorMessage(
      `Failed to start Ashes language server: ${(err as Error).message}`,
    );
    client = undefined;
  } finally {
    clientStarting = false;
  }
}

export function activate(
  context: vscode.ExtensionContext,
): void {
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
  context.subscriptions.push(
    vscode.commands.registerCommand("ashes.installToolchain", () =>
      installToolchainCommand(context),
    ),
  );

  // Register debug adapter — acquires DAP binary on demand
  const dapFactory = new AshesDebugAdapterFactory(context);
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory("ashes", dapFactory),
  );

  const debugConfigProvider = new AshesDebugConfigurationProvider();
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider(
      "ashes",
      debugConfigProvider,
    ),
  );

  // Lazy-start LSP when an Ashes document is opened
  context.subscriptions.push(
    vscode.workspace.onDidOpenTextDocument((doc) => {
      if (doc.languageId === "ashes") {
        void ensureLanguageClientStarted(context);
      }
    }),
  );

  // Check already-open documents
  for (const doc of vscode.workspace.textDocuments) {
    if (doc.languageId === "ashes") {
      void ensureLanguageClientStarted(context);
      break;
    }
  }
}

export async function deactivate(): Promise<void> {
  if (client) {
    await client.stop();
    client = undefined;
  }
  disposeCommands();
}

class AshesDebugAdapterFactory implements vscode.DebugAdapterDescriptorFactory {
  private readonly context: vscode.ExtensionContext;

  constructor(context: vscode.ExtensionContext) {
    this.context = context;
  }

  async createDebugAdapterDescriptor(
    _session: vscode.DebugSession,
    _executable: vscode.DebugAdapterExecutable | undefined,
  ): Promise<vscode.DebugAdapterDescriptor | undefined> {
    if (!vscode.workspace.isTrusted) {
      void vscode.window.showErrorMessage(
        "Ashes DAP server requires a trusted workspace.",
      );
      return undefined;
    }

    const requiredVersion = getRequiredVersion(this.context);
    let dapPath: string;
    try {
      dapPath = await vscode.window.withProgress(
        {
          location: vscode.ProgressLocation.Notification,
          title: `Downloading ${DAP_CONFIG.displayName}…`,
          cancellable: false,
        },
        () => acquireTool(this.context, DAP_CONFIG, requiredVersion),
      );
    } catch (err) {
      void vscode.window.showErrorMessage(
        `Failed to acquire Ashes DAP server: ${(err as Error).message}`,
      );
      return undefined;
    }

    return new vscode.DebugAdapterExecutable(dapPath, []);
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
        config.program = "${workspaceFolder}/out/${workspaceFolderBasename}";
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
        .then(() => undefined);
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
