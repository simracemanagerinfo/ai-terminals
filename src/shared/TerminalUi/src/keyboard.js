// Mapping locale del renderer: Ctrl+J (LF) e il comando "chat:newline"
// predefinito di Claude. Non alterare Ctrl+Enter (in Claude puo significare Send Now).
export function getTerminalShortcut(event, mode, hasSelection = false) {
  if (event.type !== 'keydown' || event.isComposing) return null;

  if (mode === 'claude' && event.key === 'Enter' && event.shiftKey &&
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
