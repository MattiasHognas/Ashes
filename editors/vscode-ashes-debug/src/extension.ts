import * as vscode from 'vscode';

/**
 * This extension is deprecated. Debug support is now integrated into the
 * main Ashes VS Code extension (ashes-vscode).
 *
 * This file is kept for reference only. If you have installed this extension
 * separately, uninstall it and use the main Ashes extension instead.
 */
export function activate(_context: vscode.ExtensionContext): void {
    vscode.window.showInformationMessage(
        'Ashes debugging is now built into the main Ashes extension. ' +
        'You can uninstall the separate "Ashes Debug" extension.'
    );
}

export function deactivate(): void {
    // Nothing to clean up
}
