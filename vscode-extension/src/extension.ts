import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";
import { type ToolConfig, acquireTool } from "./toolAcquisition";
import { acquireCompiler } from "./compilerAcquisition";
import {
  compileCommand,
  runCommand,
  testCommand,
  disposeCommands,
} from "./commands";

let client: LanguageClient | undefined;

const LSP_CONFIG: ToolConfig = {
  displayName: "Ashes language server",
  assetPrefix: "ashes-lsp",
  executableBaseName: "Ashes.Lsp",
  cacheSubdir: "lsp",
  bundledSubdir: "lsp-server",
};

const DAP_CONFIG: ToolConfig = {
  displayName: "Ashes DAP server",
  assetPrefix: "ashes-dap",
  executableBaseName: "ashes-dap",
  cacheSubdir: "dap",
  bundledSubdir: "dap-server",
};

function getRequiredVersion(context: vscode.ExtensionContext): string {
  return (
    (context.extension.packageJSON as { version?: string }).version ?? "0.0.1"
  );
}

export async function activate(
  context: vscode.ExtensionContext,
): Promise<void> {
  const requiredVersion = getRequiredVersion(context);

  // Acquire LSP server — blocks activation behind a progress bar.
  let lspPath: string;
  try {
    lspPath = await acquireTool(context, LSP_CONFIG, requiredVersion);
  } catch (err) {
    vscode.window.showErrorMessage(
      `Failed to acquire ${LSP_CONFIG.displayName} v${requiredVersion}: ${(err as Error).message}`,
    );
    return;
  }

  // Acquire DAP server and compiler eagerly in the background.
  // Failures show a notification but do not block the language server.
  const dapReady = acquireTool(context, DAP_CONFIG, requiredVersion).catch(
    (err: unknown) => {
      vscode.window.showErrorMessage(
        `Failed to acquire ${DAP_CONFIG.displayName} v${requiredVersion}: ${(err as Error).message}`,
      );
      return undefined;
    },
  );

  acquireCompiler(context, requiredVersion)
    .then((compilerPath: string) => {
      void context.workspaceState.update("ashes.compilerPath", compilerPath);
    })
    .catch((err: unknown) => {
      vscode.window.showErrorMessage(
        `Failed to acquire Ashes compiler v${requiredVersion}: ${(err as Error).message}`,
      );
    });

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

  // Register debug adapter — acquires the DAP server binary on demand
  const dapFactory = new AshesDebugAdapterFactory(context, dapReady);
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
}

export async function deactivate(): Promise<void> {
  if (client) {
    await client.stop();
    client = undefined;
  }
  disposeCommands();
}

class AshesDebugAdapterFactory implements vscode.DebugAdapterDescriptorFactory {
  private readonly dapReady: Promise<string | undefined>;

  constructor(
    _context: vscode.ExtensionContext,
    dapReady: Promise<string | undefined>,
  ) {
    this.dapReady = dapReady;
  }

  async createDebugAdapterDescriptor(
    _session: vscode.DebugSession,
    _executable: vscode.DebugAdapterExecutable | undefined,
  ): Promise<vscode.DebugAdapterDescriptor | undefined> {
    const dapPath = await this.dapReady;
    if (!dapPath) {
      vscode.window.showErrorMessage(
        "Ashes DAP server is not available. Check earlier error notifications for details.",
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
