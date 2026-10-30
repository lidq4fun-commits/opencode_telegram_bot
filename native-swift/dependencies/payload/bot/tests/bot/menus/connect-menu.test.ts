import { describe, expect, it } from "vitest";
import {
  buildConnectListView,
  buildConnectMethodView,
  buildConnectionMessage,
  parseConnectCallback,
} from "../../../src/bot/menus/connect-menu.js";
import type { ConnectIntegration } from "../../../src/app/services/provider-connection-service.js";

const integrations: ConnectIntegration[] = [
  {
    id: "opencode",
    name: "OpenCode Console",
    connected: true,
    methods: [{ id: "device", type: "oauth", label: "Login" }],
  },
  {
    id: "xai",
    name: "xAI",
    connected: false,
    methods: [{ id: "device", type: "oauth", label: "SuperGrok" }],
  },
];

type TestButton = { text?: string; callback_data?: string; url?: string };

function buttons(view: { keyboard: { inline_keyboard: unknown[][] } }): TestButton[] {
  return view.keyboard.inline_keyboard.flat() as TestButton[];
}

describe("connect menu", () => {
  it("parses every callback action", () => {
    expect(parseConnectCallback("connect:p:2")).toEqual({ action: "page", page: 2 });
    expect(parseConnectCallback("connect:i:opencode")).toEqual({
      action: "pick",
      integrationID: "opencode",
    });
    expect(parseConnectCallback("connect:go:opencode:device")).toEqual({
      action: "start",
      integrationID: "opencode",
      methodID: "device",
    });
    expect(parseConnectCallback("connect:code:con_1")).toEqual({
      action: "code",
      attemptID: "con_1",
    });
    expect(parseConnectCallback("connect:cancel:con_1")).toEqual({
      action: "cancel",
      attemptID: "con_1",
    });
    expect(parseConnectCallback("connect:unknown")).toBeNull();
  });

  it("marks connected providers and links each one", () => {
    const view = buildConnectListView(integrations, 0);
    const items = buttons(view);
    expect(items[0]?.text).toContain("✅");
    expect(items[0]?.callback_data).toBe("connect:i:opencode");
    expect(items.some((button) => button.text?.includes("xAI"))).toBe(true);
  });

  it("offers each login method", () => {
    const view = buildConnectMethodView(integrations[1]!);
    expect(buttons(view)[0]?.callback_data).toBe("connect:go:xai:device");
  });

  it("shows the login link, paste and cancel buttons", () => {
    const view = buildConnectionMessage("xAI", {
      attemptID: "con_1",
      url: "https://accounts.x.ai/oauth2/device?user_code=ABC",
      instructions: "Enter code: ABC",
      mode: "auto",
      time: { created: 1, expires: 2 },
    });

    const items = buttons(view);
    expect(items[0]).toMatchObject({
      text: expect.stringContaining("Open login page"),
      url: "https://accounts.x.ai/oauth2/device?user_code=ABC",
    });
    expect(items.some((button) => button.callback_data === "connect:code:con_1")).toBe(true);
    expect(items.some((button) => button.callback_data === "connect:cancel:con_1")).toBe(true);
    expect(view.text).toContain("Enter code: ABC");
  });

  it("warns about a loopback redirect for browser-only flows", () => {
    const view = buildConnectionMessage("DigitalOcean", {
      attemptID: "con_2",
      url: "https://cloud.digitalocean.com/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A1456%2Fauth%2Fcallback",
      instructions: "Complete authorization in your browser.",
      mode: "auto",
      time: { created: 1, expires: 2 },
    });

    expect(view.text).toContain("localhost");
  });
});
