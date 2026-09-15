// Protokół plikowy między widżetem Claude Code a rozszerzeniem — w katalogu vscode obok stanu widżetu:
//   windows/<id>.json   stan okna VS Code (pisze rozszerzenie, czyta widżet)
//   focus-request.json  prośba o pokazanie terminala (pisze widżet, czytają wszystkie okna)
// Pliki zamiast linku vscode://: link trafia do ostatnio aktywnego okna, a terminal sesji może być
// w innym — prośbę widzi każde okno, a obsługuje tylko to, które ma terminal z tą powłoką.

import * as path from 'node:path';

/** Prośba widżetu: pokaż terminal, którego powłoka jest na liście PID-ów (claude.exe i jego przodkowie). */
export interface FocusRequest {
  version: number;
  id: string;
  pids: number[];
  at: number;
}

/** Stan okna dla widżetu: gdzie są terminale, który jest aktywny i czy okno ma fokus. */
export interface WindowState {
  version: 1;
  extensionHostPid: number;
  workspaceFolders: string[];
  focused: boolean;
  activeTerminalPid: number | null;
  terminalPids: number[];
  updatedAt: number;
}

/** Starsza prośba jest nieaktualna — np. zapisana, zanim okno się otworzyło. */
export const REQUEST_MAX_AGE_MS = 10_000;

/** Ten sam katalog, którego używa widżet: obok katalogu stanu (CLAUDE_WIDGET_STATE_DIR) albo ~/.claude/widget. */
export function bridgeDir(env: NodeJS.ProcessEnv, home: string): string {
  const stateDir = env.CLAUDE_WIDGET_STATE_DIR;
  return stateDir ? path.join(path.dirname(stateDir), 'vscode') : path.join(home, '.claude', 'widget', 'vscode');
}

/** Prośba z pliku albo null, gdy jest niepoprawna albo nieaktualna. */
export function parseRequest(value: unknown, now: number): FocusRequest | null {
  if (!value || typeof value !== 'object') return null;
  const request = value as Record<string, unknown>;
  if (typeof request.id !== 'string' || typeof request.at !== 'number' || !Array.isArray(request.pids)) return null;
  const pids = request.pids.filter((pid): pid is number => Number.isInteger(pid) && (pid as number) > 0);
  if (pids.length === 0 || Math.abs(now - request.at) > REQUEST_MAX_AGE_MS) return null;
  return { version: typeof request.version === 'number' ? request.version : 1, id: request.id, pids, at: request.at };
}

/** Terminal, którego proces (powłoka albo sam claude) jest na liście PID-ów sesji. */
export function findTerminal<T>(terminals: ReadonlyArray<{ terminal: T; pid: number | undefined }>, pids: readonly number[]): T | undefined {
  return terminals.find((entry) => entry.pid !== undefined && pids.includes(entry.pid))?.terminal;
}
