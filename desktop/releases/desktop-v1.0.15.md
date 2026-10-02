# Desktop v1.0.15

**First usable release / 第一个可用版本**

The user accepted this version as the first usable desktop baseline. Earlier releases are development iterations, not the accepted baseline. This designation does not imply complete clean-machine or UI automation coverage.

Git release tag: `desktop-v1.0.15` (annotated).

- Remove the extra in-app top header to give the main content more space.
- Move interface language selection into Language & Appearance, with immediate persistence and application.
- Offer Simplified Chinese and English before installation begins.
- Refresh installer text when the language changes without losing the chosen installation path.
- Save the chosen installer language for the installed application.
- Retain the compact web menu, responsive browser layout, and installation/uninstallation progress indicators from v1.0.14.

Artifacts:

- `OpenCodeTelegram-Setup-1.0.15.exe`
- `OpenCodeTelegram-1.0.15-Windows-x64.zip`

SHA256 of the accepted build (artifacts are kept outside Git):

```text
022597E251A940BA45EF37F12CCCB0226E6E14FA2627CD834DCB9E5C69D0C9B1  OpenCodeTelegram-Setup-1.0.15.exe
FE6F3F7E35520130E157EE6EDB50A37898D58DE8931B36414342A9C70B9CFC23  OpenCodeTelegram-1.0.15-Windows-x64.zip
```

Validation: desktop and Bot compilation, 18 embedded synchronization tests, and packaged version/resource, workspace-context and browser-login checks passed. The user accepted the build; a clean-machine installation and full UI automation were not performed.
