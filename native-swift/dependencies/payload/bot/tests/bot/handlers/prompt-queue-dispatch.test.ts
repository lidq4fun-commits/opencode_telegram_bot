import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Context } from "grammy";
import { createIncomingPrompt } from "../../../src/app/types/prompt.js";
import { MAX_QUEUED_PROMPTS, promptQueue } from "../../../src/app/managers/prompt-queue-manager.js";
import { createTestAppContainer } from "../../helpers/app-container.js";

const mocked = vi.hoisted(() => ({
  admit: vi.fn(),
  startRun: vi.fn(),
  processPrompt: vi.fn(),
  mode: vi.fn(),
  busy: vi.fn(),
  reconcile: vi.fn(),
  cancel: vi.fn(),
  session: vi.fn(),
}));
vi.mock("../../../src/bot/handlers/prompt.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../../src/bot/handlers/prompt.js")>()),
  admitPromptToInbox: mocked.admit,
  startInboxPromptRun: mocked.startRun,
  processUserPrompt: mocked.processPrompt,
}));
vi.mock("../../../src/app/stores/settings-store.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../../src/app/stores/settings-store.js")>()),
  getPromptQueueMode: mocked.mode,
}));
vi.mock("../../../src/app/services/run-control-service.js", () => ({
  isForegroundBusy: mocked.busy,
}));
vi.mock("../../../src/app/services/session-service.js", () => ({
  getCurrentSession: mocked.session,
}));
vi.mock("../../../src/app/services/prompt-inbox-service.js", () => ({
  reconcileInboxPrompts: mocked.reconcile,
  cancelInboxPrompt: mocked.cancel,
}));

import {
  __resetPromptQueueDispatchForTests,
  dispatchNextQueuedPrompt,
  initializePromptQueueDispatch,
  shouldSuggestPromptQueue,
  tryEnqueuePrompt,
} from "../../../src/bot/handlers/prompt-queue-dispatch.js";

const keyboard = { keyboard: [] };
const deps = { ...createTestAppContainer(), bot: {} as never };
const reply = vi.fn();
const context = { chat: { id: 42 }, api: {}, reply } as unknown as Context;
const enqueue = (text: string) => tryEnqueuePrompt(context, createIncomingPrompt(text));

describe("V2 prompt inbox dispatch", () => {
  beforeEach(() => {
    promptQueue.__resetForTests();
    __resetPromptQueueDispatchForTests();
    mocked.mode.mockReturnValue("queue");
    mocked.busy.mockReturnValue(false);
    mocked.session.mockReturnValue({ id: "s1", directory: "/repo" });
    mocked.admit.mockImplementation(async () => ({
      sessionId: "s1",
      inboxId: `inbox-${mocked.admit.mock.calls.length}`,
    }));
    reply.mockResolvedValue(undefined);
    vi.spyOn(deps.keyboardManager, "getKeyboard").mockReturnValue(keyboard as never);
    initializePromptQueueDispatch(deps);
  });

  it("does nothing when queueing is disabled", async () => {
    mocked.mode.mockReturnValue("off");
    expect(await enqueue("work")).toBe(false);
    expect(mocked.admit).not.toHaveBeenCalled();
  });
  it("admits once and mirrors the display text with a refreshed keyboard", async () => {
    expect(await enqueue("work")).toBe(true);
    expect(mocked.admit).toHaveBeenCalledWith(context, createIncomingPrompt("work"), deps, "queue");
    expect(promptQueue.list()).toEqual([
      expect.objectContaining({
        displayText: "work",
        inbox: { sessionId: "s1", inboxId: "inbox-1", delivery: "queue" },
      }),
    ]);
    expect(reply).toHaveBeenCalledWith(expect.any(String), { reply_markup: keyboard });
  });
  it("uses steer delivery when selected", async () => {
    mocked.mode.mockReturnValue("steer");
    await enqueue("change direction");
    expect(mocked.admit).toHaveBeenCalledWith(context, expect.anything(), deps, "steer");
  });
  it("rejects overflow without contacting the server", async () => {
    for (let i = 0; i < MAX_QUEUED_PROMPTS; i++) await enqueue(`prompt ${i}`);
    mocked.admit.mockClear();
    await enqueue("overflow");
    expect(mocked.admit).not.toHaveBeenCalled();
    expect(promptQueue.size()).toBe(MAX_QUEUED_PROMPTS);
  });
  it.each([
    "/status",
    "   ",
    "🧠 openrouter\nopenai/gpt-4o",
    "🛠️ Build Agent",
    "📊 150K / 1.5M (10%)",
    "❌ 1. queued",
  ])("never queues commands or buttons: %s", async (text) => {
    expect(await enqueue(text)).toBe(false);
    expect(mocked.admit).not.toHaveBeenCalled();
  });
  it("sends media to the server but does not retain its bytes in the mirror", async () => {
    const input = createIncomingPrompt("inspect", {
      fileParts: [
        {
          type: "file",
          mime: "image/jpeg",
          filename: "photo.jpg",
          url: "data:image/jpeg;base64,cGhvdG8=",
        },
      ],
    });
    await tryEnqueuePrompt(context, input);
    expect(mocked.admit).toHaveBeenCalledWith(context, input, deps, "queue");
    expect(promptQueue.list()[0]?.fileParts).toEqual([]);
    expect(promptQueue.mediaSize()).toBe(0);
  });
  it("releases the reservation when admission fails", async () => {
    mocked.admit.mockResolvedValue(null);
    await enqueue("work");
    expect(promptQueue.size()).toBe(0);
    expect(promptQueue.isFull()).toBe(false);
  });
  it("withdraws an admission that completed after the queue was cleared", async () => {
    mocked.admit.mockImplementation(async () => {
      promptQueue.clear("test");
      return { sessionId: "s1", inboxId: "withdrawn" };
    });
    await enqueue("work");
    expect(mocked.cancel).toHaveBeenCalledWith(
      { sessionId: "s1", inboxId: "withdrawn", delivery: "queue" },
      "withdrawn_during_admission",
    );
    expect(promptQueue.size()).toBe(0);
  });
  it("counts concurrent admissions toward the cap", async () => {
    let complete: ((value: { sessionId: string; inboxId: string }) => void) | undefined;
    mocked.admit.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          complete = resolve;
        }),
    );
    const pending = enqueue("first");
    for (let i = 1; i < MAX_QUEUED_PROMPTS; i++) await enqueue(`prompt ${i}`);
    expect(promptQueue.isFull()).toBe(true);
    await enqueue("overflow");
    expect(mocked.admit).toHaveBeenCalledTimes(MAX_QUEUED_PROMPTS);
    complete?.({ sessionId: "s1", inboxId: "first" });
    await pending;
  });
  it("reconciles on idle without redispatching or echoing the prompt", async () => {
    await enqueue("first");
    await dispatchNextQueuedPrompt();
    expect(mocked.reconcile).toHaveBeenCalledWith("s1");
    expect(mocked.processPrompt).not.toHaveBeenCalled();
    expect(mocked.admit).toHaveBeenCalledTimes(1);
  });
  it("does not reconcile without a session", async () => {
    mocked.session.mockReturnValue(null);
    await dispatchNextQueuedPrompt();
    expect(mocked.reconcile).not.toHaveBeenCalled();
  });
  it("does nothing before the dispatcher is initialized", async () => {
    __resetPromptQueueDispatchForTests();
    expect(await enqueue("first")).toBe(false);
    await dispatchNextQueuedPrompt();
    expect(mocked.reconcile).not.toHaveBeenCalled();
  });
  it("suggests enabling the queue only for content while disabled", () => {
    mocked.mode.mockReturnValue("off");
    expect(shouldSuggestPromptQueue(createIncomingPrompt("work"))).toBe(true);
    expect(shouldSuggestPromptQueue(createIncomingPrompt("/status"))).toBe(false);
    mocked.mode.mockReturnValue("queue");
    expect(shouldSuggestPromptQueue(createIncomingPrompt("work"))).toBe(false);
  });
});
