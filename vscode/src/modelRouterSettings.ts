import * as vscode from 'vscode';
import { HostClient } from './hostClient';

/** The scope whose value wins for a setting (workspace over global), or undefined when neither is set. */
function explicitTarget<T>(inspected: { workspaceValue?: T; globalValue?: T } | undefined): vscode.ConfigurationTarget | undefined {
  if (inspected?.workspaceValue !== undefined) {
    return vscode.ConfigurationTarget.Workspace;
  }
  return inspected?.globalValue !== undefined ? vscode.ConfigurationTarget.Global : undefined;
}

/**
 * Writes the host's Model Router values (utility model, auto mode) back into the explicitly-set VS Code settings, after
 * anything changed them on the host side: the settings panel, or a slash command (`/onboard apply`). The explicit
 * VS Code values are pushed into the shared config at every host start, so a value changed elsewhere is silently
 * reverted the next time unless it is followed here. A setting never touched in VS Code is left alone — the shared
 * config already wins for it.
 */
export async function followModelRouterSettings(host: HostClient | undefined, log: (line: string) => void): Promise<void> {
  if (!host) {
    return;
  }
  const config = vscode.workspace.getConfiguration('inferpal');
  try {
    const cfg = JSON.parse(await host.configGet()) as { utilityModel?: string; modelRouterAuto?: boolean };
    const utility = config.inspect<string>('utilityModel');
    const utilityTarget = explicitTarget(utility);
    if (utilityTarget !== undefined && cfg.utilityModel !== undefined
        && (utility?.workspaceValue ?? utility?.globalValue) !== cfg.utilityModel) {
      await config.update('utilityModel', cfg.utilityModel, utilityTarget);
    }
    const auto = config.inspect<boolean>('modelRouterAuto');
    const autoTarget = explicitTarget(auto);
    if (autoTarget !== undefined && cfg.modelRouterAuto !== undefined
        && (auto?.workspaceValue ?? auto?.globalValue) !== cfg.modelRouterAuto) {
      await config.update('modelRouterAuto', cfg.modelRouterAuto, autoTarget);
    }
  } catch (err) {
    log(`[inferpal] model router settings follow failed: ${String(err)}`);
  }
}
