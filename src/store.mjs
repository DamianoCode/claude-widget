// Wspólne dla hooka i statusline: gdzie leży stan widżetu i jak się go zapisuje.
// Każda sesja ma dwa pliki, po jednym na piszącego (hook i statusline), więc nigdy nie
// nadpisują sobie nawzajem pól.

import { readFileSync, writeFileSync, renameSync, mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';

export const STATE_DIR = process.env.CLAUDE_WIDGET_STATE_DIR || join(homedir(), '.claude', 'widget', 'state');

const RENAME_ATTEMPTS = 5;
const RENAME_BACKOFF_MS = 15;

export async function readInput() {
  // Bufory skleja się przed dekodowaniem: znak wielobajtowy na granicy kawałków
  // rozpadłby się przy dekodowaniu każdego kawałka osobno.
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  try {
    const value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    return value && typeof value === 'object' ? value : null;
  } catch {
    return null;
  }
}

// Id sesji staje się nazwą pliku, więc zostają w nim tylko bezpieczne znaki.
export function sessionPath(sessionId, kind) {
  const safe = String(sessionId ?? '').replace(/[^A-Za-z0-9_-]/g, '');
  return safe ? join(STATE_DIR, `${safe}.${kind}.json`) : null;
}

export function limitsPath() {
  return join(STATE_DIR, 'limits.json');
}

export function readJson(path) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    return null;
  }
}

// Zapis do pliku tymczasowego i podmiana nazwą: widżet nigdy nie przeczyta połowy pliku.
// Podmiana może chwilowo nie przejść na Windows, gdy ktoś akurat trzyma plik otwarty.
export function writeJson(path, value) {
  mkdirSync(STATE_DIR, { recursive: true });
  const tmp = `${path}.${process.pid}.tmp`;
  writeFileSync(tmp, JSON.stringify(value), 'utf8');
  for (let attempt = 1; ; attempt += 1) {
    try {
      renameSync(tmp, path);
      return;
    } catch (error) {
      if (attempt >= RENAME_ATTEMPTS) {
        rmSync(tmp, { force: true });
        throw error;
      }
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, RENAME_BACKOFF_MS);
    }
  }
}

export function removeFile(path) {
  if (!path) return;
  try {
    rmSync(path, { force: true });
  } catch {
    /* plik mógł już zniknąć */
  }
}

// Sesje uruchamiane ze skryptów (`claude -p`, SDK) i bez obsługi nie trafiają do widżetu —
// ta sama reguła co w hooks/notify.ps1.
export function isAttendedSession(env = process.env) {
  const entry = env.CLAUDE_CODE_ENTRYPOINT;
  if (entry && entry !== 'cli') return false;
  return env.CLAUDE_CODE_SESSION_ATTENDED !== '0';
}
