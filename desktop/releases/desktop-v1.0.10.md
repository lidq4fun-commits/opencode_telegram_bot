# Desktop v1.0.10

## Complete core runtime payload

| Dependency | Packaged version |
| --- | --- |
| Node/npm | Node 24.15.0 x64 and its npm distribution |
| OpenCode V1 | 1.18.34 x64 baseline |
| OpenCode V2 | 2.0.22 x64 baseline |
| Bun | 1.4.2 x64 baseline |
| Portable Git | 2.56.0.windows.1 x64 |
| WebView2 fixed runtime | 154.0.4258.53 x64 |
| WebView2 SDK | 1.0.4258.31 |

- Existing compatible Node, Bun, Git, OpenCode and WebView2 are preferred; missing or incompatible tools use private app-local copies.
- Portable Git includes Git Bash, which is added to the child-process search path when available.
- Node reuse additionally requires x64, the packaged native ABI and successful SQLite loading.
- Installed WebView2 must be at least 154.0.4258; older or missing versions fall back to the fixed runtime.
- OpenCode reuse matches the selected API major version. An explicitly selected external executable is not overwritten.
- API installation buttons use the complete local payload without npm downloads; legacy installations without that payload retain the original online fallback.
- Fixed WebView2 sandbox read/execute grants are limited to the app-local runtime directory.
- Assembly/file versions, CLI version output and Windows uninstall display version now come from the build's release version.
- Source URLs, pinned versions, publisher verification information and runtime SHA256 inventory are included. Binary dependencies remain Git-ignored.

## Scope and limitations

- Target: Windows 10 version 1809 or later / Windows 11 x64, with .NET Framework 4.6.2+ and WPF.
- Installing core dependencies does not require network access. Telegram and cloud model use still require connectivity and user configuration.
- Optional plugins, language servers, model weights and task-specific project dependencies are not included.
- Existing system tools are not replaced. Uninstalling a separately managed user-data engine does not remove the engines shipped inside the app.
- No code signing or clean-machine OS compatibility certification is claimed.

## Verification

- Desktop regression tests cover payload completeness, version metadata, native SQLite compatibility, existing-tool preference, incompatible-tool rejection and API version switching.
- Session-follow and browser-login regressions are retained.
- V1 and V2 binaries started from isolated user profiles and returned authenticated HTTP 200 with proxy-based outbound access blocked; no model tasks were submitted. This is not a firewall-isolated network test.
- Fixed WebView2 154.0.4258.53 rendered the existing web workspace and preserved cookie authentication over two refreshes.
- Installer sandbox permissions were tested against an isolated directory without changing existing installations.
- The standalone installer payload was extracted into a new directory containing spaces; all 9875 dependency inventory files passed SHA256/size verification and the relocated runtime checks passed.
- Final checks: Bot build, ESLint and TypeScript passed; 2355 Bot tests passed with 7 skipped (`BOT_LOCALE=en`); 54 desktop payload/session/login assertions passed.

## Artifacts

- Installer: `desktop/artifacts/OpenCodeTelegram-Setup-1.0.10.exe`.
- Distribution: `desktop/artifacts/OpenCodeTelegram-1.0.10-Windows-x64.zip`.
- Checksums: `desktop/artifacts/SHA256-1.0.10.txt`.
- Git tag: `desktop-v1.0.10`.
- Installer SHA256: `DA4BEADB872E282F7EC73B01081B182CF25EF01C3DF4FE5CD8185E7A0AEE11DC`.
- Distribution ZIP SHA256: `3A3DADBC625EDEAD8DD9BDCC1781215DDA0FFE331ECBAB65AA2AE27806E1843A`.
