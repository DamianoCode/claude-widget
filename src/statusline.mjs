#!/usr/bin/env node
// Statusline Claude Code, która przy okazji zasila widżet: zapisuje kontekst sesji
// i limity konta (5 h i tygodniowy), a do terminala wypisuje jedną krótką linię.
//
// Linia statusu musi się pokazać zawsze — nawet gdy zapis stanu widżetu się nie uda.

import { basename } from 'node:path';
import { readInput, sessionPath, limitsPath, writeJson, isAttendedSession } from './store.mjs';

const input = (await readInput()) ?? {};

try {
  if (isAttendedSession()) record(input, Date.now());
} catch {
  /* stan widżetu jest dodatkiem do linii statusu, nie jej warunkiem */
}

process.stdout.write(render(input));

function record(data, now) {
  const path = sessionPath(data.session_id, 'usage');
  if (path) {
    const context = data.context_window ?? {};
    writeJson(path, {
      name: typeof data.session_name === 'string' ? data.session_name : '',
      project: basename(String(data.workspace?.project_dir ?? data.cwd ?? '')),
      model: data.model?.display_name ?? '',
      contextPct: numberOrNull(context.used_percentage),
      contextTokens: numberOrNull(context.total_input_tokens),
      contextSize: numberOrNull(context.context_window_size),
      updatedAt: now,
    });
  }

  // Limity są wspólne dla całego konta, więc wystarczy ostatni odczyt z dowolnej sesji.
  // rate_limits przychodzi tylko w planach Pro i Max, i to po pierwszej odpowiedzi w sesji.
  const limits = data.rate_limits;
  if (limits?.five_hour || limits?.seven_day) {
    writeJson(limitsPath(), {
      fiveHour: limitWindow(limits.five_hour),
      sevenDay: limitWindow(limits.seven_day),
      updatedAt: now,
    });
  }
}

function render(data) {
  const parts = [];
  if (data.model?.display_name) parts.push(data.model.display_name);
  pushPercent(parts, 'kontekst', data.context_window?.used_percentage);
  pushPercent(parts, '5 h', data.rate_limits?.five_hour?.used_percentage);
  pushPercent(parts, 'tydzień', data.rate_limits?.seven_day?.used_percentage);
  return parts.join(' · ');
}

function pushPercent(parts, label, value) {
  if (typeof value === 'number' && Number.isFinite(value)) parts.push(`${label} ${Math.round(value)}%`);
}

function limitWindow(window) {
  return window ? { pct: numberOrNull(window.used_percentage), resetsAt: numberOrNull(window.resets_at) } : null;
}

function numberOrNull(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}
