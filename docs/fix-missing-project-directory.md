# Missing project directory fix

The local logs on 2026-10-30 show that the selected session references
`/Users/isabella/OpenCodeTelegram-build`, which no longer exists.
OpenCode failed to load agents and rejected prompts with `Location not found`.

The fix checks local project directories before project switching, new session
creation, prompt dispatch and desktop attachment. Telegram explains how to recover.
The desktop receives an explicit unavailable-project state and can leave the
broken session without being forced back by synchronization. Remote server paths
are not checked against the bot filesystem. Existing session data is preserved.

Prepare-Runtime.command now preserves an existing extracted Bot source tree.
The embedded bot-source.zip includes the fixes and regression tests.

Validation: Bot build, TypeScript typecheck, ESLint; all available tests excluding
tests/e2e/**; 29 embedded web synchronization tests; shell syntax validation.
The original archive lacks the e2e/ support scripts, so those tests cannot run.
No live Telegram message or AI prompt was sent during verification.

This is a source package. It has not been installed into the running application.
To resume the old conversation, restore its original directory. To use a different
project, select an existing directory and create a new session. Do not silently
retarget an old session to unrelated files.
