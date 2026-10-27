import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Context } from "grammy";
import type { AppContainer } from "../../../src/app/bootstrap/app-container.js";
import { createTestAppContainer } from "../../helpers/app-container.js";

const mocks = vi.hoisted(() => ({
  complete: vi.fn(),
}));

vi.mock("../../../src/app/services/provider-connection-service.js", () => ({
  completeOauthConnection: mocks.complete,
  listConnectableIntegrations: vi.fn(),
  startOauthConnection: vi.fn(),
}));

import { handleConnectCodeTextAnswer } from "../../../src/bot/callbacks/connect-callback-handler.js";

function createContext(text: string): Context {
  return {
    chat: { id: 1 },
    message: { text },
    api: { deleteMessage: vi.fn().mockResolvedValue(undefined) },
    reply: vi.fn().mockResolvedValue({ message_id: 2 }),
  } as unknown as Context;
}

let container: AppContainer;

beforeEach(() => {
  container = createTestAppContainer();
  mocks.complete.mockReset().mockResolvedValue(undefined);
});

function startCodePrompt(): void {
  container.interactionManager.start({
    kind: "custom",
    expectedInput: "text",
    metadata: {
      flow: "provider_connect_code",
      integrationID: "opencode",
      attemptID: "con_1",
      messageId: 55,
    },
  });
}

describe("provider connect code answer", () => {
  it("extracts the code from the full redirect URL", async () => {
    startCodePrompt();
    const ctx = createContext("http://127.0.0.1:1456/auth/callback?code=abc123&state=xyz");

    const handled = await handleConnectCodeTextAnswer(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.complete).toHaveBeenCalledWith("opencode", "con_1", "abc123");
    expect(container.interactionManager.getSnapshot()).toBeNull();
    expect(ctx.api.deleteMessage).toHaveBeenCalledWith(1, 55);
  });

  it("submits a pasted raw code", async () => {
    startCodePrompt();
    const ctx = createContext("  ABC-123  ");

    await handleConnectCodeTextAnswer(ctx, container);

    expect(mocks.complete).toHaveBeenCalledWith("opencode", "con_1", "ABC-123");
  });

  it("asks for a code when the message is empty", async () => {
    startCodePrompt();
    const ctx = createContext("   ");

    const handled = await handleConnectCodeTextAnswer(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.complete).not.toHaveBeenCalled();
    expect(container.interactionManager.getSnapshot()?.kind).toBe("custom");
  });

  it("keeps the prompt active when the server rejects the code", async () => {
    startCodePrompt();
    mocks.complete.mockRejectedValue(new Error("invalid code"));
    const ctx = createContext("bad-code");

    const handled = await handleConnectCodeTextAnswer(ctx, container);

    expect(handled).toBe(true);
    expect(container.interactionManager.getSnapshot()?.kind).toBe("custom");
  });
});
