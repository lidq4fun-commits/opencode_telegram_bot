import { beforeEach, describe, expect, it, vi } from "vitest";

const mocked = vi.hoisted(() => ({ status: vi.fn() }));
vi.mock("../../../src/opencode/client.js", () => ({
  opencodeV2Client: { provider: { connectionStatus: mocked.status } },
  opencodeClient: {},
}));
vi.mock("../../../src/app/services/model-selection-service.js", () => ({
  getStoredModel: () => ({ providerID: "openai", modelID: "gpt-5" }),
}));
vi.mock("../../../src/app/stores/settings-store.js", () => ({
  getCurrentProject: () => ({ worktree: "/repo" }),
}));
import {
  needsProviderLogin,
  notifyProviderLoginIfNeeded,
} from "../../../src/app/services/provider-login-service.js";

describe("startup provider login notification", () => {
  beforeEach(() => mocked.status.mockResolvedValue({ data: "ready" }));
  it("notifies the user when the selected provider is not logged in", async () => {
    mocked.status.mockResolvedValue({ data: "needs_auth" });
    const send = vi.fn().mockResolvedValue(undefined);
    await notifyProviderLoginIfNeeded(send);
    expect(send).toHaveBeenCalledOnce();
    expect(send).toHaveBeenCalledWith(expect.stringContaining("/connect"));
    expect(mocked.status).toHaveBeenCalledWith({
      providerID: "openai",
      modelID: "gpt-5",
      directory: "/repo",
    });
  });
  it("does not send a login warning when connected", async () => {
    const send = vi.fn();
    await notifyProviderLoginIfNeeded(send);
    expect(send).not.toHaveBeenCalled();
  });
  it.each([{ error: new Error("service unavailable") }, { data: "unknown" }])(
    "does not confuse an unavailable service with a missing account: %j",
    async (response) => {
      mocked.status.mockResolvedValue(response);
      expect(await needsProviderLogin({ providerID: "openai", modelID: "gpt-5" })).toBe(false);
    },
  );
});
