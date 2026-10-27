import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Api } from "grammy";

const mocks = vi.hoisted(() => ({
  status: vi.fn(),
  cancel: vi.fn(),
}));

vi.mock("../../../src/app/services/provider-connection-service.js", () => ({
  readOauthStatus: mocks.status,
  cancelOauthConnection: mocks.cancel,
}));

import {
  cancelWatchedConnection,
  pollConnection,
  watchConnection,
} from "../../../src/bot/services/provider-connect-runtime.js";
import { getWatchedConnection } from "../../../src/bot/services/provider-connect-runtime.js";

const attempt = {
  attemptID: "con_1",
  url: "https://example.com/device?user_code=ABC",
  instructions: "Enter code: ABC",
  mode: "auto" as const,
  time: { created: Date.now(), expires: Date.now() + 60_000 },
};

function createApi() {
  return { editMessageText: vi.fn().mockResolvedValue(undefined) } as unknown as Api;
}

function watch(api: Api, expires = Date.now() + 60_000) {
  return watchConnection(
    {
      api,
      chatId: 1,
      messageId: 2,
      integrationID: "opencode",
      integrationName: "OpenCode Console",
      attempt: { ...attempt, time: { created: Date.now(), expires } },
    },
    10 * 60_000,
  );
}

beforeEach(() => {
  mocks.status.mockReset().mockResolvedValue({ status: "pending", time: attempt.time });
  mocks.cancel.mockReset().mockResolvedValue(undefined);
});

describe("provider connect runtime", () => {
  it("reports a completed login and stops watching", async () => {
    const api = createApi();
    watch(api);
    mocks.status.mockResolvedValueOnce({ status: "complete", time: attempt.time });

    await pollConnection("con_1");

    expect(api.editMessageText).toHaveBeenCalledWith(
      1,
      2,
      expect.stringContaining("Connected: OpenCode Console"),
      expect.objectContaining({ reply_markup: { inline_keyboard: [] } }),
    );
    expect(getWatchedConnection("con_1")).toBeUndefined();
  });

  it("reports a failed login with the provider message", async () => {
    const api = createApi();
    watch(api);
    mocks.status.mockResolvedValueOnce({
      status: "failed",
      message: "access denied",
      time: attempt.time,
    });

    await pollConnection("con_1");

    expect(api.editMessageText).toHaveBeenCalledWith(
      1,
      2,
      expect.stringContaining("access denied"),
      expect.anything(),
    );
  });

  it("expires a pending attempt that ran out of time", async () => {
    const api = createApi();
    watch(api, Date.now() - 1);

    await pollConnection("con_1");

    expect(api.editMessageText).toHaveBeenCalledWith(
      1,
      2,
      expect.stringContaining("expired"),
      expect.anything(),
    );
  });

  it("cancels the provider attempt and updates the message", async () => {
    const api = createApi();
    watch(api);

    await expect(cancelWatchedConnection("con_1")).resolves.toBe(true);

    expect(mocks.cancel).toHaveBeenCalledWith("opencode", "con_1");
    expect(api.editMessageText).toHaveBeenCalledWith(
      1,
      2,
      expect.stringContaining("Cancelled"),
      expect.anything(),
    );
    expect(getWatchedConnection("con_1")).toBeUndefined();
  });

  it("returns false for a finished attempt", async () => {
    await expect(cancelWatchedConnection("missing")).resolves.toBe(false);
  });
});
