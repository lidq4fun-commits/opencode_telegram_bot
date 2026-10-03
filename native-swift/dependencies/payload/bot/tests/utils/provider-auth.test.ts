import { describe, expect, it, vi } from "vitest";
import {
  isProviderLoginRequired,
  promptFailureMessage,
  providerLoginReminder,
} from "../../src/utils/provider-auth.js";
import { config } from "../../src/config.js";
import { toV1ErrorPayload } from "../../src/opencode/v2/mappers.js";

describe("provider login reminders", () => {
  it.each([
    { type: "ProviderAuthError", message: "Credentials unavailable" },
    { name: "ProviderAuthError", data: { providerID: "openai" } },
    { type: "APICallError", status: 401, message: "Unauthorized" },
    { message: "API key is missing" },
    { message: "refresh token expired" },
    "You are not logged in",
  ])("recognizes missing or expired provider credentials: %j", (error) => {
    expect(isProviderLoginRequired(error)).toBe(true);
    expect(promptFailureMessage(error)).toContain("/connect");
  });
  it.each([
    new Error("Transport: fetch failed"),
    { _tag: "UnauthorizedError", message: "Invalid API key for server" },
    { type: "APICallError", status: 429, message: "Rate limit" },
    { type: "APICallError", status: 500, message: "Internal error" },
    "Insufficient credits",
  ])("does not mislabel service, quota or transport failures: %j", (error) => {
    expect(isProviderLoginRequired(error)).toBe(false);
    expect(promptFailureMessage(error)).not.toContain("/connect");
  });
  it("provides Chinese instructions without leaking service credentials", () => {
    vi.stubEnv("BOT_LOCALE", "zh");
    const previous = config.opencode.apiUrl;
    try {
      config.opencode.apiUrl = "http://user:secret@localhost:49374/?auth_token=private#secret";
      const message = providerLoginReminder();
      expect(message).toContain("网页端");
      expect(message).toContain("/connect");
      expect(message).not.toContain("secret");
      expect(message).not.toContain("private");
      expect(message).not.toContain("auth_token");
    } finally {
      config.opencode.apiUrl = previous;
    }
  });
  it("includes the login instructions in asynchronous V2 session errors", () => {
    expect(
      toV1ErrorPayload({ type: "APICallError", status: 401, message: "Unauthorized" }).data.message,
    ).toContain("/connect");
    expect(
      toV1ErrorPayload({ type: "APICallError", status: 429, message: "Rate limit" }).data.message,
    ).toBe("Rate limit");
  });
});
