import test from 'node:test';
import assert from 'node:assert/strict';
import {getTerminalShortcut} from '../src/keyboard.js';

function keyEvent(overrides = {}) {
  return {
    type: 'keydown',
    key: 'Enter',
    code: 'Enter',
    shiftKey: false,
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    isComposing: false,
    ...overrides
  };
}

test('Shift+Enter in Claude sends Ctrl+J newline (LF), not CR submit', () => {
  assert.deepEqual(
    getTerminalShortcut(keyEvent({shiftKey: true}), 'claude'),
    {type: 'input', data: '\x0a'}
  );
});

test('Shift+Enter in Codex sends Ctrl+J newline (LF), not Enter submit', () => {
  assert.deepEqual(
    getTerminalShortcut(keyEvent({shiftKey: true}), 'codex'),
    {type: 'input', data: '\x0a'}
  );
});

test('Enter and Ctrl+Enter remain native in both AI terminals', () => {
  for (const mode of ['claude', 'codex']) {
    assert.equal(getTerminalShortcut(keyEvent(), mode), null);
    assert.equal(getTerminalShortcut(keyEvent({ctrlKey: true}), mode), null);
  }
});

test('Shift+Enter is not intercepted in diagnostic PowerShell', () => {
  assert.equal(getTerminalShortcut(keyEvent({shiftKey: true}), 'shell'), null);
});

test('Alt/Meta+Shift+Enter and IME composition stay untouched', () => {
  for (const mode of ['claude', 'codex']) {
    assert.equal(getTerminalShortcut(keyEvent({altKey: true, shiftKey: true}), mode), null);
    assert.equal(getTerminalShortcut(keyEvent({metaKey: true, shiftKey: true}), mode), null);
    assert.equal(getTerminalShortcut(keyEvent({isComposing: true, shiftKey: true}), mode), null);
  }
});

test('Only keydown sends newline; PowerShell is never intercepted', () => {
  for (const mode of ['claude', 'codex']) {
    assert.equal(getTerminalShortcut(keyEvent({type: 'keyup', shiftKey: true}), mode), null);
  }
  assert.equal(getTerminalShortcut(keyEvent({shiftKey: true}), 'shell'), null);
});

test('Ctrl+Shift+C and Ctrl+Shift+V route to native clipboard', () => {
  assert.deepEqual(
    getTerminalShortcut(keyEvent({key: 'C',code:'KeyC',ctrlKey:true,shiftKey:true}), 'claude', true),
    {type: 'copy'}
  );
  assert.deepEqual(
    getTerminalShortcut(keyEvent({key: 'C',code:'KeyC',ctrlKey:true,shiftKey:true}), 'claude', false),
    {type: 'ignore'}
  );
  assert.deepEqual(
    getTerminalShortcut(keyEvent({key: 'V',code:'KeyV',ctrlKey:true,shiftKey:true}), 'claude'),
    {type: 'paste'}
  );
});
