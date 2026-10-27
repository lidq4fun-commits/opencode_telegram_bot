import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Context } from "grammy";
import type { AppContainer } from "../../../src/app/bootstrap/app-container.js";
import { createTestAppContainer } from "../../helpers/app-container.js";

const mocks = vi.hoisted(() => ({
  list: vi.fn(),
  start: vi.fn(),
  complete: vi.fn(),
  connectKey: vi.fn(),
  watch: vi.fn(),
  getWatched: vi.fn(),
  cancelWatched: vi.fn(),
}));

vi.mock("../../../src/app/services/provider-connection-service.js", () => ({
  listConnectableIntegrations: mocks.list,
  startOauthConnection: mocks.start,
  completeOauthConnection: mocks.complete,
  connectProviderKey: mocks.connectKey,
}));

vi.mock("../../../src/bot/services/provider-connect-runtime.js", () => ({
  watchConnection: mocks.watch,
  getWatchedConnection: mocks.getWatched,
  cancelWatchedConnection: mocks.cancelWatched,
}));

import {
  handleConnectCallback,
  handleConnectKeyTextAnswer,
  handleConnectSearchTextAnswer,
} from "../../../src/bot/callbacks/connect-callback-handler.js";

const integration = {
  id: "opencode",
  name: "OpenCode Console",
  connected: false,
  methods: [{ id: "device", type: "oauth" as const, label: "Login with OpenCode" }],
};

const keyIntegration = {
  id: "zai-coding-plan",
  name: "Z.ai Coding Plan",
  connected: false,
  methods: [{ id: "key", type: "key" as const, label: "API key" }],
};

const attempt = {
  attemptID: "con_1",
  url: "https://opencode.ai/console/device?user_code=ABC",
  instructions: "Enter code: ABC",
  mode: "auto" as const,
  time: { created: 1, expires: 2 },
};

function createContext(data: string): Context {
  return {
    chat: { id: 1 },
    callbackQuery: { data, message: { message_id: 10 }, from: { id: 1 } },
    answerCallbackQuery: vi.fn().mockResolvedValue(undefined),
    editMessageText: vi.fn().mockResolvedValue(undefined),
    deleteMessage: vi.fn().mockResolvedValue(undefined),
    reply: vi.fn().mockResolvedValue({ message_id: 20 }),
  } as unknown as Context;
}

let container: AppContainer;

beforeEach(() => {
  container = createTestAppContainer();
  mocks.list.mockReset().mockResolvedValue([integration]);
  mocks.start.mockReset().mockResolvedValue(attempt);
  mocks.watch.mockReset();
  mocks.getWatched.mockReset();
  mocks.cancelWatched.mockReset();
  container.interactionManager.start({
    kind: "inline",
    expectedInput: "callback",
    metadata: { menuKind: "connect", messageId: 10 },
  });
});

describe("provider connect callbacks", () => {
  it("starts the login and sends the link with paste/cancel buttons", async () => {
    const ctx = createContext("connect:go:opencode:device");

    const handled = await handleConnectCallback(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.start).toHaveBeenCalledWith(integration, integration.methods[0]);
    expect(ctx.reply).toHaveBeenCalledWith(
      expect.stringContaining("Enter code: ABC"),
      expect.objectContaining({ reply_markup: expect.anything() }),
    );
    expect(mocks.watch).toHaveBeenCalledWith(
      expect.objectContaining({
        chatId: 1,
        messageId: 20,
        integrationID: "opencode",
        attempt,
      }),
    );
    expect(ctx.deleteMessage).toHaveBeenCalled();
    expect(container.interactionManager.getSnapshot()).toBeNull();
  });

  it("shows login methods for a provider", async () => {
    const ctx = createContext("connect:i:opencode");

    await handleConnectCallback(ctx, container);

    expect(ctx.editMessageText).toHaveBeenCalledWith(
      expect.stringContaining("OpenCode Console"),
      expect.objectContaining({ reply_markup: expect.anything() }),
    );
  });

  it("ignores a provider that disappeared from the list", async () => {
    const ctx = createContext("connect:i:missing");

    await handleConnectCallback(ctx, container);

    expect(ctx.editMessageText).not.toHaveBeenCalled();
    expect(ctx.answerCallbackQuery).toHaveBeenCalledWith(
      expect.objectContaining({ show_alert: true }),
    );
  });

  it("asks for an API key instead of starting OAuth", async () => {
    mocks.list.mockResolvedValue([keyIntegration]);
    const ctx = createContext("connect:go:zai-coding-plan:key");

    const handled = await handleConnectCallback(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.start).not.toHaveBeenCalled();
    expect(ctx.reply).toHaveBeenCalledWith(
      expect.stringContaining("API key"),
      expect.objectContaining({ reply_markup: expect.anything() }),
    );
    expect(container.interactionManager.getSnapshot()).toMatchObject({
      kind: "custom",
      expectedInput: "text",
      metadata: expect.objectContaining({
        flow: "provider_connect_key",
        integrationID: "zai-coding-plan",
        methodID: "key",
      }),
    });
  });

  it("saves the API key, deletes both messages and confirms", async () => {
    mocks.list.mockResolvedValue([keyIntegration]);
    mocks.connectKey.mockResolvedValue(undefined);

    const ctx = {
      chat: { id: 1 },
      message: { text: " sk-test-key " },
      api: { deleteMessage: vi.fn().mockResolvedValue(undefined) },
      reply: vi.fn().mockResolvedValue({ message_id: 2 }),
      deleteMessage: vi.fn().mockResolvedValue(undefined),
    } as unknown as Context;

    container.interactionManager.start({
      kind: "custom",
      expectedInput: "text",
      metadata: {
        flow: "provider_connect_key",
        integrationID: "zai-coding-plan",
        methodID: "key",
        messageId: 55,
      },
    });

    const handled = await handleConnectKeyTextAnswer(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.connectKey).toHaveBeenCalledWith(keyIntegration, keyIntegration.methods[0], "sk-test-key");
    expect(ctx.deleteMessage).toHaveBeenCalled();
    expect(ctx.api.deleteMessage).toHaveBeenCalledWith(1, 55);
    expect(container.interactionManager.getSnapshot()).toBeNull();
    expect(ctx.reply).toHaveBeenCalledWith(
      expect.stringContaining("Connected: Z.ai Coding Plan"),
    );
  });

  it("filters providers by provider name as well as integration name", async () => {
    mocks.list.mockResolvedValue([
      { ...integration, providers: ["OpenCode Go"] },
      keyIntegration,
    ]);
    const ctx = createContext("connect:search");
    await handleConnectCallback(ctx, container);

    const searchCtx = {
      chat: { id: 1 },
      message: { text: "go" },
      api: { deleteMessage: vi.fn().mockResolvedValue(undefined) },
      reply: vi.fn().mockResolvedValue({ message_id: 30 }),
    } as unknown as Context;

    await handleConnectSearchTextAnswer(searchCtx, container);

    const replyCall = (searchCtx.reply as ReturnType<typeof vi.fn>).mock.calls[0];
    const labels = (
      replyCall?.[1]?.reply_markup?.inline_keyboard as Array<Array<{ text?: string }>> | undefined
    )
      ?.flat()
      .map((button) => button.text);
    expect(labels?.some((label) => label?.includes("OpenCode Console"))).toBe(true);
  });

  it("filters providers by name after a search", async () => {
    mocks.list.mockResolvedValue([integration, keyIntegration]);
    const ctx = createContext("connect:search");

    await handleConnectCallback(ctx, container);
    expect(container.interactionManager.getSnapshot()).toMatchObject({
      kind: "custom",
      metadata: expect.objectContaining({ flow: "provider_connect_search" }),
    });

    const searchCtx = {
      chat: { id: 1 },
      message: { text: "zai" },
      api: { deleteMessage: vi.fn().mockResolvedValue(undefined) },
      reply: vi.fn().mockResolvedValue({ message_id: 30 }),
    } as unknown as Context;

    const handled = await handleConnectSearchTextAnswer(searchCtx, container);

    expect(handled).toBe(true);
    expect(searchCtx.reply).toHaveBeenCalledWith(
      expect.stringContaining("zai"),
      expect.objectContaining({ reply_markup: expect.anything() }),
    );
    expect(container.interactionManager.getSnapshot()).toMatchObject({
      kind: "inline",
      metadata: expect.objectContaining({ menuKind: "connect" }),
    });
  });
});
