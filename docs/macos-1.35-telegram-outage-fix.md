# macOS 1.35 Telegram outage recovery

This Mac fix implements the six shared Bot changes from the reported 1.0.35
failure scenario. It is an independent implementation from the bug description,
not a byte-for-byte sync of the unavailable Windows sources. Swift sources are unchanged.

## Changes

- Pinned-session and busy/idle presentation run in a background lane; pending
  updates coalesce to the latest session/state. Restored permission and question
  presentation is deduplicated and does not block local attachment.
- Desktop command processing and heartbeat writes run independently. A command
  receives an acknowledgement only after it completes. Writes to state.tmp and
  state.json are serialized. Phone/desktop arbitration and the six-second
  stale-heartbeat protection are retained.
- Telegram command-menu initialization runs in the background with in-flight
  deduplication; a failed initialization can be retried on a subsequent update.
- Fresh inbound commands and successful outbound messages do not wait for
  supplemental outage notifications. Existing send scheduling and retry policy
  are retained.
- Question delivery checks the interaction identity, current question index and
  send token before registering a delivered message or clearing a failed poll.
  Stale delivered prompts are removed in the background.
- Previous missing-project-directory validation and source-preservation changes
  remain included. Existing runtime settings and conversations are not migrated.

## Validation

- Bot compilation, TypeScript typecheck and ESLint passed.
- All 204 available Bot test files passed: 2417 tests.
- Embedded web synchronization tests: 29 passed.
- The original source package lacks e2e/ support scripts. Tests under tests/e2e/
  could not run and are excluded from the available-test result above.
- Regressions cover pending Telegram pins, session presentation coalescing,
  heartbeats during attachments pending beyond six seconds, delayed command
  acknowledgement, fresh commands during pending notifications, successful
  sends with supplemental notifications still pending, and late question sends.

The build uses the existing Mac packaging script and bundled runtime inputs.
No real Telegram message or AI request was sent during testing. End-to-end use
of the installed application with a phone still requires manual acceptance.
The missing directory referenced by an existing conversation must still be
restored, or a valid project selected and a new conversation created.
