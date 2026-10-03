import type { Context } from "grammy";
import {
  MAX_QUEUED_PROMPTS,
  MAX_QUEUED_MEDIA_BYTES,
  promptQueue,
  type QueuedPromptInput,
} from "../../app/managers/prompt-queue-manager.js";
import type { IncomingPrompt } from "../../app/types/prompt.js";
import {
  cancelInboxPrompt,
  reconcileInboxPrompts,
} from "../../app/services/prompt-inbox-service.js";
import { isForegroundBusy } from "../../app/services/run-control-service.js";
import { getCurrentSession } from "../../app/services/session-service.js";
import { getPromptQueueMode } from "../../app/stores/settings-store.js";
import { t } from "../../i18n/index.js";
import { logger } from "../../utils/logger.js";
import { isReplyKeyboardButtonText } from "../message-patterns.js";
import { admitPromptToInbox, startInboxPromptRun, type ProcessPromptDeps } from "./prompt.js";

// The queue helpers are called from the guard and the media handlers without
// deps, so the dispatcher receives them once at startup instead. Until then the
// chat counts as not busy and nothing is queued.
let promptDeps: ProcessPromptDeps | null = null;

export function initializePromptQueueDispatch(deps: ProcessPromptDeps): void {
  promptDeps = deps;
}

function isBusy(): boolean {
  return promptDeps !== null && isForegroundBusy(promptDeps);
}

function isPromptQueueEnabled(): boolean {
  return getPromptQueueMode() !== "off";
}

/** Whether the text is user prompt content rather than a command or a button press. */
function isQueueablePrompt(input: IncomingPrompt): boolean {
  const normalizedText = input.text.trim();
  const hasContent =
    Boolean(normalizedText) || input.fileParts.length > 0 || input.photos.length > 0;
  return hasContent && !normalizedText.startsWith("/") && !isReplyKeyboardButtonText(input.text);
}

/**
 * Whether the user should be told that this message could have been queued.
 * True only when the setting is off and the text would otherwise have been queued.
 */
export function shouldSuggestPromptQueue(input: IncomingPrompt): boolean {
  return !isPromptQueueEnabled() && isQueueablePrompt(input);
}

export function canQueueMediaPrompt(ctx: Context): boolean {
  const message = ctx.message;
  return Boolean(
    isPromptQueueEnabled() &&
    message &&
    (message.voice || message.audio || message.photo?.length || message.document),
  );
}

/**
 * Queues a prepared prompt that arrived while the session was busy.
 * Returns false when queueing does not apply, so the caller keeps its old behaviour.
 */
export async function tryEnqueuePrompt(ctx: Context, input: QueuedPromptInput): Promise<boolean> {
  if (!promptDeps || !isPromptQueueEnabled() || !ctx.chat || !isQueueablePrompt(input)) {
    return false;
  }

  await sendPromptToInbox(ctx, input, promptDeps);
  return true;
}

/**
 * Sends a busy-time prompt into the OpenCode V2 session inbox and mirrors it as a queue
 * item. The reservation keeps the cap honest while the prompt is on its way and tells
 * whether the queue was cleared in the meantime.
 */
async function sendPromptToInbox(
  ctx: Context,
  input: QueuedPromptInput,
  deps: ProcessPromptDeps,
): Promise<void> {
  const delivery = getPromptQueueMode() === "steer" ? "steer" : "queue";
  const reservationId = promptQueue.reserve();
  if (!reservationId) {
    logger.info(`[PromptQueue] Rejected inbox prompt: queue is full (max=${MAX_QUEUED_PROMPTS})`);
    await replyWithKeyboard(ctx, t("queue.full", { max: String(MAX_QUEUED_PROMPTS) }));
    return;
  }

  const admitted = await admitPromptToInbox(ctx, input, deps, delivery);
  if (!admitted) {
    promptQueue.releaseReservation(reservationId);
    return;
  }

  const inbox = { sessionId: admitted.sessionId, inboxId: admitted.inboxId, delivery } as const;

  // OpenCode may deliver the prompt before the send returns (the turn had just ended):
  // the pickup has already been shown, so there is no button, only the run to open.
  if (promptQueue.wasInboxIdDelivered(admitted.inboxId)) {
    if (!promptQueue.releaseReservation(reservationId)) {
      return;
    }
    if (!deps.assistantRunState.hasBotRun(admitted.sessionId)) {
      const session = getCurrentSession();
      if (session?.id === admitted.sessionId) {
        await startInboxPromptRun(session, deps, input.responseMode);
      }
    }
    await replyInboxAdmission(ctx, delivery);
    return;
  }

  const item = promptQueue.confirmReservation(reservationId, {
    displayText: input.displayText ?? input.text,
    inbox,
    ...(input.responseMode ? { responseMode: input.responseMode } : {}),
  });
  if (!item) {
    // Cleared by /abort or a session change while the prompt was on its way.
    await cancelInboxPrompt(inbox, "withdrawn_during_admission");
    return;
  }

  logger.info(
    `[PromptQueue] Prompt sent to the session inbox: delivery=${delivery}, size=${promptQueue.size()}/${MAX_QUEUED_PROMPTS}`,
  );
  await replyInboxAdmission(ctx, delivery);
}

async function replyInboxAdmission(ctx: Context, delivery: "steer" | "queue"): Promise<void> {
  const params = { count: String(promptQueue.size()), max: String(MAX_QUEUED_PROMPTS) };
  await replyWithKeyboard(
    ctx,
    delivery === "steer" ? t("queue.steer_added", params) : t("queue.added", params),
  );
}

export async function tryEnqueuePromptIfBusy(
  ctx: Context,
  input: QueuedPromptInput,
): Promise<boolean> {
  return isBusy() && tryEnqueuePrompt(ctx, input);
}

/**
 * Rejects a busy queued-media candidate before handlers download or encode it.
 * Media sizes are raw Telegram file_size values, not expanded data-URI bytes.
 */
export async function rejectQueuedMediaBeforePreparation(
  ctx: Context,
  mediaBytes: number | undefined,
): Promise<boolean> {
  if (!isBusy() || !isPromptQueueEnabled() || !ctx.chat) {
    return false;
  }
  if (promptQueue.isFull()) {
    await replyWithKeyboard(ctx, t("queue.full", { max: String(MAX_QUEUED_PROMPTS) }));
    return true;
  }
  // Bound each upload before downloading, even when V2 stores the queued payload.
  if (
    typeof mediaBytes !== "number" ||
    !Number.isSafeInteger(mediaBytes) ||
    mediaBytes < 0 ||
    !promptQueue.canAcceptMedia(mediaBytes)
  ) {
    await replyWithKeyboard(ctx, t("queue.media_limit", { maxSizeMb: formatQueuedMediaLimit() }));
    return true;
  }
  return false;
}

function formatQueuedMediaLimit(): string {
  return String(MAX_QUEUED_MEDIA_BYTES / (1024 * 1024));
}

/** Reconcile the mirror; OpenCode, not the bot, delivers queued prompts. */
export async function dispatchNextQueuedPrompt(): Promise<void> {
  if (!promptDeps) return;
  const session = getCurrentSession();
  if (session) await reconcileInboxPrompts(session.id);
}

async function replyWithKeyboard(ctx: Context, text: string): Promise<void> {
  const keyboard = promptDeps?.keyboardManager.getKeyboard();
  await ctx.reply(text, keyboard ? { reply_markup: keyboard } : {}).catch((err) => {
    logger.error("[PromptQueue] Failed to send queue reply:", err);
  });
}

/** Test helper: clears the stored context and dependencies. */
export function __resetPromptQueueDispatchForTests(): void {
  promptDeps = null;
}
