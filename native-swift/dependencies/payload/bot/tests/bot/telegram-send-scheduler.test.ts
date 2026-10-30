import { afterEach, describe, expect, it, vi } from "vitest";
import {
  TelegramSendScheduler,
  withTelegramDeliveryPriority,
} from "../../src/bot/telegram-send-scheduler.js";

describe("Telegram shared send scheduling", () => {
  afterEach(() => vi.useRealTimers());

  it("shares spacing across sends and edits, with important messages ahead of progress", async () => {
    vi.useFakeTimers();
    const scheduler = new TelegramSendScheduler();
    const calls: string[] = [];
    const send = (name: string) => async () => {
      calls.push(name);
      return name;
    };
    const first = scheduler.schedule("sendMessage", { chat_id: 1 }, send("first"));
    const progress = scheduler.schedule(
      "editMessageText",
      { chat_id: 1, message_id: 2 },
      send("progress"),
    );
    const question = scheduler.schedule(
      "sendMessage",
      { chat_id: 1, reply_markup: {} },
      send("question"),
    );
    await first;
    expect(calls).toEqual(["first"]);
    await vi.advanceTimersByTimeAsync(1099);
    expect(calls).toEqual(["first"]);
    await vi.advanceTimersByTimeAsync(1);
    await question;
    expect(calls).toEqual(["first", "question"]);
    await vi.advanceTimersByTimeAsync(1100);
    await progress;
    expect(calls).toEqual(["first", "question", "progress"]);
  });

  it("coalesces a hundred pending edits into the latest state and promotes the final edit", async () => {
    vi.useFakeTimers();
    const scheduler = new TelegramSendScheduler();
    await scheduler.schedule("sendMessage", { chat_id: 1 }, async () => true);
    const edit = vi.fn(async (version: number) => version);
    const other = scheduler.schedule(
      "editMessageText",
      { chat_id: 1, message_id: 6 },
      async () => "other",
    );
    const updates = Array.from({ length: 100 }, (_, version) =>
      scheduler.schedule("editMessageText", { chat_id: 1, message_id: 5 }, () => edit(version)),
    );
    const final = withTelegramDeliveryPriority("important", () =>
      scheduler.schedule("editMessageText", { chat_id: 1, message_id: 5 }, () => edit(100)),
    );
    await vi.advanceTimersByTimeAsync(1100);
    await expect(Promise.all(updates)).resolves.toEqual(Array(100).fill(100));
    await expect(final).resolves.toBe(100);
    expect(edit).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1100);
    await expect(other).resolves.toBe("other");
  });

  it("keeps polling, other chats and important sends working during a slow progress request", async () => {
    vi.useFakeTimers();
    const scheduler = new TelegramSendScheduler();
    let release!: () => void;
    const slow = withTelegramDeliveryPriority("progress", () =>
      scheduler.schedule(
        "sendMessage",
        { chat_id: 1 },
        () =>
          new Promise<void>((resolve) => {
            release = resolve;
          }),
      ),
    );
    const question = scheduler.schedule("sendMessage", { chat_id: 1 }, async () => "question");
    await expect(scheduler.schedule("getUpdates", {}, async () => "updates")).resolves.toBe(
      "updates",
    );
    await expect(
      scheduler.schedule("sendMessage", { chat_id: 2 }, async () => "other"),
    ).resolves.toBe("other");
    await vi.advanceTimersByTimeAsync(1100);
    await expect(question).resolves.toBe("question");
    release();
    await slow;
  });

  it("propagates send failures without blocking subsequent work", async () => {
    vi.useFakeTimers();
    const scheduler = new TelegramSendScheduler();
    const error = new Error("transport failed");
    await expect(
      scheduler.schedule("sendMessage", { chat_id: 1 }, async () => {
        throw error;
      }),
    ).rejects.toBe(error);
    const next = scheduler.schedule("sendMessage", { chat_id: 1 }, async () => "recovered");
    await vi.advanceTimersByTimeAsync(1100);
    await expect(next).resolves.toBe("recovered");
  });

  it("pauses the whole chat during a server retry_after, including already queued work", async () => {
    vi.useFakeTimers();
    const scheduler = new TelegramSendScheduler();
    await scheduler.schedule("sendMessage", { chat_id: 1 }, async () => true);
    const operation = vi.fn(async () => "next");
    const next = scheduler.schedule("sendMessage", { chat_id: 1 }, operation);
    scheduler.defer(1, 3000);
    await vi.advanceTimersByTimeAsync(2999);
    expect(operation).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    await expect(next).resolves.toBe("next");
  });

  it("remembers a short flood wait returned after the queue became idle", async () => {
    vi.useFakeTimers();
    const scheduler = new TelegramSendScheduler();
    await scheduler.schedule("sendMessage", { chat_id: 1 }, async () => true);
    await vi.advanceTimersByTimeAsync(2000);
    scheduler.defer(1, 3000);
    const send = vi.fn(async () => true);
    const next = scheduler.schedule("sendMessage", { chat_id: 1 }, send);
    await vi.advanceTimersByTimeAsync(2999);
    expect(send).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    await next;
  });
});
