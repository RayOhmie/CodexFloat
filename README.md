# CodexFloat

Windows floating monitor for Codex remaining usage, reset time, and reset-card expiry.

This project is designed as a small single-file WinForms app. It reads local encrypted credentials, calls Codex/ChatGPT internal backend endpoints, and shows the result in a compact floating water-level widget.

## Features

- Circular floating widget with animated water level.
- Rotating `5h` and `Weekly` remaining-usage display.
- Countdown ring for reset time.
- Click to expand full details; leave the detail panel to collapse.
- Reset-card expiry display.
- Local DPAPI-encrypted `ACCESS_TOKEN` and `ACCOUNT_ID` storage.
- Optional auto-detection from common Windows Codex/ChatGPT JSON locations.
- Startup, opacity, always-on-top, click-through, and lock-position settings.
- The same behavior toggles are also available from the floating widget context menu.
- Unified language setting for tray menu, floating menu, details, settings, and about dialogs.
- Optional environment safety monitoring before ChatGPT backend queries, with startup IP/location checks, medium-risk confirmation, and high-risk China/Hong Kong blocking.
- User-confirmed medium-risk environments are stored in a trusted address library that can be reviewed and pruned from the Environment Safety menu.
- On first use with no trusted address library, CodexFloat asks separately whether to set the current IP and location as the default trusted environment.
- Submenus automatically open to the left when opening to the right would leave the floating widget's screen.
- Theme presets inspired by desktop traffic-monitor widgets.
- Custom app logo and multi-size Windows `.ico` icon.

## Data Sources

CodexFloat fetches model IQ reference scores from [Codex Radar](https://codexradar.com/en/). Thanks to Codex Radar for making the model IQ data available as a reference.

Environment safety checks use public IP geolocation data. In Chinese UI, CodexFloat also requests localized Chinese location text for display; the localized text is not used as the security decision source.
When a medium-risk environment is confirmed as safe, CodexFloat adds it to the trusted address library instead of replacing the previous trusted environment.

## Download / Build

Current source file:

```text
CodexFloat.cs
```

Logo and icon assets:

```text
assets/CodexFloat-logo.svg
assets/CodexFloat-logo-256.png
assets/CodexFloat.ico
```

Build with Windows .NET Framework compiler:

```powershell
.\build.ps1
```

The generated executable is:

```text
CodexFloat.exe
```

## Credentials

Config path:

```text
%APPDATA%\CodexFloat\config.json
```

On first launch, CodexFloat can migrate an existing legacy config from:

```text
%APPDATA%\CodexResetMonitor\config.json
```

Credentials are stored as:

```text
access_token_dpapi
account_id_dpapi
```

Both values are encrypted by Windows DPAPI for the current Windows user. The settings UI never displays saved credentials in plaintext.

## GitHub Publishing

Recommended steps for publishing to `https://github.com/RayOhmie`:

```powershell
git init
git add .
git commit -m "Initial open source release"
git branch -M main
git remote add origin https://github.com/RayOhmie/CodexFloat.git
git push -u origin main
```

Create an empty repository named `CodexFloat` on GitHub first, then run the commands above from this folder.

## Important Note

The current API endpoints are internal ChatGPT/Codex `backend-api` endpoints, not public stable OpenAI API contracts. They may change without notice.

## License

MIT. See [LICENSE](LICENSE).
