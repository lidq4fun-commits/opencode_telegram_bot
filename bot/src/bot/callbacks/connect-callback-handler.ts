import { InlineKeyboard, type Context } from "grammy";
import type { AppContainer } from "../../app/bootstrap/app-container.js";
import {
  completeOauthConnection,
  connectProviderKey,
  listConnectableIntegrations,
  startOauthConnection,
  type ConnectIntegration,
  type ConnectMethod,
} from "../../app/services/provider-connection-service.js";
import { t } from "../../i18n/index.js";
import { logger } from "../../utils/logger.js";
import {
  buildConnectListView,
  buildConnectMethodView,
  buildConnectionMessage,
  parseConnectCallback,
  CONNECT_CALLBACK_PREFIX,
} from "../menus/connect-menu.js";
import {
  clearActiveInlineMenu,
  ensureActiveInlineMenu,
  replyWithInlineMenu,
} from "../menus/inline-menu.js";
import {
  cancelWatchedConnection,
  getWatchedConnection,
  watchConnection,
} from "../services/provider-connect-runtime.js";
import { alert, failure, notify } from "./feedback.js";

const CODE_PROMPT_FLOW = "provider_connect_code";
const KEY_PROMPT_FLOW = "provider_connect_key";
const SEARCH_PROMPT_FLOW = "provider_connect_search";

function codePromptMessageId(deps: AppContainer, attemptID: string): number | null {
  const state = deps.interactionManager.getSnapshot();
  if (
    state?.kind !== "custom" ||
    state.metadata.flow !== CODE_PROMPT_FLOW ||
    state.metadata.attemptID !== attemptID ||
    typeof state.metadata.messageId !== "number"
  ) {
    return null;
  }
  return state.metadata.messageId;
}

function extractOauthCode(raw: string): { code?: string; error?: string } {
  const value = raw.trim();
  if (!value) {
    return {};
  }

  try {
    const url = new URL(value);
    const error = url.searchParams.get("error_description") || url.searchParams.get("error");
    if (error) {
      return { error };
    }
    return { code: url.searchParams.get("code") || value };
  } catch {
    return { code: value };
  }
}

async function editMenu(ctx: Context, text: string, keyboard: InlineKeyboard): Promise<void> {
  await ctx.answerCallbackQuery();
  await ctx.editMessageText(text, { reply_markup: keyboard });
}

async function startConnection(
  ctx: Context,
  deps: AppContainer,
  integrationID: string,
  methodID: string,
): Promise<void> {
  const integrations = await listConnectableIntegrations();
  const integration = integrations.find((item) => item.id === integrationID);
  const method = integration?.methods.find((item) => item.id === methodID);

  if (!integration || !method) {
    await alert(ctx, "connect.inactive");
    return;
  }

  if (method.type === "key") {
    await startKeyPrompt(ctx, deps, integration, method);
    return;
  }

  const attempt = await startOauthConnection(integration, method);
  const view = buildConnectionMessage(integration.name, attempt);
  const message = await ctx.reply(view.text, { reply_markup: view.keyboard });

  if (!ctx.chat) {
    await ctx.answerCallbackQuery();
    return;
  }

  watchConnection({
    api: ctx.api,
    chatId: ctx.chat.id,
    messageId: message.message_id,
    integrationID: integration.id,
    integrationName: integration.name,
    attempt,
  });

  await ctx.answerCallbackQuery();
  await ctx.deleteMessage().catch(() => {});
  clearActiveInlineMenu("connect_started", deps);

  logger.info(`[ProviderConnect] Login started: ${integration.id}/${method.id}`);
}

async function startKeyPrompt(
  ctx: Context,
  deps: AppContainer,
  integration: ConnectIntegration,
  method: ConnectMethod,
): Promise<void> {
  const prompt = await ctx.reply(t("connect.key_prompt", { provider: integration.name }), {
    reply_markup: new InlineKeyboard().text(
      t("connect.cancel"),
      `${CONNECT_CALLBACK_PREFIX}cancel_input`,
    ),
  });

  await ctx.answerCallbackQuery();
  await ctx.deleteMessage().catch(() => {});
  clearActiveInlineMenu("connect_key_prompt", deps);

  deps.interactionManager.start({
    kind: "custom",
    expectedInput: "text",
    metadata: {
      flow: KEY_PROMPT_FLOW,
      integrationID: integration.id,
      methodID: method.id,
      answer: method.answer,
      messageId: prompt.message_id,
    },
  });
}

async function startSearchPrompt(ctx: Context, deps: AppContainer): Promise<void> {
  const prompt = await ctx.reply(t("connect.search_prompt"), {
    reply_markup: new InlineKeyboard().text(
      t("connect.cancel"),
      `${CONNECT_CALLBACK_PREFIX}cancel_input`,
    ),
  });

  await ctx.answerCallbackQuery();
  await ctx.deleteMessage().catch(() => {});
  clearActiveInlineMenu("connect_search_prompt", deps);

  deps.interactionManager.start({
    kind: "custom",
    expectedInput: "text",
    metadata: {
      flow: SEARCH_PROMPT_FLOW,
      messageId: prompt.message_id,
    },
  });
}

async function cancelInput(ctx: Context, deps: AppContainer): Promise<void> {
  const state = deps.interactionManager.getSnapshot();
  const flow = state?.kind === "custom" ? state.metadata.flow : undefined;
  if (
    !state ||
    state.kind !== "custom" ||
    (flow !== KEY_PROMPT_FLOW && flow !== SEARCH_PROMPT_FLOW && flow !== CODE_PROMPT_FLOW)
  ) {
    await alert(ctx, "connect.inactive");
    return;
  }

  const messageId = typeof state.metadata.messageId === "number" ? state.metadata.messageId : null;
  deps.interactionManager.clear("connect_input_cancelled");
  if (messageId !== null && ctx.chat) {
    await ctx.api.deleteMessage(ctx.chat.id, messageId).catch(() => {});
  }

  await notify(ctx, "connect.cancelled");
}

async function startCodePrompt(
  ctx: Context,
  deps: AppContainer,
  attemptID: string,
): Promise<void> {
  const entry = getWatchedConnection(attemptID);
  if (!entry) {
    await alert(ctx, "connect.inactive");
    return;
  }

  const prompt = await ctx.reply(t("connect.code_prompt"), {
    reply_markup: new InlineKeyboard().text(
      t("connect.cancel"),
      `${CONNECT_CALLBACK_PREFIX}cancel:${attemptID}`,
    ),
  });

  await ctx.answerCallbackQuery();
  deps.interactionManager.start({
    kind: "custom",
    expectedInput: "text",
    metadata: {
      flow: CODE_PROMPT_FLOW,
      integrationID: entry.integrationID,
      attemptID,
      messageId: prompt.message_id,
    },
  });
}

async function cancelConnection(
  ctx: Context,
  deps: AppContainer,
  attemptID: string,
): Promise<void> {
  const promptMessageId = codePromptMessageId(deps, attemptID);
  if (promptMessageId !== null) {
    deps.interactionManager.clear("connect_cancelled");
    if (ctx.chat) {
      await ctx.api.deleteMessage(ctx.chat.id, promptMessageId).catch(() => {});
    }
  }

  const cancelled = await cancelWatchedConnection(attemptID);
  if (!cancelled) {
    await alert(ctx, "connect.inactive");
    return;
  }

  await notify(ctx, "connect.cancelled");
}

export async function handleConnectCallback(ctx: Context, deps: AppContainer): Promise<boolean> {
  const data = ctx.callbackQuery?.data;
  if (!data || !data.startsWith(CONNECT_CALLBACK_PREFIX)) {
    return false;
  }

  const payload = parseConnectCallback(data);
  if (!payload) {
    return false;
  }

  try {
    if (payload.action === "cancel") {
      await cancelConnection(ctx, deps, payload.attemptID);
      return true;
    }

    if (payload.action === "cancel_input") {
      await cancelInput(ctx, deps);
      return true;
    }

    if (payload.action === "code") {
      await startCodePrompt(ctx, deps, payload.attemptID);
      return true;
    }

    const isActiveMenu = await ensureActiveInlineMenu(ctx, "connect", deps);
    if (!isActiveMenu) {
      return true;
    }

    if (payload.action === "search") {
      await startSearchPrompt(ctx, deps);
      return true;
    }

    if (payload.action === "all") {
      const integrations = await listConnectableIntegrations();
      const view = buildConnectListView(integrations, 0);
      await editMenu(ctx, view.text, view.keyboard);
      return true;
    }

    if (payload.action === "page") {
      const integrations = await listConnectableIntegrations();
      const view = buildConnectListView(integrations, payload.page);
      await editMenu(ctx, view.text, view.keyboard);
      return true;
    }

    if (payload.action === "pick") {
      const integrations = await listConnectableIntegrations();
      const integration = integrations.find((item) => item.id === payload.integrationID);
      if (!integration) {
        await alert(ctx, "connect.inactive");
        return true;
      }
      const view = buildConnectMethodView(integration);
      await editMenu(ctx, view.text, view.keyboard);
      return true;
    }

    await startConnection(ctx, deps, payload.integrationID, payload.methodID);
  } catch (error) {
    logger.error("[ProviderConnect] Callback failed:", error);
    await failure(ctx, "connect.start_error");
  }

  return true;
}

export async function handleConnectCodeTextAnswer(
  ctx: Context,
  deps: AppContainer,
): Promise<boolean> {
  const state = deps.interactionManager.getSnapshot();
  if (state?.kind !== "custom" || state.metadata.flow !== CODE_PROMPT_FLOW) {
    return false;
  }

  const text = ctx.message?.text;
  if (text === undefined || text.startsWith("/")) {
    return false;
  }

  const parsed = extractOauthCode(text);
  if (parsed.error) {
    await ctx.reply(t("connect.code_error", { message: parsed.error }));
    return true;
  }
  if (!parsed.code) {
    await ctx.reply(t("connect.code_empty"));
    return true;
  }

  const integrationID = String(state.metadata.integrationID ?? "");
  const attemptID = String(state.metadata.attemptID ?? "");

  try {
    await completeOauthConnection(integrationID, attemptID, parsed.code);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    logger.warn("[ProviderConnect] Code submission failed:", error);
    await ctx.reply(t("connect.code_error", { message }));
    return true;
  }

  deps.interactionManager.clear("provider_connect_code_submitted");
  if (ctx.chat && typeof state.metadata.messageId === "number") {
    await ctx.api.deleteMessage(ctx.chat.id, state.metadata.messageId).catch(() => {});
  }
  await ctx.reply(t("connect.code_submitted"));

  return true;
}

export async function handleConnectKeyTextAnswer(
  ctx: Context,
  deps: AppContainer,
): Promise<boolean> {
  const state = deps.interactionManager.getSnapshot();
  if (state?.kind !== "custom" || state.metadata.flow !== KEY_PROMPT_FLOW) {
    return false;
  }

  const text = ctx.message?.text;
  if (text === undefined || text.startsWith("/")) {
    return false;
  }

  // The key must not stay in the Telegram history.
  await ctx.deleteMessage().catch(() => {});

  const key = text.trim();
  if (!key) {
    await ctx.reply(t("connect.key_empty"));
    return true;
  }

  const integrationID = String(state.metadata.integrationID ?? "");
  const methodID = String(state.metadata.methodID ?? "");
  let integrationName = integrationID;

  try {
    const integrations = await listConnectableIntegrations();
    const integration = integrations.find((item) => item.id === integrationID);
    const method = integration?.methods.find((item) => item.id === methodID);
    if (!integration || !method) {
      throw new Error("Provider is no longer available");
    }

    integrationName = integration.name;
    await connectProviderKey(integration, method, key);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    logger.warn("[ProviderConnect] Key submission failed:", error);
    await ctx.reply(t("connect.key_error", { message }));
    return true;
  }

  deps.interactionManager.clear("provider_connect_key_saved");
  if (ctx.chat && typeof state.metadata.messageId === "number") {
    await ctx.api.deleteMessage(ctx.chat.id, state.metadata.messageId).catch(() => {});
  }

  logger.info(`[ProviderConnect] Key saved for ${integrationID}`);
  await ctx.reply(t("connect.key_success", { provider: integrationName }));
  return true;
}

export async function handleConnectSearchTextAnswer(
  ctx: Context,
  deps: AppContainer,
): Promise<boolean> {
  const state = deps.interactionManager.getSnapshot();
  if (state?.kind !== "custom" || state.metadata.flow !== SEARCH_PROMPT_FLOW) {
    return false;
  }

  const text = ctx.message?.text;
  if (text === undefined || text.startsWith("/")) {
    return false;
  }

  const query = text.trim();
  if (!query) {
    await ctx.reply(t("connect.search_empty"));
    return true;
  }

  const integrations = await listConnectableIntegrations();
  const needle = query.toLowerCase();
  const matches = integrations.filter(
    (integration) =>
      integration.name.toLowerCase().includes(needle) ||
      integration.id.toLowerCase().includes(needle) ||
      (integration.providers ?? []).some((provider) =>
        provider.toLowerCase().includes(needle),
      ),
  );

  if (matches.length === 0) {
    await ctx.reply(t("connect.search_empty"));
    return true;
  }

  deps.interactionManager.clear("provider_connect_search_submitted");
  if (ctx.chat && typeof state.metadata.messageId === "number") {
    await ctx.api.deleteMessage(ctx.chat.id, state.metadata.messageId).catch(() => {});
  }

  const view = buildConnectListView(matches.slice(0, 8), 0, query);
  await replyWithInlineMenu(
    ctx,
    { menuKind: "connect", text: view.text, keyboard: view.keyboard },
    deps,
  );
  return true;
}
