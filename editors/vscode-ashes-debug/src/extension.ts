import * as vscode from 'vscode';
import * as path from 'path';

/**
 * VS Code extension entry point for Ashes debugging support.
 * Registers a DebugAdapterDescriptorFactory that launches the
 * ashes-dap server as the debug adapter process.
 */
export function activate(context: vscode.ExtensionContext): void {
    const factory = new AshesDebugAdapterFactory();
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory('ashes', factory)
    );

    // Register a configuration provider for auto-filling launch configs
    const provider = new AshesDebugConfigurationProvider();
    context.subscriptions.push(
        vscode.debug.registerDebugConfigurationProvider('ashes', provider)
    );
}

export function deactivate(): void {
    // Nothing to clean up
}

class AshesDebugAdapterFactory implements vscode.DebugAdapterDescriptorFactory {
    createDebugAdapterDescriptor(
        session: vscode.DebugSession,
        _executable: vscode.DebugAdapterExecutable | undefined
    ): vscode.ProviderResult<vscode.DebugAdapterDescriptor> {
        const config = session.configuration;
        const dapServerPath: string = config.dapServerPath || 'ashes-dap';

        return new vscode.DebugAdapterExecutable(dapServerPath, []);
    }
}

class AshesDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
    resolveDebugConfiguration(
        folder: vscode.WorkspaceFolder | undefined,
        config: vscode.DebugConfiguration,
        _token?: vscode.CancellationToken
    ): vscode.ProviderResult<vscode.DebugConfiguration> {
        // If no launch config provided, create a default one
        if (!config.type && !config.request && !config.name) {
            const editor = vscode.window.activeTextEditor;
            if (editor && editor.document.languageId === 'ashes') {
                config.type = 'ashes';
                config.name = 'Ashes: Launch';
                config.request = 'launch';
                config.program = '${workspaceFolder}/out/${workspaceFolderBasename}';
                config.cwd = '${workspaceFolder}';
                config.stopOnEntry = false;
            }
        }

        if (!config.program) {
            return vscode.window.showInformationMessage(
                'Cannot start debugging: no program specified in launch configuration. Add a "program" property to your launch.json pointing to the compiled binary.'
            ).then(_ => undefined);
        }

        return config;
    }
}
