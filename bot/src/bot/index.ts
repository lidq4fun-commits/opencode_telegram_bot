import { Bot, Context, InputFile } from "grammy";
import { config } from "../config.js";
import { getCurrentProject } from "../app/stores/settings-store.js";
import type { AppContainer } from "../app/bootstrap/app-container.js";
import {
  configureAttachPresentation,
  restoreAttachedCurrentSession,
} from "../app/services/attach-service.js";
import { refreshModelViewsAfterLateCatalogSettle } from "./services/model-views.js";
import { logger } from "../utils/logger.js";
import { safeBackgroundTask } from "../utils/safe-background-task.js";
import { withTelegramRateLimitRetry } from "../utils/telegram-rate-limit-retry.js";
import { telegramOutageNoticeService } from "../app/services/telegram-outage-notice-service.js";
import { flushTelegramOutageNotices, isUnretriedTelegramSend } from "./telegram-outage-notices.js";
import { LocalCommandRegistry } from "../app/services/local-command-registry.js";
import { registerCallbackRouter } from "./callbacks/callback-router.js";
import { initializePromptQueueDispatch } from "./handlers/prompt-queue-dispatch.js";
import { normalizeRichMessage } from "./handlers/rich-message-handler.js";
import { authMiddleware } from "./middleware/auth.js";
import { interactionGuardMiddleware } from "./middleware/interaction-guard.js";
import { staleUpdateMiddleware } from "./middleware/stale-update.js";
import { beginDesktopMobileActivity, waitForDesktopSyncIdle } from "../app/services/desktop-sync-service.js";
import {
  ensureCommandsInitialized,
  registerCommandRouter,
} from "./routers/command-router.js";
import { registerMessageRouter } from "./routers/message-router.js";
import { createAttachPresentation } from "./services/attach-presentation.js";
import { createTelegramBotOptions } from "./telegram-client-options.js";
import { downloadTelegramFile } from "../app/services/file-download-service.js";
import {
  decryptEot10,
  encryptEot10,
  encryptEot10RichMessage,
  isEot10Message,
  decryptEot10MediaBuffer, isEot10MediaDocument, cacheDecryptedEot10Media,
  encryptEot10MediaInput } from "../../EOT10Crypto.mjs";

const TRANSIENT_RETRY_SAFE_TELEGRAM_METHODS = new Set([
  "editMessageReplyMarkup",
  "editMessageText",
  "sendChatAction",
  "sendMessageDraft",
  "sendRichMessageDraft",
]);

const STARTUP_MANAGED_TELEGRAM_METHODS = new Set(["deleteWebhook", "getMe", "getWebhookInfo"]);

const CHAT_DELIVERY_TELEGRAM_METHODS = new Set([
  "sendMessage",
  "sendRichMessage",
  "editMessageText",
  "sendDocument",
  "sendAudio",
  "sendPhoto",
]);

interface TelegramApiErrorResponse {
  ok: false;
  error_code: number;
  description: string;
  parameters?: object;
}

class TelegramApiResponseError extends Error {
  constructor(readonly response: TelegramApiErrorResponse) {
    super(response.description ?? "Telegram API request failed");
    Object.assign(this, response);
  }
}

export function shouldRetryTelegramServerError(method: string): boolean {
  return TRANSIENT_RETRY_SAFE_TELEGRAM_METHODS.has(method);
}

function isTelegramApiErrorResponse(response: unknown): response is TelegramApiErrorResponse {
  return (
    typeof response === "object" &&
    response !== null &&
    Reflect.get(response, "ok") === false &&
    typeof Reflect.get(response, "error_code") === "number" &&
    typeof Reflect.get(response, "description") === "string"
  );
}

export function createBot(
  container: AppContainer,
  localCommandRegistry = LocalCommandRegistry.empty(),
): Bot<Context> {
  container.resetInteractions("bot_startup");
  container.attachManager.clear("bot_startup");
  container.resetRuntimeStreams("bot_startup");

  const botOptions = createTelegramBotOptions(config.telegram);
  const bot = new Bot(config.telegram.token, botOptions);
  const eot10Enabled = process.env.OPENCODE_TELEGRAM_EOT10_ENABLED === "1" &&
    Boolean(process.env.OPENCODE_TELEGRAM_EOT10_PASSWORD);

  configureAttachPresentation(createAttachPresentation(container));

  container.setTelegramContext(bot, config.telegram.allowedUserId);

  initializePromptQueueDispatch({ ...container, bot });

  container.setReadyRestoreHandler(async (reason) => {
    const restored = await restoreAttachedCurrentSession({
      ...container,
      bot,
      chatId: config.telegram.allowedUserId,
      forceFullRestore: true,
    });
    refreshModelViewsAfterLateCatalogSettle(container, reason);

    if (restored) {
      logger.info(`[Bot] Restored followed session after OpenCode ready: reason=${reason}`);
      return;
    }

    const currentProject = getCurrentProject();
    if (config.bot.trackBackgroundSessions && currentProject?.worktree) {
      await container.ensureEventSubscription(currentProject.worktree);
      logger.info(
        `[Bot] Started background session tracking after OpenCode ready: reason=${reason}, directory=${currentProject.worktree}`,
      );
    }
  });

  container.startHeartbeat();

  let lastGetUpdatesTime = Date.now();
  bot.api.config.use(async (prev, method, payload, signal) => {
    if (method === "getUpdates") {
      const now = Date.now();
      const timeSinceLast = now - lastGetUpdatesTime;
      logger.debug(`[Bot API] getUpdates called (${timeSinceLast}ms since last)`);
      lastGetUpdatesTime = now;
      return prev(method, payload, signal);
    }

    if (STARTUP_MANAGED_TELEGRAM_METHODS.has(method)) {
      return prev(method, payload, signal);
    }

    if (method === "sendMessage") {
      logger.debug(`[Bot API] sendMessage to chat ${(payload as { chat_id?: number }).chat_id}`);
    }

    try {
      const runCall = async () => {
        const target = eot10Enabled && String((payload as { chat_id?: number }).chat_id ?? "") ===
          String(config.telegram.allowedUserId);
        const secured = target ? { ...payload } as Record<string, unknown> : payload as Record<string, unknown>;
        let securedMethod = method;
        if (target) {
          if (typeof secured.text === "string") {
            secured.text = encryptEot10(secured.text);
            delete secured.entities;
          }
          if (typeof secured.caption === "string") {
            secured.caption = encryptEot10(secured.caption);
            delete secured.caption_entities;
          }
          if (secured.rich_message) secured.rich_message = encryptEot10RichMessage(secured.rich_message);
          if (method === "sendMediaGroup") {
            if (!Array.isArray(secured.media) || !secured.media.length) throw new Error("Invalid EOT10 media group");
            secured.media = await Promise.all(secured.media.map(async (item: Record<string, unknown>) => {
              const encrypted = await encryptEot10MediaInput(item.media);
              const result: Record<string, unknown> = { ...item, type: "document", media: new InputFile(encrypted.buffer, encrypted.filename) };
              delete result.thumbnail; delete result.thumb; delete result.cover;
              if (typeof item.caption === "string") result.caption = encryptEot10(item.caption);
              delete result.caption_entities;
              return result;
            }));
          }
          const mediaField = ({ sendPhoto: "photo", sendDocument: "document", sendAudio: "audio",
            sendVoice: "voice", sendVideo: "video", sendAnimation: "animation",
            sendVideoNote: "video_note", sendSticker: "sticker" } as Record<string, string>)[method];
          if (mediaField && secured[mediaField]) {
            const encrypted = await encryptEot10MediaInput(secured[mediaField]);
            secured.document = new InputFile(encrypted.buffer, encrypted.filename);
            delete secured[mediaField]; delete secured.thumbnail; delete secured.thumb; delete secured.cover;
            securedMethod = "sendDocument" as typeof method;
          }
        }
        const response = await prev(securedMethod, secured as typeof payload, signal);
        if (isTelegramApiErrorResponse(response)) {
          throw new TelegramApiResponseError(response);
        }
        return response;
      };
      const response = isUnretriedTelegramSend()
        ? await runCall()
        : await withTelegramRateLimitRetry(runCall, {
            maxRetries: 5,
            retryTransientServerErrors: shouldRetryTelegramServerError(method),
            onRetry: ({ attempt, retryAfterMs, error }) => {
              logger.warn(
                `[Bot API] Retryable Telegram error on ${method}, retrying in ${retryAfterMs}ms (attempt=${attempt})`,
                error,
              );
            },
          });

      if (CHAT_DELIVERY_TELEGRAM_METHODS.has(method)) {
        const chatId = (payload as { chat_id?: number }).chat_id;
        if (typeof chatId === "number") {
          telegramOutageNoticeService.noteChatSendSucceeded();
          await flushTelegramOutageNotices({ api: bot.api, chatId });
        }
      }

      return response;
    } catch (error) {
      if (error instanceof TelegramApiResponseError) {
        return error.response;
      }
      throw error;
    }
  });

  bot.use((ctx, next) => {
    const hasCallbackQuery = !!ctx.callbackQuery;
    const hasMessage = !!ctx.message;
    const callbackData = ctx.callbackQuery?.data || "N/A";
    logger.debug(
      `[DEBUG] Incoming update: hasCallbackQuery=${hasCallbackQuery}, hasMessage=${hasMessage}, callbackData=${callbackData}`,
    );
    return next();
  });

  bot.use(async (ctx, next) => {
    const authorized = String(ctx.from?.id ?? "") === String(config.telegram.allowedUserId);
    const privateChat = authorized && ctx.chat?.type === "private" &&
      String(ctx.chat.id) === String(config.telegram.allowedUserId);
    if (!privateChat) { await next(); return; }
    const message = ctx.message;
    if (!eot10Enabled) {
      if (isEot10Message(message?.text) || isEot10MediaDocument(message?.document)) {
        await ctx.reply("桌面端加密未开启，请先开启 EOT10 后重试。"); return;
      }
      await next(); return;
    }
    if (message?.document) {
      if (!isEot10MediaDocument(message.document)) { await ctx.reply("请发送 EOT10 加密附件。"); return; }
      try {
        const encrypted = await downloadTelegramFile(ctx.api, message.document.file_id);
        const clear = decryptEot10MediaBuffer(encrypted.buffer);
        cacheDecryptedEot10Media(message.document.file_id, { buffer: clear.buffer, filePath: encrypted.filePath });
        message.document.file_name = clear.metadata.filename;
        message.document.mime_type = clear.metadata.mimeType;
        message.document.file_size = clear.buffer.length;
        if (message.caption) message.caption = decryptEot10(message.caption);
      } catch (error) {
        logger.warn("[EOT10] Invalid encrypted media", error);
        await ctx.reply("加密附件验证失败，请检查密码或重新发送。"); return;
      }
    } else if (typeof message?.text === "string") {
      try {
        const clearText = decryptEot10(message.text);
        message.text = clearText;
        const command = clearText.match(/^\/[A-Za-z0-9_]+(?:@[A-Za-z0-9_]+)?(?=\s|$)/);
        if (command) message.entities = [{ type: "bot_command", offset: 0, length: command[0].length }];
      } catch (error) {
        logger.warn("[EOT10] Invalid encrypted text", error);
        await ctx.reply("加密消息验证失败，请检查密码。"); return;
      }
    } else if (message) {
      await ctx.reply("当前媒体类型需以 EOT10 加密文档发送。"); return;
    }
    await next();
  });
  bot.use(authMiddleware);
  bot.use(staleUpdateMiddleware);
  bot.use(async (ctx, next) => {
    if (process.env.OPENCODE_TELEGRAM_DESKTOP_SYNC !== "1" || (!ctx.message && !ctx.callbackQuery)) {
      await next();
      return;
    }
    const complete = beginDesktopMobileActivity();
    try {
      await waitForDesktopSyncIdle();
      await next();
    } finally {
      complete();
    }
  });
  bot.on("message:rich_message", normalizeRichMessage);
  bot.use((ctx, next) => ensureCommandsInitialized(ctx, next, localCommandRegistry));
  const guardDeps = { ...container, localCommandRegistry };
  bot.use((ctx, next) => interactionGuardMiddleware(ctx, next, guardDeps));

  registerCommandRouter(bot, { container, localCommandRegistry });
  registerCallbackRouter(bot, { container });
  registerMessageRouter(bot, { container });

  safeBackgroundTask({
    taskName: "bot.clearGlobalCommands",
    task: async () => {
      try {
        await Promise.all([
          bot.api.setMyCommands([], { scope: { type: "default" } }),
          bot.api.setMyCommands([], { scope: { type: "all_private_chats" } }),
        ]);
        return { success: true as const };
      } catch (error) {
        return { success: false as const, error };
      }
    },
    onSuccess: (result) => {
      if (result.success) {
        logger.debug("[Bot] Cleared global commands (default and all_private_chats scopes)");
        return;
      }

      logger.warn("[Bot] Could not clear global commands:", result.error);
    },
  });

  bot.catch((err) => {
    logger.error("[Bot] Unhandled error in bot:", err);
    container.resetInteractions("bot_unhandled_error");
    if (err.ctx) {
      logger.error(
        "[Bot] Error context - update type:",
        err.ctx.update ? Object.keys(err.ctx.update) : "unknown",
      );
    }
  });

  return bot;
}

export function restoreFollowedSessionOnPollingStart(
  bot: Bot<Context>,
  container: AppContainer,
): void {
  safeBackgroundTask({
    taskName: "bot.restoreAfterPollingStart",
    task: () =>
      restoreAttachedCurrentSession({
        ...container,
        bot,
        chatId: config.telegram.allowedUserId,
        forceFullRestore: true,
      }),
  });
}
