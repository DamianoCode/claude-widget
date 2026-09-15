import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { bridgeDir, findTerminal, parseRequest, REQUEST_MAX_AGE_MS, withTimeout } from '../protocol';

test('the bridge directory sits next to the widget state, as the widget expects', () => {
  assert.equal(bridgeDir({}, 'C:\\Users\\me'), path.join('C:\\Users\\me', '.claude', 'widget', 'vscode'));
  assert.equal(bridgeDir({ CLAUDE_WIDGET_STATE_DIR: 'D:\\tmp\\w\\state' }, 'C:\\Users\\me'), path.resolve('D:\\tmp\\w', 'vscode'));
  // Widżet zdejmuje końcowy ukośnik (WidgetPaths) — tu też, inaczej każda strona patrzy gdzie indziej.
  assert.equal(bridgeDir({ CLAUDE_WIDGET_STATE_DIR: 'D:\\tmp\\w\\state\\' }, 'C:\\Users\\me'), path.resolve('D:\\tmp\\w', 'vscode'));
});

test('a terminal pid that never arrives does not hold up the window state', async () => {
  const never = new Promise<number>(() => undefined);
  assert.equal(await withTimeout(never, 20), undefined);
  assert.equal(await withTimeout(Promise.resolve(4120), 20), 4120);
  assert.equal(await withTimeout(Promise.reject(new Error('closed')), 20), undefined);
});

test('a fresh request from the widget is accepted with its session pids', () => {
  const request = parseRequest({ version: 1, id: 'a1', pids: [4120, 3300], at: 1000 }, 1500);
  assert.deepEqual(request, { version: 1, id: 'a1', pids: [4120, 3300], at: 1000 });
});

test('stale, malformed or empty requests are ignored', () => {
  assert.equal(parseRequest({ id: 'a1', pids: [1], at: 0 }, REQUEST_MAX_AGE_MS + 1), null);
  assert.equal(parseRequest({ id: 'a1', pids: [], at: 5 }, 5), null);
  assert.equal(parseRequest({ id: 'a1', pids: ['x', -3, 1.5], at: 5 }, 5), null);
  assert.equal(parseRequest({ pids: [1], at: 5 }, 5), null);
  assert.equal(parseRequest(null, 5), null);
  assert.equal(parseRequest('focus', 5), null);
});

test('the terminal whose shell is in the session chain is chosen', () => {
  const terminals = [
    { terminal: 'build', pid: 200 },
    { terminal: 'pending', pid: undefined },
    { terminal: 'claude', pid: 4120 },
  ];
  assert.equal(findTerminal(terminals, [9000, 4120, 12780]), 'claude');
  assert.equal(findTerminal(terminals, [1, 2]), undefined);
});
