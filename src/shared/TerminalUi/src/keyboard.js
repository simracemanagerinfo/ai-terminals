// Shift+Enter viene convertito in Ctrl+J (LF): newline per entrambe le TUI.
// Rimangono intatti Ctrl+Enter, Ctrl+C, Ctrl+V e i tasti PowerShell.
export function getTerminalShortcut(event, mode, hasSelection = false) {
  if (event.type !== 'keydown' || event.isComposing) return null;

  if ((mode === 'claude' || mode === 'codex') && event.key === 'Enter' && event.shiftKey &&
      !event.ctrlKey && !event.altKey && !event.metaKey) {
    return {type: 'input', data: '\x0a'};
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
