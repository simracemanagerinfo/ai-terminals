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

test('Enter and Ctrl+Enter keep native Claude semantics', () => {
  assert.equal(getTerminalShortcut(keyEvent(), 'claude'), null);
  assert.equal(getTerminalShortcut(keyEvent({ctrlKey: true}), 'claude'), null);
});

test('Shift+Enter is not intercepted in diagnostic PowerShell', () => {
  assert.equal(getTerminalShortcut(keyEvent({shiftKey: true}), 'shell'), null);
});

test('Alt+Shift+Enter and IME composition are left untouched', () => {
  assert.equal(getTerminalShortcut(keyEvent({altKey: true, shiftKey: true}), 'claude'), null);
  assert.equal(getTerminalShortcut(keyEvent({isComposing: true, shiftKey: true}), 'claude'), null);
});

test('Only keydown generates terminal data (no doubled keyup)', () => {
  assert.equal(getTerminalShortcut(keyEvent({type: 'keyup',shiftKey: true}), 'claude'), null);
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
