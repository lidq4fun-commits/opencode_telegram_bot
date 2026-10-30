import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Context } from "grammy";
import { createIncomingPrompt } from "../../../src/app/types/prompt.js";
import { createTestAppContainer } from "../../helpers/app-container.js";

const mocks = vi.hoisted(() => ({
  mode: vi.fn(), admit: vi.fn(), current: vi.fn(), reconcile: vi.fn(), busy: vi.fn(),
}));
vi.mock("../../../src/app/stores/settings-store.js", () => ({ getPromptQueueMode: mocks.mode }));
vi.mock("../../../src/app/services/session-service.js", () => ({ getCurrentSession: mocks.current }));
vi.mock("../../../src/app/services/run-control-service.js", () => ({ isForegroundBusy: mocks.busy }));
vi.mock("../../../src/app/services/prompt-inbox-service.js", () => ({ reconcileInboxPrompts: mocks.reconcile, cancelInboxPrompt: vi.fn() }));
vi.mock("../../../src/bot/handlers/prompt.js", () => ({ admitPromptToInbox: mocks.admit, startInboxPromptRun: vi.fn(), clearPromptResponseMode: vi.fn() }));
import { promptQueue, MAX_QUEUED_PROMPTS } from "../../../src/app/managers/prompt-queue-manager.js";
import { initializePromptQueueDispatch, tryEnqueuePrompt, dispatchNextQueuedPrompt, __resetPromptQueueDispatchForTests, shouldSuggestPromptQueue } from "../../../src/bot/handlers/prompt-queue-dispatch.js";

describe("V2 inbox prompt dispatch", () => {
  beforeEach(() => {
    vi.clearAllMocks(); promptQueue.__resetForTests(); __resetPromptQueueDispatchForTests();
    mocks.mode.mockReturnValue("queue"); mocks.busy.mockReturnValue(true);
    mocks.current.mockReturnValue({ id: "session-1" });
    mocks.admit.mockResolvedValue({ sessionId: "session-1", inboxId: "inbox-1" });
    initializePromptQueueDispatch({ ...createTestAppContainer(), bot: {} as never });
  });
  const context = () => ({ chat: { id: 42 }, reply: vi.fn().mockResolvedValue(undefined) }) as unknown as Context;
  it("sends queue prompts to OpenCode and stores only an inbox mirror", async () => {
    await expect(tryEnqueuePrompt(context(), createIncomingPrompt("do this"))).resolves.toBe(true);
    expect(mocks.admit).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ text: "do this" }), expect.anything(), "queue");
    expect(promptQueue.list()).toEqual([expect.objectContaining({ inbox: { sessionId: "session-1", inboxId: "inbox-1", delivery: "queue" } })]);
  });
  it("supports steer delivery", async () => {
    mocks.mode.mockReturnValue("steer");
    await tryEnqueuePrompt(context(), createIncomingPrompt("do this"));
    expect(mocks.admit).toHaveBeenLastCalledWith(expect.anything(), expect.anything(), expect.anything(), "steer");
  });
  it("releases a failed admission reservation", async () => {
    mocks.admit.mockResolvedValue(null);
    await tryEnqueuePrompt(context(), createIncomingPrompt("do this"));
    expect(promptQueue.size()).toBe(0);
  });
  it("does not admit a prompt when the queue is full", async () => {
    for (let index=0;index<MAX_QUEUED_PROMPTS;index++)promptQueue.reserve();
    await tryEnqueuePrompt(context(), createIncomingPrompt("overflow"));
    expect(mocks.admit).not.toHaveBeenCalled();
  });
  it("reconciles the OpenCode inbox instead of locally replaying prompts", async () => {
    await dispatchNextQueuedPrompt();
    expect(mocks.reconcile).toHaveBeenCalledWith("session-1");
  });
  it("ignores commands and disabled queues", async () => {
    expect(await tryEnqueuePrompt(context(), createIncomingPrompt("/status"))).toBe(false);
    mocks.mode.mockReturnValue("off");
    expect(await tryEnqueuePrompt(context(), createIncomingPrompt("do this"))).toBe(false);
    expect(shouldSuggestPromptQueue(createIncomingPrompt("do this"))).toBe(true);
    expect(mocks.admit).not.toHaveBeenCalled();
  });
});
