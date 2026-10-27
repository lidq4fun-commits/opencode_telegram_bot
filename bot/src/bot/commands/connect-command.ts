import type { CommandContext, Context } from "grammy";
import type { AppContainer } from "../../app/bootstrap/app-container.js";
import { listConnectableIntegrations } from "../../app/services/provider-connection-service.js";
import { isForegroundBusy } from "../../app/services/run-control-service.js";
import { t } from "../../i18n/index.js";
import { logger } from "../../utils/logger.js";
import { buildConnectListView } from "../menus/connect-menu.js";
import { replyWithInlineMenu } from "../menus/inline-menu.js";
import { replyBusyBlocked } from "../messages/busy-blocked-renderer.js";

export async function connectCommand(
  ctx: CommandContext<Context>,
  deps: AppContainer,
): Promise<void> {
  try {
    if (isForegroundBusy(deps)) {
      await replyBusyBlocked(ctx);
      return;
    }

    const integrations = await listConnectableIntegrations();
    if (integrations.length === 0) {
      await ctx.reply(t("connect.empty"));
      return;
    }

    const { text, keyboard } = buildConnectListView(integrations, 0);
    await replyWithInlineMenu(
      ctx,
      {
        menuKind: "connect",
        text,
        keyboard,
      },
      deps,
    );
  } catch (error) {
    logger.error("[ProviderConnect] Could not list integrations:", error);
    await ctx.reply(t("connect.start_error"));
  }
}
