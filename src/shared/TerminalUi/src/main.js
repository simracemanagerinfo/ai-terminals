import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import './style.css';
import { getTerminalShortcut } from './keyboard.js';

const root = document.getElementById('terminal-root');
const terminals = new Map();
let activeId = null;

function send(message) {
  window.chrome?.webview?.postMessage(message);
}
function notifySize(id, force = false) {
  const entry = terminals.get(id);
  if (!entry) return;
  const cols = entry.term.cols;
  const rows = entry.term.rows;
  if (!force && cols === entry.cols && rows === entry.rows) return;
  entry.cols = cols;
  entry.rows = rows;
  send({type: 'resize', id, cols, rows});
}
function resizeVisible() {
  if (!activeId) return;
  const item = terminals.get(activeId);
  if (!item) return;
  try { item.fit.fit(); notifySize(activeId, true); }
  catch { /* WebView can report zero size during mounting. */ }
}
new ResizeObserver(() => resizeVisible()).observe(root);

function select(id) {
  for (const [key, entry] of terminals)
    entry.element.classList.toggle('active', key === id);
  activeId = terminals.has(id) ? id : null;
  requestAnimationFrame(() => {
    resizeVisible();
    terminals.get(activeId)?.term.focus();
  });
}
function create(id, mode = 'claude') {
  if (terminals.has(id) || typeof id !== 'string') return;
  const element = document.createElement('div');
  element.className = 'term-session' + (mode === 'codex' ? ' codex-session' : '');
  element.dataset.id = id;
  root.appendChild(element);
  const codex = mode === 'codex';
  const term = new Terminal({
    cursorBlink: true,
    cursorStyle: 'bar',
    convertEol: false,
    allowTransparency: false,
    scrollback: 18000,
    fontFamily: 'CaskaydiaCove Nerd Font, Cascadia Mono, Consolas, monospace',
    fontSize: 14,
    lineHeight: 1.08,
    theme: {
      background: codex ? '#10191D' : '#171210',
      foreground: codex ? '#DEEAF0' : '#f5e4d6',
      cursor: codex ? '#63B6BA' : '#e8a080',
      selectionBackground: codex ? '#24515B' : '#7c4d3b',
      black: codex ? '#10191D' : '#171210', red: '#e5887a', green: '#b9dba2', yellow: '#f1cf82',
      blue: '#9fc2de', magenta: '#dca6bf', cyan: '#9fd5d7', white: '#f5e4d6',
      brightBlack: '#9f897c', brightRed: '#f0a797', brightGreen: '#cbe8bb',
      brightYellow: '#ffe4a0', brightBlue: '#bfd7ee', brightMagenta: '#efc0d2',
      brightCyan: '#b2eff0', brightWhite: '#fff7ee'
    }
  });
  const fit = new FitAddon();
  term.loadAddon(fit);
  term.open(element);
  terminals.set(id, {term, fit, element, mode, cols: 0, rows: 0});
  term.onData(data => send({type:'input', id, data}));
  term.onResize(() => notifySize(id));
  term.attachCustomKeyEventHandler(event => {
    const action = getTerminalShortcut(event, mode, term.hasSelection());
    if (!action) return true;
    event.preventDefault();
    if (action.type === 'input') send({type:'input', id, data:action.data});
    else if (action.type === 'copy') send({type:'copy', id, data:term.getSelection()});
    else if (action.type === 'paste') send({type:'paste', id});
    return false;
  });
  if (!activeId) select(id);
}
function remove(id) {
  const item = terminals.get(id);
  if (!item) return;
  item.term.dispose();
  item.element.remove();
  terminals.delete(id);
  if (activeId === id) select(terminals.keys().next().value ?? null);
}

window.chrome?.webview?.addEventListener('message', (event) => {
  const msg = event.data;
  if (!msg || typeof msg !== 'object') return;
  switch (msg.type) {
    case 'create': create(msg.id, msg.mode); break;
    case 'select': if (terminals.has(msg.id)) select(msg.id); break;
    case 'clipboard-paste':
      terminals.get(msg.id)?.term.paste(msg.data ?? '');
      break;
    case 'output': terminals.get(msg.id)?.term.write(msg.data ?? ''); break;
    case 'exited':
      terminals.get(msg.id)?.term.write('\r\n\x1b[38;2;217;119;87m[Processo terminato]\x1b[0m\r\n');
      break;
    case 'remove': remove(msg.id); break;
  }
});
requestAnimationFrame(() => send({type:'ready'}));
