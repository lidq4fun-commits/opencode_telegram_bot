import { config } from "../config.js";
import { t } from "../i18n/index.js";

/** Only provider credential failures, never transport failures or service Basic auth. */
export function isProviderLoginRequired(error: unknown): boolean {
  if (!error) return false;
  if (typeof error === "string") {
    return /(?:needs[_ ]auth|ProviderAuthError|not (?:logged|signed) in|(?:api[ _-]?key|credentials?|access token|refresh token).{0,60}(?:missing|required|not (?:found|set|configured)|invalid|expired)|(?:missing|invalid|expired).{0,40}(?:api[ _-]?key|credentials?|token)|(?:please|must|need to).{0,30}(?:log|sign) in)/i.test(
      error,
    );
  }
  if (typeof error !== "object") return false;
  const value = error as Record<string, unknown>;
  if (value._tag === "UnauthorizedError" || value.name === "UnauthorizedError") return false;
  if (typeof value.type === "string" && value.status === 401) return true;
  return (
    value.type === "ProviderAuthError" ||
    value.name === "ProviderAuthError" ||
    value._tag === "ProviderAuthError" ||
    value.status === "needs_auth" ||
    isProviderLoginRequired(value.message) ||
    isProviderLoginRequired(value.data)
  );
}

export function providerLoginReminder(): string {
  let address = "";
  try {
    const url = new URL(config.opencode.apiUrl);
    url.username = "";
    url.password = "";
    url.search = "";
    url.hash = "";
    address = url.toString();
  } catch {
    // The desktop's embedded web view remains available without a printable URL.
  }
  return t("bot.provider_login_required", { url: address });
}

export function promptFailureMessage(error: unknown): string {
  return isProviderLoginRequired(error) ? providerLoginReminder() : t("bot.prompt_send_error");
}
