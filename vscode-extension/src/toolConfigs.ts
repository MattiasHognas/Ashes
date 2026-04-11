import * as vscode from "vscode";
import type { ToolConfig } from "./toolAcquisition";

export const LSP_CONFIG: ToolConfig = {
  displayName: "Ashes language server",
  assetPrefix: "ashes-lsp",
  executableBaseName: "ashes-lsp",
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

export function getRequiredVersion(context: vscode.ExtensionContext): string {
  return (
    (context.extension.packageJSON as { version?: string }).version ?? "0.0.1"
  );
}
