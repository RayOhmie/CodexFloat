# Changelog

All notable public changes to CodexFloat will be documented in this file.

## Unreleased

## 0.1.48 - 2026-06-26

- Show a separate first-run trusted-environment confirmation popup when no trusted address library exists, and keep the Environment Safety submenu arrow visible before hover.
- Add an Environment Safety top-level menu with monitoring toggles and a trusted address library, and store user-confirmed medium-risk environments as multiple trusted records.
- Use a public localized IP geolocation API for Chinese environment location display, with built-in mappings only as fallback text.
- Expand Chinese environment location mappings for common China and Malaysia IP locations, and show compact environment-risk text in the mini floating widget.
- Use regular-weight environment popup button text and widen the medium-risk action buttons to avoid clipping in Chinese and English.
- Match Chinese environment location order to the English location order.
- Polish environment popup typography, button text rendering, and vertically centered IP/location content for both two-line and three-line address layouts.
- Center environment IP/location blocks using measured prompt text height instead of fixed text bounds.
- Vertically center environment IP/location blocks between the prompt text and confirmation buttons.
- Add localized Chinese location lines under English environment locations, clarify medium-risk action button copy, and require explicit user choice for medium-risk prompts.
- Add countdown confirmation to high-risk environment popups and restore clearer IP/location blocks with more spacious text layout across environment prompts.
- Position environment safety popups in the screen bottom-right and add countdown confirmation controls for safe and medium-risk states.
- Replace environment safety Windows notifications with app-owned status popups and improve the medium-risk confirmation dialog layout.
- Keep the floating-widget Scroll Data submenu arrow visible as soon as the right-click menu opens.
- Add environment safety monitoring before ChatGPT queries, including startup IP/location checks, medium-risk user confirmation, and high-risk blocking.
- Increase the expanded detail panel content typography and contrast while keeping the top title and update timestamp unchanged.
- Keep reset-card detail lines left-aligned within their centered text group in the expanded detail panel.
- Remove legacy experimental files from the public source tree.

## 0.1.47 - 2026-06-24

- Publish releases with uppercase `V` version labels.
- Fix Release note wording and encoding so `V0.1.44` is correctly treated as the initial public release.

## 0.1.46 - 2026-06-24

- Open the English Codex Radar page from the English About dialog and README.

## 0.1.45 - 2026-06-24

- Add Codex Radar attribution in About and README for model IQ data.
- Write bilingual Chinese and English error logs.

## 0.1.44 - 2026-06-24

Initial open source release.

- Add a Windows floating widget for Codex remaining usage and reset countdowns.
- Show 5h and Weekly remaining usage, reset-card expiry, and CodexRadar model IQ scores.
- Store credentials locally with Windows DPAPI encryption.
- Support automatic credential import from common Codex and ChatGPT local JSON locations.
- Provide theme presets, language switching, startup behavior, opacity, always-on-top, click-through, and lock-position settings.
- Include README, privacy, security, contribution, license, and issue-template files for GitHub publishing.
