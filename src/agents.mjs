#!/usr/bin/env node
// Lista sesji z `claude agents --json --all` dla widżetu. Daje dwie rzeczy, których hooki nie dają:
//   - sesje w tle (widok agentów, `claude --bg`, `/bg`, `/fork`) — ich hooki widżet pomija,
//     bo Claude Code oznacza je jako sesje bez obsługi,
//   - sesje w terminalu, które nie wysłały jeszcze żadnego zdarzenia do hooka.
// Widżet uruchamia go co kilka sekund w osobnym procesie; wynik trafia do state/agents.json.
//   --input <plik>  czyta listę z pliku zamiast wywoływać claude (testy)
//   --now <ms>      bieżący czas w milisekundach (testy)

import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { STATE_DIR, readJson, writeJson } from './store.mjs';

const AGENTS_PATH = join(STATE_DIR, 'agents.json');
const TIMEOUT_MS = 15000;
// Skończona sesja w tle zostaje na liście jeszcze przez 12 h, potem znika z widżetu
// (w widoku agentów zostaje, dopóki jej nie usuniesz).
const RECENT_MS = 12 * 3600 * 1000;
const WAITING_MAX = 70;

main();

function main() {
  const now = Number(argument('--now') ?? Date.now());
  const previous = readJson(AGENTS_PATH);
  try {
    const sessions = track(readListing(), Array.isArray(previous?.sessions) ? previous.sessions : [], now);
    writeJson(AGENTS_PATH, { ok: true, updatedAt: now, sessions });
  } catch (error) {
    // Bez listy widżet działa jak dawniej, na samych hookach.
    writeJson(AGENTS_PATH, { ok: false, updatedAt: now, error: String(error?.message ?? error).slice(0, 300), sessions: [] });
  }
}

function readListing() {
  const input = argument('--input');
  const text = input ? readFileSync(input, 'utf8') : runClaude();
  const listed = JSON.parse(text);
  if (!Array.isArray(listed)) throw new Error('claude agents --json nie zwrócił listy sesji');
  return listed;
}

function runClaude() {
  // Przez powłokę, bo claude bywa zainstalowany jako claude.exe albo jako claude.cmd z npm.
  const result = spawnSync('claude', ['agents', '--json', '--all'], {
    encoding: 'utf8',
    timeout: TIMEOUT_MS,
    windowsHide: true,
    shell: process.platform === 'win32',
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`claude agents: kod ${result.status} ${String(result.stderr ?? '').trim()}`);
  return result.stdout;
}

// Łączy bieżącą listę z poprzednią, żeby wiedzieć, od kiedy sesja jest w danym stanie
// i czy widżet widział, jak kończyła pracę.
export function track(listed, previous, now) {
  const before = new Map(previous.map((session) => [session.sessionId, session]));
  const sessions = [];

  for (const entry of listed) {
    if (!entry || typeof entry.sessionId !== 'string') continue;
    const background = entry.kind === 'background';
    const state = background ? String(entry.state ?? 'working') : null;
    if (state === 'stopped') continue; // zatrzymałeś ją sam

    const prior = before.get(entry.sessionId);
    const changed = !prior || prior.state !== state;
    const since = !changed ? prior.since : prior ? now : state === 'working' && entry.startedAt ? entry.startedAt : now;
    // Wynik jest nowy tylko wtedy, gdy widżet widział, jak sesja kończyła pracę. Sesja zastana
    // już skończona — np. po restarcie komputera — nie zapala zielonego.
    const fresh = state === 'done' ? (changed ? Boolean(prior) : prior.fresh === true) : false;
    if (background && (state === 'done' || state === 'failed') && now - since > RECENT_MS) continue;

    sessions.push({
      sessionId: entry.sessionId,
      id: typeof entry.id === 'string' ? entry.id : entry.sessionId.slice(0, 8),
      kind: background ? 'background' : 'interactive',
      pid: Number.isInteger(entry.pid) ? entry.pid : null,
      cwd: typeof entry.cwd === 'string' ? entry.cwd : '',
      name: typeof entry.name === 'string' ? entry.name : '',
      status: typeof entry.status === 'string' ? entry.status : '',
      startedAt: Number.isFinite(entry.startedAt) ? entry.startedAt : null,
      state,
      waitingFor: describeWaiting(entry.waitingFor),
      since,
      fresh,
    });
  }
  return sessions;
}

// `waitingFor` nazywa to, na co sesja czeka; dokumentacja nie podaje jego kształtu,
// więc przyjmuje się tekst albo obiekt z jednym z typowych pól.
function describeWaiting(waitingFor) {
  if (typeof waitingFor === 'string') return shorten(waitingFor.trim());
  if (waitingFor && typeof waitingFor === 'object') {
    const text = waitingFor.question ?? waitingFor.message ?? waitingFor.description ?? waitingFor.tool ?? waitingFor.type;
    if (typeof text === 'string') return shorten(text.trim());
  }
  return '';
}

function shorten(text) {
  return text.length > WAITING_MAX ? `${text.slice(0, WAITING_MAX - 1)}…` : text;
}

function argument(name) {
  const index = process.argv.indexOf(name);
  return index > -1 ? process.argv[index + 1] : undefined;
}
