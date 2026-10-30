import { InlineKeyboard } from "grammy";
import type { IntegrationAttempt } from "@opencode/client";
import type { ConnectIntegration } from "../../app/services/provider-connection-service.js";
import { t } from "../../i18n/index.js";

export const CONNECT_CALLBACK_PREFIX = "connect:";
const PAGE_SIZE = 8;
const MAX_LABEL_LENGTH = 64;

export type ConnectCallback =
  | { action: "page"; page: number }
  | { action: "pick"; integrationID: string }
  | { action: "start"; integrationID: string; methodID: string }
  | { action: "code"; attemptID: string }
  | { action: "cancel"; attemptID: string }
  | { action: "cancel_input" }
  | { action: "search" }
  | { action: "all" };

export function parseConnectCallback(data: string): ConnectCallback | null {
  if (!data.startsWith(CONNECT_CALLBACK_PREFIX)) {
    return null;
  }

  const parts = data.split(":");
  const action = parts[1];

  if (action === "search") {
    return { action: "search" };
  }

  if (action === "all") {
    return { action: "all" };
  }

  if (action === "cancel_input") {
    return { action: "cancel_input" };
  }

  if (action === "p" && parts[2]) {
    const page = Number.parseInt(parts[2], 10);
    return Number.isInteger(page) && page >= 0 ? { action: "page", page } : null;
  }

  if (action === "i" && parts[2]) {
    return { action: "pick", integrationID: parts[2] };
  }

  if (action === "go" && parts[2] && parts[3]) {
    return { action: "start", integrationID: parts[2], methodID: parts[3] };
  }

  if (action === "code" && parts[2]) {
    return { action: "code", attemptID: parts[2] };
  }

  if (action === "cancel" && parts[2]) {
    return { action: "cancel", attemptID: parts[2] };
  }

  return null;
}

function truncate(label: string): string {
  return label.length <= MAX_LABEL_LENGTH ? label : `${label.slice(0, MAX_LABEL_LENGTH - 3)}...`;
}

export function buildConnectListView(
  integrations: ConnectIntegration[],
  page: number,
  query?: string,
): { text: string; keyboard: InlineKeyboard } {
  const totalPages = Math.max(1, Math.ceil(integrations.length / PAGE_SIZE));
  const normalizedPage = Math.min(Math.max(0, page), totalPages - 1);
  const startIndex = normalizedPage * PAGE_SIZE;
  const keyboard = new InlineKeyboard();

  integrations.slice(startIndex, startIndex + PAGE_SIZE).forEach((integration) => {
    const badge = integration.connected ? "✅ " : "";
    keyboard
      .text(
        truncate(`${badge}${integration.name}`),
        `${CONNECT_CALLBACK_PREFIX}i:${integration.id}`,
      )
      .row();
  });

  if (totalPages > 1) {
    if (normalizedPage > 0) {
      keyboard.text(t("projects.prev_page"), `${CONNECT_CALLBACK_PREFIX}p:${normalizedPage - 1}`);
    }
    if (normalizedPage < totalPages - 1) {
      keyboard.text(t("projects.next_page"), `${CONNECT_CALLBACK_PREFIX}p:${normalizedPage + 1}`);
    }
    keyboard.row();
  }

  keyboard.text(t("connect.search_button"), `${CONNECT_CALLBACK_PREFIX}search`);
  if (query) {
    keyboard.text(t("connect.all"), `${CONNECT_CALLBACK_PREFIX}all`);
  }
  keyboard.row();

  const text = query
    ? t("connect.search_results", { query, count: String(integrations.length) })
    : t("connect.select");
  const pageInfo =
    totalPages > 1
      ? `\n\n${t("projects.page_indicator", {
          current: String(normalizedPage + 1),
          total: String(totalPages),
        })}`
      : "";
  const hint = query ? "" : `\n\n${t("connect.search_hint")}`;

  return { text: `${text}${pageInfo}${hint}`, keyboard };
}

export function buildConnectMethodView(integration: ConnectIntegration): {
  text: string;
  keyboard: InlineKeyboard;
} {
  const keyboard = new InlineKeyboard();

  for (const method of integration.methods) {
    const label =
      method.type === "key" && !method.label ? t("connect.key_method") : method.label;
    const prefix = method.type === "key" ? "🔑 " : method.mobile ? "📱 " : "🖥️ ";
    keyboard
      .text(
        truncate(`${prefix}${label}`),
        `${CONNECT_CALLBACK_PREFIX}go:${integration.id}:${method.id}`,
      )
      .row();
  }

  keyboard.text(t("connect.back"), `${CONNECT_CALLBACK_PREFIX}p:0`);

  const usedBy =
    integration.providers && integration.providers.length > 0
      ? `\n\n${t("connect.used_by", { providers: integration.providers.join(", ") })}`
      : "";
  const flowHint =
    integration.methods.some((method) => method.type === "oauth" && method.mobile)
      ? `\n\n${t("connect.methods_hint")}`
      : integration.methods.some((method) => method.type === "oauth")
        ? `\n\n${t("connect.pc_only_hint")}`
        : "";

  return {
    text: `${t("connect.methods", { provider: integration.name })}${usedBy}${flowHint}`,
    keyboard,
  };
}

function isLoopbackRedirect(url: string): boolean {
  try {
    const redirect = new URL(url).searchParams.get("redirect_uri");
    return Boolean(redirect && /^(https?:\/\/)?(localhost|127\.0\.0\.1)([:/]|$)/i.test(redirect));
  } catch {
    return false;
  }
}

export function buildConnectionMessage(
  integrationName: string,
  attempt: IntegrationAttempt,
): { text: string; keyboard: InlineKeyboard } {
  const baseText = t("connect.started", {
    provider: integrationName,
    instructions: attempt.instructions || "",
  }).trim();
  const text = isLoopbackRedirect(attempt.url)
    ? `${baseText}\n\n${t("connect.loopback_hint")}`
    : baseText;

  const keyboard = new InlineKeyboard()
    .url(t("connect.open"), attempt.url)
    .row()
    .text(t("connect.paste"), `${CONNECT_CALLBACK_PREFIX}code:${attempt.attemptID}`)
    .text(t("connect.cancel"), `${CONNECT_CALLBACK_PREFIX}cancel:${attempt.attemptID}`);

  return { text, keyboard };
}
