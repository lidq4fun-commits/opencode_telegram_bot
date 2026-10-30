import { t } from "../../../i18n/index.js";
import { logger } from "../../../utils/logger.js";
import { markAttachedSessionIdle } from "../../../app/services/attach-service.js";
import { shouldSuppressUserAbortSessionError } from "../../../app/managers/abort-suppression-manager.js";
import {
  clearPromptResponseMode,
  consumePromptResponseMode,
  startPromptRun,
} from "../../handlers/prompt.js";
import {
  forgetPromptForModelRetry,
  isModelUnavailableMessage,
  retryPromptAfterModelUnavailable,
} from "../../../app/services/prompt-model-retry.js";
import { getTtsMode } from "../../../app/stores/settings-store.js";
import { opencodeClient } from "../../../opencode/client.js";
import { dispatchNextQueuedPrompt } from "../../handlers/prompt-queue-dispatch.js";
import { resetStreamThrottle } from "../../streaming/stream-throttle.js";
import {
  formatSessionMessage,
  isCompactProgressMode,
  type EventHandlerDeps,
} from "./handler-context.js";
import { closeForegroundRun } from "./run-close.js";

const SESSION_RETRY_PREFIX = "🔁";

type SessionLifecycleDeps = EventHandlerDeps<
  | "assistantRunState"
  | "attachManager"
  | "foregroundSessionState"
  | "keyboardManager"
  | "scheduledTaskRuntime"
  | "summaryAggregator"
>;

/** Session idle, error and retry. */
export function registerSessionLifecycleHandlers(deps: SessionLifecycleDeps): void {
  const { runtime, policy, summaryAggregator } = deps;

  summaryAggregator.setOnSessionIdle(async (sessionId, { interrupted }) => {
    resetStreamThrottle(sessionId);
    forgetPromptForModelRetry(sessionId);
    await markAttachedSessionIdle(sessionId, deps);
    // Dropped immediately when this session is no longer current: the early
    // returns below would otherwise leave a compact-progress timer armed.
    // A still-current session keeps the card until after in-flight completion
    // work, then finalizes it (delete or finished summary).
    const canFinalizeCompactProgress =
      Boolean(policy.getDestination(sessionId)) && policy.isForegroundSession(sessionId);
    // Background operations outlive the turn only in the session the user follows.
    if (!canFinalizeCompactProgress) {
      runtime.stopBackgroundOperations("session_idle", sessionId);
    }
    runtime.clearToolTracking(sessionId, "session_idle", canFinalizeCompactProgress);
    if (!canFinalizeCompactProgress) {
      runtime.compactProgressStreamer.clearSession(sessionId, "session_idle");
    }
    await runtime.getCompletionTask(sessionId)?.catch(() => undefined);

    // A turn the bot did not start that was stopped from a client ends like an aborted one.
    if (
      interrupted &&
      deps.assistantRunState.hasRun(sessionId) &&
      !deps.assistantRunState.hasBotRun(sessionId)
    ) {
      deps.assistantRunState.clearRun(sessionId, "session_interrupted");
    }

    const completedRun = deps.assistantRunState.isResponseCompleted(sessionId)
      ? deps.assistantRunState.finishRun(sessionId, "session_idle")
      : null;
    clearPromptResponseMode(sessionId);

    const destination = policy.getDestination(sessionId);
    if (!destination) {
      runtime.compactProgressStreamer.clearSession(sessionId, "session_idle");
      deps.foregroundSessionState.markIdle(sessionId);
      return;
    }

    if (!policy.isForegroundSession(sessionId)) {
      runtime.compactProgressStreamer.clearSession(sessionId, "session_idle");
      deps.foregroundSessionState.markIdle(sessionId);
      await deps.scheduledTaskRuntime.flushDeferredDeliveries();
      return;
    }

    try {
      await closeForegroundRun(deps, sessionId, destination, completedRun, "session_idle");
    } catch (err) {
      logger.error("[Bot] Failed to send session idle footer:", err);
    } finally {
      deps.foregroundSessionState.markIdle(sessionId);
      await deps.scheduledTaskRuntime.flushDeferredDeliveries();
      void dispatchNextQueuedPrompt();
    }
  });

  summaryAggregator.setOnSessionError(async (sessionId, message) => {
    await markAttachedSessionIdle(sessionId, deps);
    const destination = policy.getDestination(sessionId);
    const keepBackground = Boolean(destination) && policy.isForegroundSession(sessionId);
    if (!keepBackground) {
      runtime.stopBackgroundOperations("session_error", sessionId);
    }
    runtime.clearToolTracking(sessionId, "session_error", keepBackground);

    if (!destination) {
      clearPromptResponseMode(sessionId);
      runtime.compactProgressStreamer.clearSession(sessionId, "session_error_no_bot_context");
      deps.assistantRunState.clearRun(sessionId, "session_error_no_bot_context");
      deps.foregroundSessionState.markIdle(sessionId);
      return;
    }

    if (!policy.isForegroundSession(sessionId)) {
      clearPromptResponseMode(sessionId);
      runtime.clearAssistantResponseSession(sessionId, "session_error_not_current");
      runtime.toolCallStreamer.clearSession(sessionId, "session_error_not_current");
      runtime.compactProgressStreamer.clearSession(sessionId, "session_error_not_current");
      deps.assistantRunState.clearRun(sessionId, "session_error_not_current");
      deps.foregroundSessionState.markIdle(sessionId);
      await deps.scheduledTaskRuntime.flushDeferredDeliveries();
      return;
    }

    runtime.clearAssistantResponseSession(sessionId, "session_error");
    // Parked cards keep counting their background operations, as their full-mode lines do.
    runtime.compactProgressStreamer.discardOpenCard(sessionId, "session_error");
    const errorResponseMode =
      consumePromptResponseMode(sessionId) ?? (getTtsMode() === "all" ? "text_and_tts" : "text_only");
    deps.assistantRunState.clearRun(sessionId, "session_error");
    await Promise.all([
      runtime.toolMessageBatcher.flushSession(sessionId, "session_error"),
      runtime.toolCallStreamer.breakSession(sessionId, "session_error"),
    ]);

    const normalizedMessage = message.trim() || t("common.unknown_error");
    if (shouldSuppressUserAbortSessionError(sessionId, normalizedMessage)) {
      logger.debug(`[Bot] Suppressed user-initiated abort error: session=${sessionId}`);
      deps.foregroundSessionState.markIdle(sessionId);
      await deps.scheduledTaskRuntime.flushDeferredDeliveries();
      return;
    }

    // A provider-config fetch can fail for a few seconds (VPN/proxy or TLS hiccup) and the
    // engine then reports the model as unavailable. Re-sending the same prompt recovers the
    // turn instead of showing a dead error.
    if (isModelUnavailableMessage(normalizedMessage)) {
      const retried = await retryPromptAfterModelUnavailable(sessionId, {
        send: (request) =>
          opencodeClient.session.promptAsync(
            request as unknown as Parameters<typeof opencodeClient.session.promptAsync>[0],
          ),
        onRetryAttempt: async (attempt, total) => {
          if (attempt > 1) {
            return;
          }

          await runtime.delivery
            .sendText(
              destination,
              t("bot.model_unavailable_retrying", {
                attempt: String(attempt),
                total: String(total),
              }),
            )
            .catch((err) => {
              logger.warn("[Bot] Failed to send the model retry notice:", err);
            });
        },
        onRetryAccepted: async (request) => {
          await startPromptRun({ id: sessionId, directory: request.directory }, deps, {
            agent: request.agent,
            providerID: request.model?.providerID ?? "",
            modelID: request.model?.modelID ?? "",
            responseMode: errorResponseMode,
          });
        },
      });

      if (retried) {
        logger.info(`[Bot] Model unavailable error recovered by an automatic retry: ${sessionId}`);
        return;
      }
    }

    const formattedError = formatSessionMessage(normalizedMessage);
    const errorText = isModelUnavailableMessage(normalizedMessage)
      ? t("bot.model_unavailable_failed", { message: formattedError })
      : t("bot.session_error", { message: formattedError });

    await runtime.delivery
      .sendText(destination, errorText)
      .catch((err) => {
        logger.error("[Bot] Failed to send session.error message:", err);
      });

    deps.foregroundSessionState.markIdle(sessionId);
    await deps.scheduledTaskRuntime.flushDeferredDeliveries();
    void dispatchNextQueuedPrompt();
  });

  summaryAggregator.setOnSessionRetry(async ({ sessionId, message }) => {
    if (!policy.getDestination(sessionId) || !policy.isForegroundSession(sessionId)) {
      return;
    }

    if (isCompactProgressMode()) {
      runtime.compactProgressStreamer.updateActivity(sessionId, t("progress.compact.retrying"));
      return;
    }

    const retryMessage = t("bot.session_retry", { message: formatSessionMessage(message) });
    runtime.toolCallStreamer.replaceByPrefix(sessionId, SESSION_RETRY_PREFIX, retryMessage);
  });
}
