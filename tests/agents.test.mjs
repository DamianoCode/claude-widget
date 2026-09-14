// Tests for src/agents.mjs — the bridge between `claude agents --json --all` and the widget.
// The listing comes from a fixture file instead of the real CLI, and time is passed in.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const SCRIPT = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'agents.mjs');
const HOUR = 3600 * 1000;

// Shapes as printed by Claude Code 2.1.270.
const interactive = (overrides = {}) => ({
  pid: 25296, cwd: 'C:\\apps\\claudius', kind: 'interactive', startedAt: 1000,
  sessionId: '5a5cb1ac-2aa8-460a-a0af-b8e980134376', name: 'claudius-1c', status: 'busy', ...overrides,
});
const background = (overrides = {}) => ({
  pid: 22808, id: '349831c7', cwd: 'C:\\apps\\api', kind: 'background', startedAt: 2000,
  sessionId: '349831c7-8a94-4af9-b019-a82efec35e51', name: 'create probe.txt file', status: 'busy', state: 'working', ...overrides,
});

function runner() {
  const dir = mkdtempSync(join(tmpdir(), 'claude-widget-agents-'));
  const fixture = join(dir, 'listing.json');
  return (listing, now) => {
    writeFileSync(fixture, typeof listing === 'string' ? listing : JSON.stringify(listing));
    const result = spawnSync(process.execPath, [SCRIPT, '--input', fixture, '--now', String(now)], {
      encoding: 'utf8',
      env: { ...process.env, CLAUDE_WIDGET_STATE_DIR: join(dir, 'state') },
    });
    assert.equal(result.status, 0, result.stderr);
    return JSON.parse(readFileSync(join(dir, 'state', 'agents.json'), 'utf8'));
  };
}

const byId = (output, sessionId) => output.sessions.find((session) => session.sessionId === sessionId);

test('lists terminal and background sessions with their state', () => {
  const run = runner();
  const output = run([interactive(), background()], 5000);
  assert.equal(output.ok, true);
  assert.equal(output.updatedAt, 5000);

  const terminal = byId(output, interactive().sessionId);
  assert.equal(terminal.kind, 'interactive');
  assert.equal(terminal.pid, 25296);
  assert.equal(terminal.status, 'busy');
  assert.equal(terminal.state, null);

  const bg = byId(output, background().sessionId);
  assert.equal(bg.kind, 'background');
  assert.equal(bg.id, '349831c7');
  assert.equal(bg.state, 'working');
  assert.equal(bg.since, 2000, 'a session first seen working has been working since it started');
});

test('a background session the widget watched finish is a new result, once', () => {
  const run = runner();
  run([background()], 5000);
  const finished = byId(run([background({ state: 'done', status: 'idle' })], 9000), background().sessionId);
  assert.equal(finished.fresh, true);
  assert.equal(finished.since, 9000);

  const later = byId(run([background({ state: 'done', status: 'idle' })], 60000), background().sessionId);
  assert.equal(later.fresh, true, 'still the same unseen result');
  assert.equal(later.since, 9000, 'the time it finished does not move');
});

test('a background session found already finished does not light green', () => {
  // After a reboot every old session would otherwise look like a brand-new result.
  const run = runner();
  const found = byId(run([background({ state: 'done', status: 'idle' })], 5000), background().sessionId);
  assert.equal(found.fresh, false);
});

test('a finished background session leaves the widget after 12 hours', () => {
  const run = runner();
  run([background()], 0);
  run([background({ state: 'done' })], 1000);
  assert.ok(byId(run([background({ state: 'done' })], 1000 + 11 * HOUR), background().sessionId));
  assert.equal(byId(run([background({ state: 'done' })], 1000 + 13 * HOUR), background().sessionId), undefined);
});

test('a blocked session says what it waits for, and a stopped one is dropped', () => {
  const run = runner();
  const output = run([
    // The value Claude Code 2.1.270 reported for a session blocked on AskUserQuestion.
    background({ state: 'blocked', status: 'waiting', waitingFor: 'input needed' }),
    background({ sessionId: 'aaaaaaaa-0000-0000-0000-000000000000', id: 'aaaaaaaa', state: 'blocked', waitingFor: { tool: 'Bash' } }),
    background({ sessionId: 'bbbbbbbb-0000-0000-0000-000000000000', id: 'bbbbbbbb', state: 'stopped' }),
    background({ sessionId: 'cccccccc-0000-0000-0000-000000000000', id: 'cccccccc', state: 'blocked', waitingFor: 'permission needed' }),
  ], 5000);
  assert.equal(byId(output, background().sessionId).waitingFor, 'Czeka na Twoją odpowiedź');
  assert.equal(byId(output, 'aaaaaaaa-0000-0000-0000-000000000000').waitingFor, 'Bash', 'an unknown value is shown as is');
  assert.equal(byId(output, 'cccccccc-0000-0000-0000-000000000000').waitingFor, 'Czeka na Twoją zgodę');
  assert.equal(byId(output, 'bbbbbbbb-0000-0000-0000-000000000000'), undefined);
});

test('a listing that cannot be read switches the feature off instead of keeping stale sessions', () => {
  const run = runner();
  run([background()], 5000);
  const broken = run('{ not json', 9000);
  assert.equal(broken.ok, false);
  assert.deepEqual(broken.sessions, []);
  assert.ok(broken.error.length > 0);
});
