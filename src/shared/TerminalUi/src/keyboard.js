// Claude accepts Ctrl+J/LF as multiline input.
// Codex on Windows can receive LF through ConPTY as a plain Enter, even when
// the terminal captures Shift+Enter. Send a genuine Windows Ctrl+J key down/up
// record over ConPTY instead (documented in openai/codex issue #48996).
// Keep Enter, Ctrl+C, Ctrl+V, and other shell shortcuts unchanged.
export const WIN32_CTRL_J =
  '\x1b[74;36;10;1;8;1_\x1b[74;36;10;0;8;1_';

export function getTerminalShortcut(event, mode, hasSelection = false) {
  if (event.type !== 'keydown' || event.isComposing) return null;

  const modifiedEnter = event.key === 'Enter' &&
    (event.shiftKey || event.ctrlKey) &&
    !event.altKey && !event.metaKey;

  if (mode === 'claude' && modifiedEnter && event.shiftKey && !event.ctrlKey) {
    return {type: 'input', data: '\x0a'};
  }

  if (mode === 'codex') {
    // Users can request a newline with any of these three combinations.
    // The TUI sees Ctrl+J as a Windows keyboard event instead of bare LF.
    const ctrlJ = event.ctrlKey && !event.shiftKey &&
      !event.altKey && !event.metaKey && event.code === 'KeyJ';
    if (modifiedEnter || ctrlJ) {
      return {type: 'input', data: WIN32_CTRL_J};
    }
  }

  if (event.ctrlKey && event.shiftKey && !event.altKey && !event.metaKey) {
    if (event.code === 'KeyC') {
      return {type: hasSelection ? 'copy' : 'ignore'};
    }
    if (event.code === 'KeyV') {
      return {type: 'paste'};
    }
  }
  return null;
}
