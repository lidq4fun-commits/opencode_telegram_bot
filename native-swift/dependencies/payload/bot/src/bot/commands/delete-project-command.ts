import type { CommandContext, Context } from "grammy";
import type { AppContainer } from "../../app/bootstrap/app-container.js";
import { getProjects } from "../../app/services/project-service.js";
import { isForegroundBusy } from "../../app/services/run-control-service.js";
import { syncSessionDirectoryCache } from "../../app/services/session-cache-service.js";
import { t } from "../../i18n/index.js";
import { logger } from "../../utils/logger.js";
import { buildDeleteProjectListView } from "../menus/delete-menu.js";
import { replyWithInlineMenu } from "../menus/inline-menu.js";
import { replyBusyBlocked } from "../messages/busy-blocked-renderer.js";

export async function deleteProjectCommand(
  ctx: CommandContext<Context>,
  deps: AppContainer,
): Promise<void> {
  try {
    if (isForegroundBusy(deps)) {
      await replyBusyBlocked(ctx);
      return;
    }

    await syncSessionDirectoryCache();
    const projects = await getProjects();

    if (projects.length === 0) {
      await ctx.reply(t("delete.project.empty"));
      return;
    }

    const { text, keyboard } = buildDeleteProjectListView(projects, 0);
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
    logger.error("[DeleteProject] Error loading projects:", error);
    await ctx.reply(t("projects.fetch_error"));
  }
}
