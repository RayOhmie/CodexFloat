# Privacy

CodexFloat stores configuration locally at:

```text
%APPDATA%\CodexFloat\config.json
```

On first launch, CodexFloat may copy a legacy config from `%APPDATA%\CodexResetMonitor\config.json` if the new config does not exist.

The app does not send credentials to any third-party service. It uses the stored token only to call ChatGPT/Codex backend endpoints configured in the app.

Automatic credential discovery scans common local Windows Codex/ChatGPT JSON paths. Imported credentials are encrypted with Windows DPAPI before being saved.

Do not share `config.json`, screenshots of request headers, logs, or exported settings if they may contain account-specific data.
