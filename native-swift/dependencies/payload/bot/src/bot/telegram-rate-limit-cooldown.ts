import { getTelegramRetryAfterMs } from "../utils/telegram-rate-limit-retry.js";

// Long flood waits must not hold grammY's sequential update loop or session attach.
export const MAX_TELEGRAM_RATE_LIMIT_WAIT_MS = 5000;

export class TelegramRateLimitCooldown {
  private readonly chats = new Map<string, { until: number; error: unknown }>();

  async run<T>(chatId: string | number | undefined, operation: () => Promise<T>): Promise<T> {
    if (chatId === undefined) {
      return operation();
    }
    const key = String(chatId);
    const cooldown = this.chats.get(key);
    if (cooldown && Date.now() < cooldown.until) {
      throw cooldown.error;
    }
    this.chats.delete(key);

    try {
      return await operation();
    } catch (error) {
      if (typeof error === "object" && error !== null && Reflect.get(error, "error_code") === 429) {
        const delay = getTelegramRetryAfterMs(error);
        if (delay !== null && delay > MAX_TELEGRAM_RATE_LIMIT_WAIT_MS) {
          this.chats.set(key, { until: Date.now() + delay, error });
        }
      }
      throw error;
    }
  }
}
