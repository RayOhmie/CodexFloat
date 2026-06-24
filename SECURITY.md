# Security Policy

## Supported Versions

Only the latest source on `main` is maintained.

## Reporting a Vulnerability

Please report security issues privately via the author's GitHub profile:

https://github.com/RayOhmie

Do not open public issues containing tokens, account IDs, local config contents, or request/response payloads.

## Credential Handling

`ACCESS_TOKEN` and `ACCOUNT_ID` are stored locally with Windows DPAPI under the current Windows user. The settings UI masks saved values and does not display plaintext credentials.
