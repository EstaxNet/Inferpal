import * as vscode from 'vscode';
import { HostClient } from './hostClient';
import { hostErrorText } from './hostStatus';
import { t } from './i18n';

/** The scope whose value wins for a setting (workspace over global), or undefined when neither is set. */
function explicitTarget<T>(inspected: { workspaceValue?: T; globalValue?: T } | undefined): vscode.ConfigurationTarget | undefined {
  if (inspected?.workspaceValue !== undefined) {
    return vscode.ConfigurationTarget.Workspace;
  }
  return inspected?.globalValue !== undefined ? vscode.ConfigurationTarget.Global : undefined;
}

/** The Model Router values VS Code's settings and the shared config last agreed on, by setting key. */
type Agreed = Record<string, string | boolean>;
const AGREED_KEY = 'inferpal.modelRouterAgreed';
const ROUTER_KEYS = ['utilityModel', 'modelRouterAuto'] as const;
let agreedState: vscode.Memento | undefined;

/** Called once at activation: where the last agreed values are kept (this workspace's state). */
export function initModelRouterSync(state: vscode.Memento): void {
  agreedState = state;
}

function readAgreed(): Agreed {
  return agreedState?.get<Agreed>(AGREED_KEY) ?? {};
}

async function recordAgreed(changes: Agreed): Promise<void> {
  await agreedState?.update(AGREED_KEY, { ...readAgreed(), ...changes });
}

/** Said, never only logged: a value that is not kept in step comes back changed at the next start. */
function sayNotKept(err: unknown, log: (line: string) => void): void {
  log(`[inferpal] model router settings not kept in step: ${String(err)}`);
  void vscode.window.showWarningMessage(
    t('The utility model setting could not be kept in step between VS Code and Inferpal ({0}) — it may change back at the next start. If a settings file has unsaved changes, save it, then save again.', hostErrorText(err)),
  );
}

/**
 * Writes the host's Model Router values (utility model, auto mode) back into the explicitly-set VS Code settings, after
 * anything changed them on the host side: the settings panel, or a slash command (`/onboard apply`). A setting never
 * touched in VS Code is left alone — the shared config already wins for it.
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
      await recordAgreed({ utilityModel: cfg.utilityModel });
    }
    const auto = config.inspect<boolean>('modelRouterAuto');
    const autoTarget = explicitTarget(auto);
    if (autoTarget !== undefined && cfg.modelRouterAuto !== undefined
        && (auto?.workspaceValue ?? auto?.globalValue) !== cfg.modelRouterAuto) {
      await config.update('modelRouterAuto', cfg.modelRouterAuto, autoTarget);
      await recordAgreed({ modelRouterAuto: cfg.modelRouterAuto });
    }
  } catch (err) {
    // VS Code refuses the write while a settings file has unsaved changes: the panel's value would be pushed back over
    // at the next start, with only a log line saying so.
    sayNotKept(err, log);
  }
}

/**
 * Keeps the explicitly-set Model Router settings and the shared config in step, at every host start and when the VS
 * Code setting changes — decided on what changed since they last AGREED, never by the VS Code value alone.
 *
 * ⚠ Pushed as it stood, the VS Code value was a copy of what the user last set THERE: a utility model picked in Visual
 * Studio since then was set back at the next start of VS Code, without a word. Changed in VS Code since they agreed: it
 * is pushed. Unchanged in VS Code but changed in the shared config: the VS Code setting follows. Read-modify-write on
 * the full JSON: `config/update` replaces the whole object.
 */
export async function syncModelRouterSettings(host: HostClient | undefined, log: (line: string) => void): Promise<void> {
  if (!host) {
    return;
  }
  const config = vscode.workspace.getConfiguration('inferpal');
  try {
    const cfg = JSON.parse(await host.configGet()) as Record<string, unknown>;
    const agreed = readAgreed();
    const now: Agreed = {};
    let push = false;
    for (const key of ROUTER_KEYS) {
      const inspected = config.inspect<string | boolean>(key);
      const target = explicitTarget(inspected);
      const mine = inspected?.workspaceValue ?? inspected?.globalValue;
      if (target === undefined || mine === undefined) {
        continue;   // never set in VS Code: the shared config wins
      }
      const shared = cfg[key] as string | boolean | undefined;
      if (mine === shared) {
        now[key] = mine;
      } else if (agreed[key] !== undefined && mine === agreed[key] && shared !== undefined) {
        // Unchanged here, changed in the shared config since they agreed (Visual Studio, the panel): followed.
        await config.update(key, shared, target);
        now[key] = shared;
        log(`[inferpal] ${key} follows the shared config → ${String(shared)}`);
      } else {
        cfg[key] = mine;
        push = true;
        now[key] = mine;
        log(`[inferpal] ${key} → ${String(mine)}`);
      }
    }
    if (push) {
      await host.configUpdate(JSON.stringify(cfg));
    }
    await recordAgreed(now);
  } catch (err) {
    sayNotKept(err, log);
  }
}
