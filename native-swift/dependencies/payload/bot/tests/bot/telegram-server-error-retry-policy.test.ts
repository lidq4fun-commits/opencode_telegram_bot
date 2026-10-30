import { afterEach, describe, expect, it, vi } from "vitest";

const mocked = vi.hoisted(() => ({
  fetch: vi.fn(),
}));

vi.mock("../../src/utils/safe-background-task.js", () => ({
  safeBackgroundTask: vi.fn(),
}));

vi.mock("../../src/bot/telegram-client-options.js", () => ({
  createTelegramBotOptions: () => ({ client: { fetch: mocked.fetch } }),
}));

import { safeBackgroundTask } from "../../src/utils/safe-background-task.js";
import { createBot, shouldRetryTelegramServerError } from "../../src/bot/index.js";
import { createTestAppContainer } from "../helpers/app-container.js";
import { telegramOutageNoticeService } from "../../src/app/services/telegram-outage-notice-service.js";
import { flushTelegramOutageNotices } from "../../src/bot/telegram-outage-notices.js";

const container = createTestAppContainer();

function telegramApiResponse(errorCode: number, result?: unknown): { json(): Promise<unknown> } {
  if (errorCode === 200) {
    return { json: () => Promise.resolve({ ok: true, result }) };
  }

  return {
    json: () =>
      Promise.resolve({
        ok: false,
        error_code: errorCode,
        description: `Telegram error ${errorCode}`,
        ...(errorCode === 429 ? { parameters: { retry_after: 1 } } : {}),
      }),
  };
}

describe("bot Telegram 5xx retry policy", () => {
  it("returns a successful send before its pending supplemental outage notice", async () => {
    vi.useFakeTimers();
    const bot = createBot(container);
    // Startup tasks stay mocked; only supplemental notifications run here.
    vi.mocked(safeBackgroundTask).mockImplementation(({ taskName, task }) => {
      if (taskName === "bot.outageNotice") void task().catch(() => {});
    });
    let release!: (value: { json(): Promise<unknown> }) => void;
    mocked.fetch
      .mockResolvedValueOnce(telegramApiResponse(200, { message_id: 1 }))
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            release = resolve;
          }),
      );
    telegramOutageNoticeService.markAssistantReplyUndelivered();
    await expect(bot.api.sendMessage(123, "delivered")).resolves.toEqual({ message_id: 1 });
    await vi.advanceTimersByTimeAsync(1200);
    expect(mocked.fetch).toHaveBeenCalledTimes(2);
    release(telegramApiResponse(200, { message_id: 2 }));
    await vi.advanceTimersByTimeAsync(0);
  });

  it("paces every encrypted text chunk through the same chat budget", async () => {
    vi.useFakeTimers();
    vi.stubEnv("OPENCODE_TELEGRAM_EOT10_ENABLED", "1");
    vi.stubEnv("OPENCODE_TELEGRAM_EOT10_PASSWORD", "scheduler-test-password");
    mocked.fetch.mockResolvedValue(telegramApiResponse(200, { message_id: 1 }));
    const bot = createBot(container);
    const result = bot.api.sendMessage(123456789, "a".repeat(6000));
    await vi.advanceTimersByTimeAsync(0);
    expect(mocked.fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1100);
    expect(mocked.fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1100);
    await expect(result).resolves.toEqual({ message_id: 1 });
    expect(mocked.fetch).toHaveBeenCalledTimes(3);
  });

  it("prioritizes a question over queued progress edits without delaying incoming polling", async () => {
    vi.useFakeTimers();
    mocked.fetch.mockResolvedValue(telegramApiResponse(200, { message_id: 1 }));
    const bot = createBot(container);
    await bot.api.sendMessage(123, "initial");
    const progress = bot.api.editMessageText(123, 456, "progress");
    const question = bot.api.sendMessage(123, "approve?", {
      reply_markup: { inline_keyboard: [[{ text: "Yes", callback_data: "yes" }]] },
    });
    await bot.api.getUpdates();
    expect(mocked.fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1100);
    await question;
    expect(String(mocked.fetch.mock.calls[2]?.[0])).toContain("/sendMessage");
    await vi.advanceTimersByTimeAsync(1100);
    await progress;
    expect(String(mocked.fetch.mock.calls[3]?.[0])).toContain("/editMessageText");
  });

  it("releases long flood waits, suppresses repeated sends, and keeps polling available", async () => {
    vi.useFakeTimers();
    mocked.fetch.mockResolvedValueOnce({
      json: async () => ({
        ok: false,
        error_code: 429,
        description: "Too Many Requests: retry after 3499",
        parameters: { retry_after: 3499 },
      }),
    });
    const bot = createBot(container);

    await expect(bot.api.editMessageText(123, 456, "status")).rejects.toMatchObject({
      error_code: 429,
    });
    await expect(bot.api.sendMessage(123, "notice")).rejects.toMatchObject({ error_code: 429 });
    expect(mocked.fetch).toHaveBeenCalledTimes(1);

    mocked.fetch.mockResolvedValueOnce(telegramApiResponse(200, []));
    await expect(bot.api.getUpdates()).resolves.toEqual([]);
    mocked.fetch.mockResolvedValueOnce(telegramApiResponse(200, { message_id: 2 }));
    await expect(bot.api.sendMessage(124, "other chat")).resolves.toEqual({ message_id: 2 });

    await vi.advanceTimersByTimeAsync(3499000);
    mocked.fetch.mockResolvedValueOnce(telegramApiResponse(200, { message_id: 3 }));
    await expect(bot.api.sendMessage(123, "recovered")).resolves.toEqual({ message_id: 3 });
    expect(mocked.fetch).toHaveBeenCalledTimes(4);
  });

  afterEach(() => {
    container.cleanupProcess("test");
    telegramOutageNoticeService.__resetForTests();
    vi.useRealTimers();
    mocked.fetch.mockReset();
  });

  it("allows retries only for safe state-update methods", () => {
    for (const method of [
      "editMessageReplyMarkup",
      "editMessageText",
      "sendChatAction",
      "sendMessageDraft",
      "sendRichMessageDraft",
    ]) {
      expect(shouldRetryTelegramServerError(method)).toBe(true);
    }
  });

  it("does not retry message creation or unknown methods after 5xx", () => {
    for (const method of [
      "sendMessage",
      "sendRichMessage",
      "sendDocument",
      "sendAudio",
      "deleteMessage",
      "unknownMethod",
    ]) {
      expect(shouldRetryTelegramServerError(method)).toBe(false);
    }
  });

  it("retries a transient server error for a safe edit method", async () => {
    vi.useFakeTimers();
    mocked.fetch
      .mockResolvedValueOnce(telegramApiResponse(502))
      .mockResolvedValueOnce(telegramApiResponse(200, true));
    const bot = createBot(container);

    const result = bot.api.editMessageText(123, 456, "updated");
    await vi.advanceTimersByTimeAsync(1100);

    await expect(result).resolves.toBe(true);
    expect(mocked.fetch).toHaveBeenCalledTimes(2);
  });

  it("does not retry a transient server error when creating a message", async () => {
    mocked.fetch.mockResolvedValueOnce(telegramApiResponse(502));
    const bot = createBot(container);

    await expect(bot.api.sendMessage(123, "hello")).rejects.toMatchObject({ error_code: 502 });
    expect(mocked.fetch).toHaveBeenCalledTimes(1);
  });

  it("retries a rate limit error when creating a message", async () => {
    vi.useFakeTimers();
    mocked.fetch
      .mockResolvedValueOnce(telegramApiResponse(429))
      .mockResolvedValueOnce(telegramApiResponse(200, { message_id: 1 }));
    const bot = createBot(container);

    const result = bot.api.sendMessage(123, "hello");
    await vi.advanceTimersByTimeAsync(1100);

    await expect(result).resolves.toEqual({ message_id: 1 });
    expect(mocked.fetch).toHaveBeenCalledTimes(2);
  });

  it("retries a connection-not-established error when creating a message", async () => {
    vi.useFakeTimers();
    const refused = Object.assign(new Error("connect ECONNREFUSED"), { code: "ECONNREFUSED" });
    mocked.fetch
      .mockRejectedValueOnce(refused)
      .mockResolvedValueOnce(telegramApiResponse(200, { message_id: 1 }));
    const bot = createBot(container);

    const result = bot.api.sendMessage(123, "hello");
    await vi.advanceTimersByTimeAsync(1100);

    await expect(result).resolves.toEqual({ message_id: 1 });
    expect(mocked.fetch).toHaveBeenCalledTimes(2);
  });

  it("does not retry a connection reset when creating a message", async () => {
    const reset = Object.assign(new Error("socket hang up"), { code: "ECONNRESET" });
    mocked.fetch.mockRejectedValueOnce(reset);
    const bot = createBot(container);

    await expect(bot.api.sendMessage(123, "hello")).rejects.toThrow(
      "Network request for 'sendMessage' failed!",
    );
    expect(mocked.fetch).toHaveBeenCalledTimes(1);
  });

  it("does not retry a rate limit when sending an outage notice", async () => {
    mocked.fetch.mockResolvedValue(telegramApiResponse(429));
    const bot = createBot(container);
    telegramOutageNoticeService.markAssistantReplyUndelivered();

    await flushTelegramOutageNotices({ api: bot.api, chatId: 123 });

    expect(mocked.fetch).toHaveBeenCalledTimes(1);
  });

  it("still retries a concurrent send while an outage notice fetch is pending", async () => {
    vi.useFakeTimers();
    let releaseNotice: ((value: { json(): Promise<unknown> }) => void) | undefined;
    const refused = Object.assign(new Error("connect ECONNREFUSED"), { code: "ECONNREFUSED" });
    mocked.fetch
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            releaseNotice = resolve;
          }),
      )
      .mockRejectedValueOnce(refused)
      .mockResolvedValue(telegramApiResponse(200, { message_id: 2 }));
    const bot = createBot(container);
    telegramOutageNoticeService.markAssistantReplyUndelivered();

    const notice = flushTelegramOutageNotices({ api: bot.api, chatId: 123 });
    await vi.waitFor(() => {
      expect(releaseNotice).toBeDefined();
    });

    const other = bot.api.sendMessage(123, "hello");
    await vi.advanceTimersByTimeAsync(2500);
    await expect(other).resolves.toEqual({ message_id: 2 });

    releaseNotice?.(telegramApiResponse(429));
    await notice;
    expect(mocked.fetch).toHaveBeenCalledTimes(3);
  });

  it("does not apply the API transformer retry to startup-managed methods", async () => {
    mocked.fetch.mockResolvedValue(telegramApiResponse(429));
    const bot = createBot(container);

    await expect(bot.api.getWebhookInfo()).rejects.toMatchObject({ error_code: 429 });
    expect(mocked.fetch).toHaveBeenCalledTimes(1);
  });
});
