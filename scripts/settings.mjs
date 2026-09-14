#!/usr/bin/env node
// Dopisuje hooki i statusline widżetu do ~/.claude/settings.json albo je usuwa (--remove).
// Rusza wyłącznie wpisy widżetu — rozpoznaje je po ścieżce .claude/widget/ — a przed zapisem
// zostawia kopię pliku obok. Uruchamiany przez install.ps1 i uninstall.ps1.
//   --remove            usuwa wpisy widżetu zamiast je dopisywać
//   --widget-dir <dir>  gdzie leżą pliki widżetu (domyślnie ~/.claude/widget)
//   --settings <plik>   który settings.json zmienić (domyślnie ~/.claude/settings.json)

import { readFileSync, writeFileSync, copyFileSync, existsSync, mkdirSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { homedir } from 'node:os';
import { pathToFileURL } from 'node:url';

// Zdarzenia, na które reaguje hook. Te po wykonaniu narzędzia idą w tle (async): zdarzają się
// przy każdym narzędziu, a stan zmieniają tylko z „czeka” na „pracuje”.
export const HOOK_EVENTS = {
  SessionStart: {},
  UserPromptSubmit: {},
  PermissionRequest: {},
  PermissionDenied: { async: true },
  PostToolUse: { async: true },
  PostToolUseFailure: { async: true },
  PostToolBatch: { async: true },
  Notification: {},
  Stop: {},
  StopFailure: {},
  SessionEnd: {},
};

const HOOK_MARK = '.claude/widget/hook.mjs';
const STATUS_MARK = '.claude/widget/statusline.mjs';

const normalize = (text) => String(text ?? '').replace(/\\/g, '/');

export function isWidgetHook(hook) {
  const args = Array.isArray(hook?.args) ? hook.args : [];
  return normalize([hook?.command, ...args].join(' ')).includes(HOOK_MARK);
}

export function isWidgetStatusLine(statusLine) {
  return normalize(statusLine?.command).includes(STATUS_MARK);
}

export function withoutWidget(settings) {
  const next = structuredClone(settings ?? {});
  if (next.hooks && typeof next.hooks === 'object') {
    for (const [event, groups] of Object.entries(next.hooks)) {
      if (!Array.isArray(groups)) continue;
      const kept = groups
        .map((group) => (Array.isArray(group?.hooks) ? { ...group, hooks: group.hooks.filter((hook) => !isWidgetHook(hook)) } : group))
        .filter((group) => !Array.isArray(group?.hooks) || group.hooks.length > 0);
      if (kept.length) next.hooks[event] = kept;
      else delete next.hooks[event];
    }
    if (!Object.keys(next.hooks).length) delete next.hooks;
  }
  if (isWidgetStatusLine(next.statusLine)) delete next.statusLine;
  return next;
}

// Najpierw zdejmuje stare wpisy widżetu, więc ponowna instalacja niczego nie dubluje.
// Cudzej statusline nie nadpisuje — bez niej widżet nie zna tylko limitów i kontekstu.
export function withWidget(settings, widgetDir) {
  const next = withoutWidget(settings);
  next.hooks ??= {};
  const hookPath = join(widgetDir, 'hook.mjs');
  for (const [event, extra] of Object.entries(HOOK_EVENTS)) {
    const groups = Array.isArray(next.hooks[event]) ? next.hooks[event] : [];
    groups.push({ hooks: [{ type: 'command', command: 'node', args: [hookPath], timeout: 5, ...extra }] });
    next.hooks[event] = groups;
  }
  const foreignStatusLine = Boolean(next.statusLine);
  if (!foreignStatusLine) {
    next.statusLine = { type: 'command', command: `node "${normalize(join(widgetDir, 'statusline.mjs'))}"` };
  }
  return { settings: next, foreignStatusLine };
}

function argument(name) {
  const index = process.argv.indexOf(name);
  return index > -1 ? process.argv[index + 1] : undefined;
}

function writeSettings(path, value) {
  writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

function main() {
  const remove = process.argv.includes('--remove');
  const widgetDir = resolve(argument('--widget-dir') ?? join(homedir(), '.claude', 'widget'));
  const path = resolve(argument('--settings') ?? join(homedir(), '.claude', 'settings.json'));

  let current = {};
  if (existsSync(path)) {
    try {
      current = JSON.parse(readFileSync(path, 'utf8').replace(/^﻿/, ''));
    } catch (error) {
      console.error(`Nie da się odczytać ${path}: ${error.message}. Niczego nie zmieniono.`);
      process.exit(1);
    }
    const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
    copyFileSync(path, `${path}.bak-widget-${stamp}`);
  } else {
    mkdirSync(dirname(path), { recursive: true });
  }

  if (remove) {
    writeSettings(path, withoutWidget(current));
    console.log(`Usunięto hooki i statusline widżetu z ${path}`);
    return;
  }

  const { settings, foreignStatusLine } = withWidget(current, widgetDir);
  writeSettings(path, settings);
  console.log(`Dopisano hooki widżetu do ${path}`);
  if (foreignStatusLine) {
    console.log('Masz już własną statusline — zostawiono ją. Dopóki nie wywołasz w niej statusline.mjs widżetu, nie pokaże on limitów ani kontekstu (patrz README).');
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main();
