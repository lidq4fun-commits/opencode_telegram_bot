import type { CommandContext, Context } from "grammy";
import type { AppContainer } from "../../app/bootstrap/app-container.js";
import { getCurrentProject } from "../../app/stores/settings-store.js";
import { isForegroundBusy } from "../../app/services/run-control-service.js";
import { config } from "../../config.js";
import { t } from "../../i18n/index.js";
import { logger } from "../../utils/logger.js";
import { buildDeleteSessionListView } from "../menus/delete-menu.js";
import { replyWithInlineMenu } from "../menus/inline-menu.js";
import { loadSessionPage } from "../menus/session-selection-menu.js";
import { replyBusyBlocked } from "../messages/busy-blocked-renderer.js";

export async function deleteSessionCommand(
  ctx: CommandContext<Context>,
  deps: AppContainer,
): Promise<void> {
  try {
    if (isForegroundBusy(deps)) {
      await replyBusyBlocked(ctx);
      return;
    }

    const currentProject = getCurrentProject();
    if (!currentProject) {
      await ctx.reply(t("sessions.project_not_selected"));
      return;
    }

    const pageSize = config.bot.sessionsListLimit;
    const pageData = await loadSessionPage(currentProject.worktree, 0, pageSize);

    if (pageData.sessions.length === 0) {
      await ctx.reply(t("delete.session.empty"));
      return;
    }

    const { text, keyboard } = buildDeleteSessionListView(pageData, pageSize);
    await replyWithInlineMenu(
      ctx,
      {
        menuKind: "delete",
        text,
        keyboard,
      },
      deps,
    );
  } catch (error) {
    logger.error("[DeleteSession] Error loading sessions:", error);
    await ctx.reply(t("delete.session.error"));
  }
}
