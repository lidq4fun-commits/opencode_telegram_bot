import type { Api } from "grammy";
import type { IntegrationAttempt } from "@opencode/client";
import { t } from "../../i18n/index.js";
import { logger } from "../../utils/logger.js";
import {
  cancelOauthConnection,
  readOauthStatus,
} from "../../app/services/provider-connection-service.js";

const DEFAULT_POLL_INTERVAL_MS = 3000;

export interface WatchedConnection {
  integrationID: string;
  integrationName: string;
  attemptID: string;
  api: Api;
  chatId: number;
  messageId: number;
  expiresAt: number;
  timer: NodeJS.Timeout | null;
  done: boolean;
}

const watched = new Map<string, WatchedConnection>();

export function getWatchedConnection(attemptID: string): WatchedConnection | undefined {
  return watched.get(attemptID);
}

export function watchConnection(
  params: {
    api: Api;
    chatId: number;
    messageId: number;
    integrationID: string;
    integrationName: string;
    attempt: IntegrationAttempt;
  },
  pollIntervalMs: number = DEFAULT_POLL_INTERVAL_MS,
): WatchedConnection {
  const entry: WatchedConnection = {
    integrationID: params.integrationID,
    integrationName: params.integrationName,
    attemptID: params.attempt.attemptID,
    api: params.api,
    chatId: params.chatId,
    messageId: params.messageId,
    expiresAt: params.attempt.time?.expires ?? Date.now() + 10 * 60_000,
    timer: null,
    done: false,
  };

  watched.set(entry.attemptID, entry);
  schedule(entry, pollIntervalMs);
  return entry;
}

function schedule(entry: WatchedConnection, pollIntervalMs: number): void {
  if (entry.done) {
    return;
  }

  entry.timer = setTimeout(() => {
    void pollConnection(entry.attemptID, pollIntervalMs);
  }, pollIntervalMs);
  entry.timer.unref?.();
}

async function finish(entry: WatchedConnection, text: string): Promise<void> {
  entry.done = true;
  if (entry.timer) {
    clearTimeout(entry.timer);
    entry.timer = null;
  }
  watched.delete(entry.attemptID);

  await entry.api
    .editMessageText(entry.chatId, entry.messageId, text, {
      reply_markup: { inline_keyboard: [] },
    })
    .catch((error: unknown) => {
      logger.warn(`[ProviderConnect] Could not update connection message: ${entry.attemptID}`, error);
    });
}

export async function pollConnection(
  attemptID: string,
  pollIntervalMs: number = DEFAULT_POLL_INTERVAL_MS,
): Promise<void> {
  const entry = watched.get(attemptID);
  if (!entry || entry.done) {
    return;
  }

  try {
    const status = await readOauthStatus(entry.integrationID, entry.attemptID);
    if (entry.done) {
      return;
    }

    if (status.status === "pending") {
      if (Date.now() >= entry.expiresAt) {
        await finish(entry, t("connect.expired"));
        return;
      }
      schedule(entry, pollIntervalMs);
      return;
    }

    if (status.status === "complete") {
      logger.info(
        `[ProviderConnect] Connected ${entry.integrationName} (${entry.integrationID})`,
      );
      await finish(entry, t("connect.success", { provider: entry.integrationName }));
      return;
    }

    if (status.status === "failed") {
      await finish(entry, t("connect.failed", { message: status.message }));
      return;
    }

    await finish(entry, t("connect.expired"));
  } catch (error) {
    logger.warn(`[ProviderConnect] Status check failed for ${attemptID}`, error);
    if (Date.now() >= entry.expiresAt) {
      await finish(entry, t("connect.expired"));
      return;
    }
    schedule(entry, pollIntervalMs);
  }
}

/** Returns false when the attempt is gone or already finished. */
export async function cancelWatchedConnection(attemptID: string): Promise<boolean> {
  const entry = watched.get(attemptID);
  if (!entry || entry.done) {
    return false;
  }

  entry.done = true;
  if (entry.timer) {
    clearTimeout(entry.timer);
    entry.timer = null;
  }
  watched.delete(attemptID);

  try {
    await cancelOauthConnection(entry.integrationID, entry.attemptID);
  } catch (error) {
    logger.warn(`[ProviderConnect] Cancel request failed for ${attemptID}`, error);
  }

  await finish(entry, t("connect.cancelled"));
  return true;
}
