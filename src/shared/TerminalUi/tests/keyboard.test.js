import test from 'node:test';
import assert from 'node:assert/strict';
import {getTerminalShortcut, WIN32_CTRL_J} from '../src/keyboard.js';

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

test('Codex multiline keys emit Win32 Ctrl+J, not plain LF', () => {
  assert.equal(WIN32_CTRL_J, '\x1b[74;36;10;1;8;1_\x1b[74;36;10;0;8;1_');
  const cases = [
    keyEvent({shiftKey: true}),
    keyEvent({ctrlKey: true}),
    keyEvent({key: 'j', code: 'KeyJ', ctrlKey: true}),
  ];
  for (const ev of cases) {
    assert.deepEqual(getTerminalShortcut(ev, 'codex'), {type: 'input', data: WIN32_CTRL_J});
  }
});

test('Plain Enter and Ctrl+Enter stay native in Claude; plain Enter in Codex', () => {
  assert.equal(getTerminalShortcut(keyEvent(), 'claude'), null);
  assert.equal(getTerminalShortcut(keyEvent({ctrlKey: true}), 'claude'), null);
  assert.equal(getTerminalShortcut(keyEvent(), 'codex'), null);
});

test('Shift+Enter is not intercepted in diagnostic PowerShell', () => {
  assert.equal(getTerminalShortcut(keyEvent({shiftKey: true}), 'shell'), null);
});

test('Codex preserves Ctrl+C, Ctrl+V and unmodified letter keys', () => {
  assert.equal(getTerminalShortcut(keyEvent({key:'c',code:'KeyC',ctrlKey:true}), 'codex'), null);
  assert.equal(getTerminalShortcut(keyEvent({key:'v',code:'KeyV',ctrlKey:true}), 'codex'), null);
  assert.equal(getTerminalShortcut(keyEvent({key:'j',code:'KeyJ'}), 'codex'), null);
  assert.equal(getTerminalShortcut(keyEvent({key:'j',code:'KeyJ',ctrlKey:true,shiftKey:true}), 'codex'), null);
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
