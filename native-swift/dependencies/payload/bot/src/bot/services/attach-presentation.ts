import type { AppContainer } from "../../app/bootstrap/app-container.js";
import type { AttachPresentationDeps } from "../../app/services/attach-service.js";
import { showPermissionRequest, type PermissionMenuDeps } from "../menus/permission-menu.js";
import { showCurrentQuestion, type QuestionMenuDeps } from "../menus/question-menu.js";
import { safeBackgroundTask } from "../../utils/safe-background-task.js";
import { logger } from "../../utils/logger.js";

type AttachPresentationFactoryDeps = Pick<
  AppContainer,
  "keyboardManager" | "pinnedMessageManager"
> &
  PermissionMenuDeps &
  QuestionMenuDeps;
type PinRequest = Parameters<AttachPresentationDeps["ensurePinnedSession"]>[0];

export function createAttachPresentation(
  deps: AttachPresentationFactoryDeps,
): AttachPresentationDeps {
  const { keyboardManager, pinnedMessageManager } = deps;
  let pendingPin: PinRequest | undefined;
  let activePin: PinRequest | undefined;
  let pendingState: { attached: boolean; busy: boolean } | undefined;
  let appliedState: string | undefined;
  let latestSessionKey = "";
  let running = false;
  const pendingPrompts = new Set<string>();
  const sessionKey = (request: PinRequest) =>
    JSON.stringify([request.session.id, request.session.directory]);

  async function displayPin(request: PinRequest): Promise<void> {
    const { session, forceFullRestore = false } = request;
    const key = sessionKey(request);
    if (key !== latestSessionKey) return;
    const pinnedState = pinnedMessageManager.getState();
    if (pinnedState.sessionId === session.id && pinnedState.messageId) {
      if (forceFullRestore)
        await pinnedMessageManager.loadContextFromHistory(session.id, session.directory);
      return;
    }
    if (pinnedState.messageId && pinnedState.sessionId === null) {
      await pinnedMessageManager.restoreExistingSession(session.id, session.title);
    } else {
      await pinnedMessageManager.onSessionChange(session.id, session.title);
    }
    if (key !== latestSessionKey) return;
    await pinnedMessageManager.loadContextFromHistory(session.id, session.directory);
    if (key !== latestSessionKey) return;
    const contextInfo = pinnedMessageManager.getContextInfo();
    if (contextInfo) keyboardManager.updateContext(contextInfo.tokensUsed, contextInfo.tokensLimit);
  }

  function schedule(): void {
    if (running) return;
    running = true;
    safeBackgroundTask({
      taskName: "attach.presentation",
      task: async () => {
        try {
          while (pendingPin || pendingState) {
            const pin = pendingPin;
            pendingPin = undefined;
            if (pin) {
              activePin = pin;
              try {
                await displayPin(pin);
                appliedState = undefined;
              } catch (error) {
                logger.warn("[AttachPresentation] Failed to display pinned session", error);
              } finally {
                activePin = undefined;
              }
              if (pendingPin) continue;
            }
            const state = pendingState;
            pendingState = undefined;
            if (state && pinnedMessageManager.isInitialized()) {
              const key = JSON.stringify(state);
              if (key === appliedState) continue;
              try {
                await pinnedMessageManager.setAttachState(state.attached, state.busy);
                appliedState = key;
              } catch (error) {
                logger.warn("[AttachPresentation] Failed to display attach state", error);
              }
            }
          }
        } finally {
          running = false;
        }
      },
    });
  }

  function displayPrompt(key: string, task: () => Promise<void>): void {
    if (pendingPrompts.has(key)) return;
    pendingPrompts.add(key);
    safeBackgroundTask({
      taskName: "attach.restorePrompt",
      task: async () => {
        try {
          await task();
        } finally {
          pendingPrompts.delete(key);
        }
      },
    });
  }

  return {
    async ensurePinnedSession(request) {
      if (!pinnedMessageManager.isInitialized())
        pinnedMessageManager.initialize(request.api, request.chatId);
      keyboardManager.initialize(request.api, request.chatId);
      latestSessionKey = sessionKey(request);
      const existing = pendingPin ?? activePin;
      if (
        existing &&
        sessionKey(existing) === latestSessionKey &&
        (!request.forceFullRestore || existing.forceFullRestore)
      )
        return;
      pendingPin = request;
      schedule();
    },
    async syncAttachState(attached, busy) {
      pendingState = { attached, busy };
      schedule();
    },
    async showCurrentQuestion(api, chatId) {
      const questionState = deps.interactionManager.getPayload("question");
      const index = deps.questionManager.getCurrentIndex();
      const key = `question:${deps.questionManager.getRequestID()}:${index}`;
      displayPrompt(key, async () => {
        if (
          !questionState ||
          deps.interactionManager.getPayload("question") !== questionState ||
          deps.questionManager.getCurrentIndex() !== index
        )
          return;
        await showCurrentQuestion(api, chatId, deps);
      });
    },
    async showPermissionRequest(api, chatId, request) {
      const generation = deps.permissionManager.getGeneration();
      displayPrompt(`permission:${generation}:${request.id}`, () =>
        showPermissionRequest(api, chatId, request, deps, generation),
      );
    },
  };
}
