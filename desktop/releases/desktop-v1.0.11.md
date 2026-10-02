# Desktop v1.0.11

## Shared embedded workspace

- The embedded OpenCode workspace and Telegram now synchronize project, session, model and thinking variant in both directions.
- Follow-mode choices and their settings card are removed. Legacy modes cannot disable synchronization.
- Unchanged idle polling no longer pulls the project list back to an old conversation. Phone actions and active answers trigger following with a visible explanation; switching is released after the answer ends.
- Desktop prompts wait for Bot attachment, including the first prompt after restart. New conversations are attached before their create response returns to the web UI.
- Local metadata, operation revisions and acknowledgements prevent stale actions and feedback loops. No network listener is added, and prompt text and credentials are not placed in the synchronization channel.
- Persisted desktop prompts survive model synchronization. A disconnected Bot cannot leave browsing permanently locked; starting a task still requires an acknowledged synchronized workspace.

## Validation

- Bot build, lint and type checking pass.
- 2,369 Bot tests pass; 7 are skipped.
- 14 embedded-web regression checks pass.
- Desktop compilation, shared-context checks and browser-login regressions pass.
- Tests use isolated fixtures and mocks, not live model tasks.

Install the new package and restart the Bot to enable its local synchronization channel. Existing configuration is preserved. External browsers are not part of the embedded two-way control path.
