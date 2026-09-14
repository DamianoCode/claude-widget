// Tests for scripts/settings.mjs — the part of the installer that edits the user's
// ~/.claude/settings.json. It must add exactly the widget's entries, never duplicate them,
// and never touch anything else.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

import { HOOK_EVENTS, withWidget, withoutWidget, isWidgetHook } from '../scripts/settings.mjs';

const SCRIPT = join(dirname(fileURLToPath(import.meta.url)), '..', 'scripts', 'settings.mjs');
const WIDGET_DIR = 'C:\\Users\\someone\\.claude\\widget';

const notifyHook = (kind) => ({
  type: 'command',
  command: 'powershell.exe',
  args: ['-NoProfile', '-File', 'C:\\Users\\someone\\.claude\\hooks\\notify.ps1', '-Kind', kind],
  async: true,
});

// The shape this machine had before the installer existed: widget hooks sharing a group with
// the user's own notification hook.
const HAND_MADE = {
  hooks: {
    Stop: [{ hooks: [notifyHook('done'), { type: 'command', command: 'node', args: [`${WIDGET_DIR}\\hook.mjs`], timeout: 5 }] }],
    PreToolUse: [{ matcher: 'Bash', hooks: [{ type: 'command', command: 'guard.sh' }] }],
  },
  statusLine: { type: 'command', command: 'node "C:/Users/someone/.claude/widget/statusline.mjs"' },
  theme: 'dark',
};

const widgetHooksIn = (settings, event) =>
  (settings.hooks?.[event] ?? []).flatMap((group) => group.hooks ?? []).filter(isWidgetHook);

test('installs one widget hook per event and the status line', () => {
  const { settings, foreignStatusLine } = withWidget({ theme: 'dark' }, WIDGET_DIR);
  assert.equal(foreignStatusLine, false);
  for (const event of Object.keys(HOOK_EVENTS)) {
    const hooks = widgetHooksIn(settings, event);
    assert.equal(hooks.length, 1, event);
    assert.deepEqual(hooks[0].args, [`${WIDGET_DIR}\\hook.mjs`]);
    assert.equal(hooks[0].command, 'node', 'exec form, so the hook is a direct child of Claude Code');
  }
  assert.equal(settings.statusLine.command, 'node "C:/Users/someone/.claude/widget/statusline.mjs"');
  assert.equal(settings.theme, 'dark');
});

test('installing twice changes nothing the second time', () => {
  const once = withWidget(HAND_MADE, WIDGET_DIR).settings;
  const twice = withWidget(once, WIDGET_DIR).settings;
  assert.deepEqual(twice, once);
  assert.equal(widgetHooksIn(twice, 'Stop').length, 1);
});

test('keeps the user own hooks, including those that shared a group with the widget', () => {
  const { settings } = withWidget(HAND_MADE, WIDGET_DIR);
  const stopHooks = settings.hooks.Stop.flatMap((group) => group.hooks);
  assert.ok(stopHooks.some((hook) => hook.args?.includes('done')), 'notify.ps1 -Kind done survives');
  assert.deepEqual(settings.hooks.PreToolUse, HAND_MADE.hooks.PreToolUse);
});

test('never overwrites a status line that is not the widget one', () => {
  const custom = { statusLine: { type: 'command', command: 'my-status.sh' } };
  const { settings, foreignStatusLine } = withWidget(custom, WIDGET_DIR);
  assert.equal(foreignStatusLine, true);
  assert.deepEqual(settings.statusLine, custom.statusLine);
});

test('uninstalling removes only the widget, down to empty groups and an empty hooks object', () => {
  const removed = withoutWidget(withWidget(HAND_MADE, WIDGET_DIR).settings);
  assert.deepEqual(removed.hooks.Stop, [{ hooks: [notifyHook('done')] }]);
  assert.deepEqual(removed.hooks.PreToolUse, HAND_MADE.hooks.PreToolUse);
  assert.equal(removed.statusLine, undefined);
  assert.equal(removed.theme, 'dark');
  for (const event of Object.keys(HOOK_EVENTS)) assert.equal(widgetHooksIn(removed, event).length, 0, event);

  assert.deepEqual(withoutWidget(withWidget({}, WIDGET_DIR).settings), {});
});

test('the command line edits the file, keeps a backup and reverts cleanly', () => {
  const dir = mkdtempSync(join(tmpdir(), 'claude-widget-settings-'));
  const path = join(dir, 'settings.json');
  const original = { theme: 'dark', hooks: { Stop: [{ hooks: [notifyHook('done')] }] } };
  writeFileSync(path, `\uFEFF${JSON.stringify(original, null, 2)}`); // a BOM some editors add

  const install = spawnSync(process.execPath, [SCRIPT, '--settings', path, '--widget-dir', WIDGET_DIR], { encoding: 'utf8' });
  assert.equal(install.status, 0, install.stderr);
  const installed = JSON.parse(readFileSync(path, 'utf8'));
  assert.equal(widgetHooksIn(installed, 'PermissionRequest').length, 1);
  assert.ok(readdirSync(dir).some((name) => name.startsWith('settings.json.bak-widget-')), 'a backup is kept');

  const uninstall = spawnSync(process.execPath, [SCRIPT, '--settings', path, '--remove'], { encoding: 'utf8' });
  assert.equal(uninstall.status, 0, uninstall.stderr);
  assert.deepEqual(JSON.parse(readFileSync(path, 'utf8')), original);
});

test('an unreadable settings file is left alone', () => {
  const dir = mkdtempSync(join(tmpdir(), 'claude-widget-settings-'));
  const path = join(dir, 'settings.json');
  writeFileSync(path, '{ "theme": "dark", ');
  const result = spawnSync(process.execPath, [SCRIPT, '--settings', path, '--widget-dir', WIDGET_DIR], { encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.equal(readFileSync(path, 'utf8'), '{ "theme": "dark", ');
  assert.deepEqual(readdirSync(dir), ['settings.json'], 'no backup, no write');
});
