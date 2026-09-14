// Behavior tests for src/hook.mjs and src/statusline.mjs, driven as subprocesses the way
// Claude Code invokes them, against a throwaway state directory.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, existsSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const SRC = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

function run(script, payload, dir, env = {}) {
  return spawnSync(process.execPath, [join(SRC, script)], {
    input: typeof payload === 'string' ? payload : JSON.stringify(payload),
    encoding: 'utf8',
    env: { ...process.env, CLAUDE_WIDGET_STATE_DIR: dir, CLAUDE_CODE_ENTRYPOINT: 'cli', CLAUDE_CODE_SESSION_ATTENDED: '1', ...env },
  });
}

const freshDir = () => mkdtempSync(join(tmpdir(), 'claude-widget-'));
const read = (dir, name) => JSON.parse(readFileSync(join(dir, name), 'utf8'));

function hookEvents(dir, sessionId) {
  return (name, extra = {}) => {
    const result = run('hook.mjs', { hook_event_name: name, session_id: sessionId, cwd: 'C:\\apps\\api', ...extra }, dir);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout, '', 'the hook must print nothing — PermissionRequest reads output as a decision');
    return existsSync(join(dir, `${sessionId}.state.json`)) ? read(dir, `${sessionId}.state.json`) : null;
  };
}

const BASH = { tool_name: 'Bash', tool_input: { command: 'npm test', description: 'Run tests' } };

test('a full turn with a permission prompt walks gotowe → pracuje → czeka → pracuje → gotowe', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 's1');

  const started = event('SessionStart');
  assert.equal(started.state, 'gotowe');
  assert.equal(started.fresh, false, 'a new session is idle, not a result to review');

  const working = event('UserPromptSubmit');
  assert.equal(working.state, 'pracuje');
  assert.equal(working.cwd, 'C:\\apps\\api');

  const waiting = event('PermissionRequest', BASH);
  assert.equal(waiting.state, 'czeka');
  assert.equal(waiting.detail, 'Zgoda: Bash · npm test');

  // A different tool finishing during the prompt must not turn the red light off.
  assert.equal(event('PostToolUse', { tool_name: 'Read', tool_input: { file_path: 'a.txt' }, tool_response: {} }).state, 'czeka');

  const resumed = event('PostToolUse', { ...BASH, tool_response: { stdout: 'ok' } });
  assert.equal(resumed.state, 'pracuje');
  assert.equal(resumed.since, working.turnStartedAt, 'the working timer continues from the start of the turn');

  const done = event('Stop', { background_tasks: [] });
  assert.equal(done.state, 'gotowe');
  assert.equal(done.fresh, true, 'a finished turn is a result to review');
  assert.equal(event('Notification', { notification_type: 'idle_prompt' }).fresh, true, 'idle_prompt does not mark a result as seen');

  assert.equal(event('SessionEnd', { reason: 'other' }), null);
});

test('an answered AskUserQuestion turns the light back to working', () => {
  // Regression: the answers are merged into tool_input before PostToolUse, so a comparison of
  // the whole input never matched and the session stayed red after the user had answered.
  const dir = freshDir();
  const event = hookEvents(dir, 'q1');
  event('UserPromptSubmit');
  const questions = [{ question: 'Push blokuje uruchomione api:serve. Co robimy?', header: 'api:serve', options: [{ label: 'Zatrzymaj' }, { label: 'Sam zatrzymam' }], multiSelect: false }];

  const asking = event('PermissionRequest', { tool_name: 'AskUserQuestion', tool_input: { questions } });
  assert.equal(asking.state, 'czeka');
  assert.equal(asking.detail, 'Pytanie: Push blokuje uruchomione api:serve. Co robimy?');

  const answered = event('PostToolUse', {
    tool_name: 'AskUserQuestion',
    tool_input: { questions, answers: { 'Push blokuje uruchomione api:serve. Co robimy?': 'Zatrzymaj' } },
    tool_response: {},
  });
  assert.equal(answered.state, 'pracuje');
});

test('a permitted tool that fails still ends the wait', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 'f1');
  event('UserPromptSubmit');
  event('PermissionRequest', BASH);
  assert.equal(event('PostToolUseFailure', { ...BASH, error: 'exit code 1' }).state, 'pracuje');
});

test('a denied permission ends the wait when the batch resolves', () => {
  // A denial produces no PostToolUse at all; the batch event is the only sign.
  const dir = freshDir();
  const event = hookEvents(dir, 'd1');
  event('UserPromptSubmit');
  event('PermissionRequest', BASH);
  assert.equal(event('PostToolBatch', { tool_calls: [{ ...BASH, tool_use_id: 't1', tool_response: 'denied' }] }).state, 'pracuje');
});

test('an auto-mode denial ends the wait', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 'd2');
  event('UserPromptSubmit');
  event('PermissionRequest', BASH);
  assert.equal(event('PermissionDenied', { ...BASH, tool_use_id: 't1', reason: 'classifier' }).state, 'pracuje');
});

test('a resolved batch does not clear a red light caused by an API error', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 'e1');
  event('UserPromptSubmit');
  event('StopFailure', { error: 'rate_limit' });
  assert.equal(event('PostToolBatch', { tool_calls: [] }).state, 'czeka');
});

test('a waiting state written by the previous hook version is still resolved', () => {
  const dir = freshDir();
  writeFileSync(join(dir, 'old.state.json'), JSON.stringify({
    state: 'czeka', since: 1, turnStartedAt: 1, detail: 'Zgoda: AskUserQuestion',
    pendingTool: 'AskUserQuestion\u0000{"questions":[]}', updatedAt: Date.now(),
  }));
  const event = hookEvents(dir, 'old');
  assert.equal(event('PostToolUse', { tool_name: 'AskUserQuestion', tool_input: { questions: [], answers: {} } }).state, 'pracuje');
});

test('a finished turn keeps the first sentence of the answer as a preview', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 'r1');
  event('UserPromptSubmit');
  const done = event('Stop', {
    background_tasks: [],
    last_assistant_message: '## Podsumowanie\n\n**IT-901** jest zrobione: alert, kolejka i baner. Czekam na decyzję o commicie.',
  });
  assert.equal(done.summary, 'Podsumowanie');
  const second = event('Stop', { background_tasks: [], last_assistant_message: '**IT-901** jest zrobione: alert i baner. Czekam na decyzję.' });
  assert.equal(second.summary, 'IT-901 jest zrobione: alert i baner.');
});

test('the Claude Code process id is recorded with the state', () => {
  const dir = freshDir();
  const state = hookEvents(dir, 'p1')('UserPromptSubmit');
  // The test process spawns the hook directly, as Claude Code does with command + args.
  assert.equal(state.pid, process.pid);
});

test('a turn that ends with work in the background stays yellow', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 'b1');
  const working = event('UserPromptSubmit');
  const paused = event('Stop', {
    background_tasks: [
      { id: 't1', type: 'subagent', status: 'running', description: 'review' },
      { id: 't2', type: 'shell', status: 'running', command: 'gh pr checks --watch' },
    ],
  });
  assert.equal(paused.state, 'pracuje');
  assert.equal(paused.background, 2);
  assert.equal(paused.detail, 'w tle: 2 zadania');
  assert.equal(paused.since, working.turnStartedAt);

  // Silence in the terminal while agents work is not the end of the turn.
  assert.equal(event('Notification', { notification_type: 'idle_prompt' }).state, 'pracuje');

  // The background work wakes the session; its next Stop settles the state.
  assert.equal(event('Stop', { background_tasks: [] }).state, 'gotowe');
});

test('a monitor alone does not keep the session yellow', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 'b2');
  event('UserPromptSubmit');
  assert.equal(event('Stop', { background_tasks: [{ id: 'm1', type: 'monitor', status: 'running' }] }).state, 'gotowe');
});

test('idle_prompt ends a turn that was interrupted without a Stop event, as already seen', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 's2');
  event('UserPromptSubmit');
  const idle = event('Notification', { notification_type: 'idle_prompt' });
  assert.equal(idle.state, 'gotowe');
  assert.equal(idle.fresh, false);
});

test('permission_prompt notification turns the light red, and the resolved batch turns it off', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 's3');
  event('UserPromptSubmit');
  const waiting = event('Notification', { notification_type: 'permission_prompt', message: 'Claude needs your permission' });
  assert.equal(waiting.state, 'czeka');
  assert.equal(waiting.detail, 'Potrzebna Twoja decyzja');
  assert.equal(event('PostToolBatch', { tool_calls: [] }).state, 'pracuje');
});

test('an API error that ends the turn asks for attention', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 's4');
  event('UserPromptSubmit');
  const failed = event('StopFailure', { error: 'rate_limit' });
  assert.equal(failed.state, 'czeka');
  assert.equal(failed.detail, 'Błąd API: rate_limit');
});

test('file tools are described by file name, long commands are shortened', () => {
  const dir = freshDir();
  const event = hookEvents(dir, 's5');
  assert.equal(event('PermissionRequest', { tool_name: 'Edit', tool_input: { file_path: 'C:\\apps\\api\\src\\very\\deep\\cancel.ts' } }).detail, 'Zgoda: Edit · cancel.ts');
  const long = event('PermissionRequest', { tool_name: 'Bash', tool_input: { command: `echo ${'x'.repeat(80)}\nsecond line` } }).detail;
  assert.ok(long.endsWith('…') && long.length <= 'Zgoda: Bash · '.length + 40, long);
  assert.equal(event('PermissionRequest', { tool_name: 'ExitPlanMode', tool_input: { plan: 'x' } }).detail, 'Plan do zatwierdzenia');
});

test('headless sessions (claude -p, SDK) never reach the widget', () => {
  const dir = freshDir();
  for (const env of [{ CLAUDE_CODE_ENTRYPOINT: 'sdk-cli' }, { CLAUDE_CODE_SESSION_ATTENDED: '0' }]) {
    const result = run('hook.mjs', { hook_event_name: 'UserPromptSubmit', session_id: 'h1' }, dir, env);
    assert.equal(result.status, 0);
    const line = run('statusline.mjs', { session_id: 'h1', model: { display_name: 'Opus 5' } }, dir, env);
    assert.equal(line.stdout, 'Opus 5', 'the status line itself still renders');
  }
  assert.deepEqual(readdirSync(dir), []);
});

test('a hostile session id cannot write outside the state directory', () => {
  const dir = freshDir();
  run('hook.mjs', { hook_event_name: 'UserPromptSubmit', session_id: '..\\..\\evil' }, dir);
  assert.deepEqual(readdirSync(dir), ['evil.state.json']);
});

test('malformed input exits 0 silently', () => {
  const dir = freshDir();
  for (const script of ['hook.mjs', 'statusline.mjs']) {
    const result = run(script, '{not json', dir);
    assert.equal(result.status, 0);
    assert.equal(result.stderr, '');
  }
});

test('the status line records context and account limits and prints one line', () => {
  const dir = freshDir();
  const result = run('statusline.mjs', {
    session_id: 's6',
    session_name: 'Widżet sygnalizatora',
    cwd: 'C:\\apps\\claudius',
    workspace: { current_dir: 'C:\\apps\\claudius', project_dir: 'C:\\apps\\claudius' },
    model: { display_name: 'Opus 5' },
    context_window: { used_percentage: 38.4, total_input_tokens: 384000, context_window_size: 1000000 },
    rate_limits: { five_hour: { used_percentage: 23.5, resets_at: 1757868000 }, seven_day: { used_percentage: 41.2, resets_at: 1758178800 } },
  }, dir);

  assert.equal(result.status, 0);
  assert.equal(result.stdout, 'Opus 5 · kontekst 38% · 5 h 24% · tydzień 41%');

  const usage = read(dir, 's6.usage.json');
  assert.equal(usage.name, 'Widżet sygnalizatora');
  assert.equal(usage.project, 'claudius');
  assert.equal(usage.contextPct, 38.4);
  assert.equal(usage.contextSize, 1000000);

  const limits = read(dir, 'limits.json');
  assert.deepEqual(limits.fiveHour, { pct: 23.5, resetsAt: 1757868000 });
  assert.deepEqual(limits.sevenDay, { pct: 41.2, resetsAt: 1758178800 });
});

test('without rate_limits (API key, before the first reply) no limits file is written', () => {
  const dir = freshDir();
  const result = run('statusline.mjs', { session_id: 's7', model: { display_name: 'Sonnet 5' }, context_window: { used_percentage: 5 } }, dir);
  assert.equal(result.stdout, 'Sonnet 5 · kontekst 5%');
  assert.ok(!existsSync(join(dir, 'limits.json')));
});

test('SessionEnd removes every file of the session, including the widget seen-marker', () => {
  const dir = freshDir();
  run('hook.mjs', { hook_event_name: 'UserPromptSubmit', session_id: 's8' }, dir);
  run('statusline.mjs', { session_id: 's8', context_window: { used_percentage: 5 } }, dir);
  writeFileSync(join(dir, 's8.seen.json'), JSON.stringify({ seenAt: Date.now() }));
  assert.equal(readdirSync(dir).length, 3);
  run('hook.mjs', { hook_event_name: 'SessionEnd', session_id: 's8', reason: 'clear' }, dir);
  assert.deepEqual(readdirSync(dir), []);
});
