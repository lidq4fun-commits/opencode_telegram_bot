import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  list: vi.fn(),
  providers: vi.fn(),
  connect: vi.fn(),
  status: vi.fn(),
  complete: vi.fn(),
  cancel: vi.fn(),
}));

vi.mock("../../../src/opencode/client.js", () => ({
  opencodeV2Client: {
    integration: {
      list: mocks.list,
      listProviders: mocks.providers,
      connectOauth: mocks.connect,
      oauthStatus: mocks.status,
      oauthComplete: mocks.complete,
      oauthCancel: mocks.cancel,
    },
  },
}));

import {
  cancelOauthConnection,
  completeOauthConnection,
  listConnectableIntegrations,
  readOauthStatus,
  startOauthConnection,
} from "../../../src/app/services/provider-connection-service.js";

const attempt = {
  attemptID: "con_1",
  url: "https://example.com/device?user_code=ABC",
  instructions: "Enter code: ABC",
  mode: "auto" as const,
  time: { created: 1, expires: 2 },
};

beforeEach(() => {
  mocks.list.mockReset();
  mocks.providers.mockReset().mockResolvedValue({ data: [], error: undefined });
  mocks.connect.mockReset().mockResolvedValue({ data: attempt, error: undefined });
  mocks.status.mockReset().mockResolvedValue({ data: { status: "pending", time: attempt.time }, error: undefined });
  mocks.complete.mockReset().mockResolvedValue({ data: true, error: undefined });
  mocks.cancel.mockReset().mockResolvedValue({ data: true, error: undefined });
});

describe("provider connection service", () => {
  it("lists integrations with OAuth or API-key login", async () => {
    mocks.list.mockResolvedValue({
      data: [
        {
          id: "opencode",
          name: "OpenCode Console",
          methods: [
            {
              id: "device",
              type: "oauth",
              label: "OpenCode Console account",
              form: [{ key: "server", hidden: true, default: "https://opencode.ai/console" }],
            },
          ],
          connections: [],
        },
        {
          id: "api-only",
          name: "API Only",
          methods: [{ type: "key" }],
          connections: [],
        },
        {
          id: "env-only",
          name: "Env Only",
          methods: [{ type: "env", names: ["ENV_ONLY_KEY"] }],
          connections: [],
        },
      ],
      error: undefined,
    });

    mocks.providers.mockResolvedValue({
      data: [{ id: "opencode-go", name: "OpenCode Go", integrationID: "opencode" }],
      error: undefined,
    });

    const integrations = await listConnectableIntegrations();

    expect(integrations).toHaveLength(2);

    const opencode = integrations.find((item) => item.id === "opencode");
    expect(opencode).toMatchObject({
      name: "OpenCode Console",
      connected: false,
      providers: ["OpenCode Go"],
      methods: [
        {
          id: "device",
          type: "oauth",
          label: "OpenCode Console account",
          answer: { server: "https://opencode.ai/console" },
        },
      ],
    });

    const apiOnly = integrations.find((item) => item.id === "api-only");
    expect(apiOnly?.methods).toEqual([{ id: "key", type: "key", label: "" }]);
  });

  it("defaults required choices and skips unsupported free-text forms", async () => {
    mocks.list.mockResolvedValue({
      data: [
        {
          id: "github-copilot",
          name: "GitHub Copilot",
          methods: [
            {
              id: "device",
              type: "oauth",
              label: "Login with GitHub Copilot",
              form: [
                {
                  key: "deploymentType",
                  required: true,
                  type: "string",
                  options: [
                    { value: "github.com", label: "GitHub.com" },
                    { value: "enterprise", label: "GitHub Enterprise" },
                  ],
                },
                {
                  key: "enterpriseUrl",
                  required: true,
                  type: "string",
                  when: [{ key: "deploymentType", op: "eq", value: "enterprise" }],
                },
              ],
            },
          ],
          connections: [{ type: "credential", id: "c1", label: "me", method: "oauth" }],
        },
        {
          id: "custom",
          name: "Custom",
          methods: [
            {
              id: "browser",
              type: "oauth",
              label: "Login",
              form: [{ key: "account", required: true, type: "string" }],
            },
          ],
          connections: [],
        },
      ],
      error: undefined,
    });

    const integrations = await listConnectableIntegrations();

    expect(integrations).toHaveLength(1);
    expect(integrations[0]?.connected).toBe(true);
    expect(integrations[0]?.methods[0]?.answer).toEqual({ deploymentType: "github.com" });
  });

  it("prioritizes device-code methods and marks browser-only flows", async () => {
    mocks.list.mockResolvedValue({
      data: [
        {
          id: "openai",
          name: "OpenAI",
          methods: [
            { id: "chatgpt-token-sharing", type: "oauth", label: "Sign in with ChatGPT" },
            { id: "chatgpt-browser", type: "oauth", label: "Codex browser (legacy)" },
            { id: "chatgpt-headless", type: "oauth", label: "Codex device code (legacy)" },
          ],
          connections: [],
        },
      ],
      error: undefined,
    });

    const integrations = await listConnectableIntegrations();
    const openai = integrations.find((item) => item.id === "openai");

    expect(openai?.methods.map((method) => [method.id, method.mobile])).toEqual([
      ["chatgpt-headless", true],
      ["chatgpt-token-sharing", false],
      ["chatgpt-browser", false],
    ]);
  });

  it("starts, reads, completes and cancels an OAuth attempt", async () => {
    const integration = {
      id: "opencode",
      name: "OpenCode Console",
      connected: false,
      methods: [
        {
          id: "device",
          type: "oauth" as const,
          label: "Login",
          answer: { server: "https://opencode.ai/console" },
        },
      ],
    };

    await expect(startOauthConnection(integration, integration.methods[0]!)).resolves.toEqual(attempt);
    expect(mocks.connect).toHaveBeenCalledWith({
      integrationID: "opencode",
      methodID: "device",
      answer: { server: "https://opencode.ai/console" },
    });

    await readOauthStatus("opencode", "con_1");
    expect(mocks.status).toHaveBeenCalledWith({ integrationID: "opencode", attemptID: "con_1" });

    await completeOauthConnection("opencode", "con_1", "123456");
    expect(mocks.complete).toHaveBeenCalledWith({
      integrationID: "opencode",
      attemptID: "con_1",
      code: "123456",
    });

    await cancelOauthConnection("opencode", "con_1");
    expect(mocks.cancel).toHaveBeenCalledWith({ integrationID: "opencode", attemptID: "con_1" });
  });

  it("raises the server error when the integration list fails", async () => {
    mocks.list.mockResolvedValue({ data: undefined, error: new Error("offline") });
    await expect(listConnectableIntegrations()).rejects.toThrow("offline");
  });
});
