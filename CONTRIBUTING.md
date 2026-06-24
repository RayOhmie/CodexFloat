# Contributing

Issues and pull requests are welcome.

Before opening a pull request:

- Keep credential handling local and encrypted.
- Do not commit tokens, account IDs, logs, or local config files.
- Run `.\build.ps1` on Windows and confirm the executable builds.
- Keep UI changes small and test behavior with click-through, lock-position, and opacity enabled.

The project currently uses a simple single-file WinForms build to keep distribution lightweight.
