import type { CommandContext, Context } from "grammy";
import type { AppContainer } from "../../app/bootstrap/app-container.js";
import { getBrowserRoots } from "../../app/services/file-browser-service.js";
import { isForegroundBusy } from "../../app/services/run-control-service.js";
import { t } from "../../i18n/index.js";
import { isContainerRuntime } from "../../runtime/container.js";
import { logger } from "../../utils/logger.js";
import { buildOpenRootsKeyboard, clearOpenPathIndex, renderOpenBrowseView } from "../menus/file-browser-menu.js";
import { replyBusyBlocked } from "../messages/busy-blocked-renderer.js";

export type OpenCommandDeps = Pick<
  AppContainer,
  "attachManager" | "foregroundSessionState" | "interactionManager"
>;

export interface OpenCommandOptions {
  /** Browse folders to create a new project folder inside the selected one. */
  createProject?: boolean;
}

export async function openCommand(
  ctx: CommandContext<Context>,
  deps: OpenCommandDeps,
  options: OpenCommandOptions = {},
): Promise<void> {
  try {
    if (isForegroundBusy(deps)) {
      await replyBusyBlocked(ctx);
      return;
    }

    if (isContainerRuntime()) {
      await ctx.reply(t("runtime.container.command_unavailable"));
      return;
    }

    clearOpenPathIndex();

    const roots = getBrowserRoots();
    const selectLabel = options.createProject ? t("create_project.select_here") : undefined;
    const cancelLabel = options.createProject ? t("create_project.cancel_browse") : undefined;
    let text: string;
    let keyboard;

    const singleRoot = roots[0];
    if (roots.length === 1 && singleRoot) {
      const view = await renderOpenBrowseView(singleRoot, 0, selectLabel, cancelLabel);
      if ("error" in view) {
        await ctx.reply(t("open.scan_error", { error: view.error }));
        return;
      }
      text = view.text;
      keyboard = view.keyboard;
    } else {
      text = t("open.select_root");
      keyboard = buildOpenRootsKeyboard(cancelLabel);
    }

    const message = await ctx.reply(text, { reply_markup: keyboard });

    deps.interactionManager.start({
      kind: "inline",
      expectedInput: "callback",
      metadata: {
        menuKind: "open",
        messageId: message.message_id,
        createProject: options.createProject === true,
      },
    });
  } catch (error) {
    logger.error("[Bot] Error opening directory browser:", error);
    await ctx.reply(t("open.open_error"));
  }
}
