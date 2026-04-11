import * as path from "path";
import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";
import { acquireTool } from "./toolAcquisition";
import { LSP_CONFIG, DAP_CONFIG, getRequiredVersion } from "./toolConfigs";
import {
  compileCommand,
  runCommand,
  testCommand,
  installToolchainCommand,
  disposeCommands,
} from "./commands";

export { LSP_CONFIG, DAP_CONFIG, getRequiredVersion };

let client: LanguageClient | undefined;
let clientStarting = false;

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

export function activate(context: vscode.ExtensionContext): void {
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

  // Lazy-start LSP when an Ashes document is opened (if auto-start enabled)
  const autoStart = vscode.workspace
    .getConfiguration("ashes")
    .get<boolean>("autoStartLanguageServer", true);

  if (autoStart) {
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
}

export async function deactivate(): Promise<void> {
  if (client) {
    await client.stop();
    client = undefined;
  }
  disposeCommands();
}

function getDefaultDebugProgramPath(filePath: string): string {
  const parsedPath = path.parse(filePath);
  const executableExtension = process.platform === "win32" ? ".exe" : "";
  return path.join(parsedPath.dir, `${parsedPath.name}${executableExtension}`);
}

function resolveDebugProgramPath(
  folder: vscode.WorkspaceFolder | undefined,
  program: string,
): string {
  if (path.isAbsolute(program)) {
    return program;
  }

  if (folder) {
    return path.join(folder.uri.fsPath, program);
  }

  return path.resolve(program);
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
        config.program = getDefaultDebugProgramPath(editor.document.uri.fsPath);
        config.cwd = path.dirname(editor.document.uri.fsPath);
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

  async resolveDebugConfigurationWithSubstitutedVariables(
    folder: vscode.WorkspaceFolder | undefined,
    config: vscode.DebugConfiguration,
    _token?: vscode.CancellationToken,
  ): Promise<vscode.DebugConfiguration | undefined> {
    if (!config.program || typeof config.program !== "string") {
      return config;
    }

    const resolvedProgramPath = resolveDebugProgramPath(folder, config.program);

    try {
      const fileInfo = await vscode.workspace.fs.stat(
        vscode.Uri.file(resolvedProgramPath),
      );

      if ((fileInfo.type & vscode.FileType.Directory) !== 0) {
        throw new Error("Program path points to a directory.");
      }
    } catch {
      const displayPath = folder
        ? path.relative(folder.uri.fsPath, resolvedProgramPath) || "."
        : resolvedProgramPath;

      void vscode.window.showErrorMessage(
        `Cannot start debugging: program '${displayPath}' does not exist. Compile with '--debug' first, or set launch.json \"program\" to the compiled binary.`,
      );
      return undefined;
    }

    return config;
  }
}
