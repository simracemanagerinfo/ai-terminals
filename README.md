# AI Terminals

Due applicazioni Windows indipendenti per **Claude Code** e **OpenAI Codex CLI**, con un motore ConPTY/xterm.js condiviso.

> **Migrazione in corso.** Le versioni di rilascio saranno pubblicate soltanto dopo il collaudo dei due EXE. Il terminale precedente non viene modificato.

- `ClaudeTerminal.exe`: identità terracotta, esclusivamente conversazioni Claude.
- `CodexTerminal.exe`: identità grafite/teal, esclusivamente conversazioni Codex.
- `src/shared/`: host WPF, ConPTY, storico, statistiche, renderer web.
- `src/claude/` e `src/codex/`: progetti Windows indipendenti e temi.
- `tests/`: verifiche dei due launcher, delle sessioni e dei token.
- `.github/workflows/`: compilazione Windows senza dover installare SDK o npm sul PC aziendale.

I transcript e le credenziali **non** vengono copiati nei repository: Claude e Codex continuano a usare le rispettive directory locali.

Ogni sviluppatore usa la propria cartella locale; per ricevere i commit è sufficiente `git pull --ff-only`.
