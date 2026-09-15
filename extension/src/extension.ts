// Most między widżetem Claude Code a terminalami VS Code. Widżet po kliknięciu sesji wyciąga okno
// VS Code na wierzch (tylko on może, bo to w niego kliknąłeś) i zapisuje prośbę z PID-ami procesów
// sesji; rozszerzenie w oknie, które ma terminal z tą powłoką, pokazuje go. Stan okna — terminale,
// aktywny terminal, fokus — zapisuje dla widżetu, żeby wiedział, gdzie jest sesja i czy na nią patrzysz.

import * as vscode from 'vscode';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { randomUUID } from 'node:crypto';
import { bridgeDir, findTerminal, parseRequest, PID_TIMEOUT_MS, withTimeout, type WindowState } from './protocol';

export function activate(context: vscode.ExtensionContext): void {
  const dir = bridgeDir(process.env, os.homedir());
  const windowsDir = path.join(dir, 'windows');
  const stateFile = path.join(windowsDir, `${randomUUID()}.json`);
  const requestFile = path.join(dir, 'focus-request.json');
  const handled = new Set<string>();
  fs.mkdirSync(windowsDir, { recursive: true });

  // Zapisy po kolei: processId terminali przychodzi asynchronicznie, a starszy zapis nie może
  // nadpisać nowszego.
  let queue: Promise<void> = Promise.resolve();
  const saveState = (): void => {
    queue = queue
      .then(async () => {
        const terminals = await terminalsWithPids();
        const active = vscode.window.activeTerminal;
        const state: WindowState = {
          version: 1,
          extensionHostPid: process.pid,
          workspaceFolders: (vscode.workspace.workspaceFolders ?? []).map((folder) => folder.uri.fsPath),
          focused: vscode.window.state.focused,
          activeTerminalPid: terminals.find((entry) => entry.terminal === active)?.pid ?? null,
          terminalPids: terminals.flatMap((entry) => (entry.pid === undefined ? [] : [entry.pid])),
          updatedAt: Date.now(),
        };
        writeAtomic(stateFile, state);
      })
      .catch(() => undefined);
  };

  const handleRequest = async (): Promise<void> => {
    const request = parseRequest(readJson(requestFile), Date.now());
    if (!request || handled.has(request.id)) return;
    const terminal = findTerminal(await terminalsWithPids(), request.pids);
    if (!terminal) return;
    handled.add(request.id);
    terminal.show(false);
  };

  let watcher: fs.FSWatcher | undefined;
  try {
    watcher = fs.watch(dir, (_event, name) => {
      if (name === 'focus-request.json') void handleRequest().catch(() => undefined);
    });
  } catch {
    // bez obserwacji katalogu rozszerzenie tylko nie przełącza terminala; stan okna i tak zapisuje
  }

  saveState();
  context.subscriptions.push(
    vscode.window.onDidOpenTerminal(saveState),
    vscode.window.onDidCloseTerminal(saveState),
    vscode.window.onDidChangeActiveTerminal(saveState),
    vscode.window.onDidChangeWindowState(saveState),
    vscode.workspace.onDidChangeWorkspaceFolders(saveState),
    {
      dispose: () => {
        watcher?.close();
        fs.rmSync(stateFile, { force: true });
      },
    },
  );
}

export function deactivate(): void {
  // sprzątanie robią subskrypcje z activate
}

function terminalsWithPids(): Promise<Array<{ terminal: vscode.Terminal; pid: number | undefined }>> {
  return Promise.all(
    vscode.window.terminals.map(async (terminal) => ({ terminal, pid: await withTimeout(terminal.processId, PID_TIMEOUT_MS) })),
  );
}

function readJson(file: string): unknown {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

// Zapis i podmiana nazwą: widżet nigdy nie przeczyta połowy pliku.
function writeAtomic(file: string, value: unknown): void {
  const temporary = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(temporary, JSON.stringify(value), 'utf8');
  fs.renameSync(temporary, file);
}
