import { InlineKeyboard, type Context } from "grammy";
import type { AppContainer } from "../../app/bootstrap/app-container.js";
import { opencodeClient } from "../../opencode/client.js";
import { config } from "../../config.js";
import { t } from "../../i18n/index.js";
import { logger } from "../../utils/logger.js";
import { clearProject, getCurrentProject, hideProject } from "../../app/stores/settings-store.js";
import { clearSession, getCurrentSession } from "../../app/services/session-service.js";
import { detachAttachedSession } from "../../app/services/attach-service.js";
import { getProjectById, getProjects } from "../../app/services/project-service.js";
import { noteDesktopSessionDeleted } from "../../app/services/desktop-sync-service.js";
import { isForegroundBusy } from "../../app/services/run-control-service.js";
import { replyBusyBlocked } from "../messages/busy-blocked-renderer.js";
import { appendInlineMenuCancelButton, ensureActiveInlineMenu } from "../menus/inline-menu.js";
import { loadSessionPage, type SessionListItem } from "../menus/session-selection-menu.js";
import {
  buildDeleteProjectConfirmView,
  buildDeleteProjectListView,
  buildDeleteSessionConfirmView,
  buildDeleteSessionListView,
  DELETE_CALLBACK_PREFIX,
  parseDeleteCallback,
  type DeleteCallbackPayload,
} from "../menus/delete-menu.js";
import { alert, failure, notify } from "./feedback.js";

type DeleteAction = Extract<DeleteCallbackPayload, { action: "page" | "pick" | "confirm" }>;

function projectMatches(left: string, right: string): boolean {
  return (
    left.replace(/[\\/]+$/, "").toLowerCase() === right.replace(/[\\/]+$/, "").toLowerCase()
  );
}

function withCancel(keyboard: InlineKeyboard): InlineKeyboard {
  return appendInlineMenuCancelButton(keyboard, "delete");
}

function emptyView(text: string): { text: string; keyboard: InlineKeyboard } {
  return { text, keyboard: new InlineKeyboard() };
}

async function editView(
  ctx: Context,
  view: { text: string; keyboard: InlineKeyboard },
): Promise<void> {
  await ctx.editMessageText(view.text, { reply_markup: withCancel(view.keyboard) });
}

async function sessionListView(
  page: number,
): Promise<{ text: string; keyboard: InlineKeyboard } | null> {
  const currentProject = getCurrentProject();
  if (!currentProject) {
    return null;
  }

  const pageSize = config.bot.sessionsListLimit;
  const pageData = await loadSessionPage(currentProject.worktree, page, pageSize);
  if (pageData.sessions.length === 0) {
    return null;
  }

  return buildDeleteSessionListView(pageData, pageSize);
}

async function projectListView(
  page: number,
): Promise<{ text: string; keyboard: InlineKeyboard } | null> {
  const projects = await getProjects();
  if (projects.length === 0) {
    return null;
  }

  return buildDeleteProjectListView(projects, page);
}

async function removeProjectSessions(worktree: string): Promise<number> {
  let removed = 0;

  for (;;) {
    const { data, error } = await opencodeClient.session.list({
      directory: worktree,
      limit: 100,
      roots: true,
    });

    if (error || !data) {
      throw error || new Error("No data received from server");
    }
    if (data.length === 0) {
      return removed;
    }

    let deletedThisRound = 0;
    for (const session of data) {
      const result = await opencodeClient.session.delete({
        sessionID: session.id,
        directory: worktree,
      });
      if (result.error) {
        logger.warn(`[DeleteProject] Failed to delete session ${session.id}:`, result.error);
        continue;
      }
      removed += 1;
      deletedThisRound += 1;
    }

    // A session that cannot be removed would otherwise be listed forever.
    if (deletedThisRound === 0) {
      return removed;
    }
  }
}

async function confirmSessionDelete(
  ctx: Context,
  deps: AppContainer,
  sessionId: string,
): Promise<void> {
  const { error } = await opencodeClient.session.delete({ sessionID: sessionId });
  if (error) {
    throw error;
  }
  noteDesktopSessionDeleted(sessionId);

  if (getCurrentSession()?.id === sessionId) {
    detachAttachedSession("session_deleted", deps);
    deps.foregroundSessionState.markIdle(sessionId);
    deps.assistantRunState.clearRun(sessionId, "session_deleted");
    clearSession();
    deps.resetAggregator();
    if (deps.pinnedMessageManager.isInitialized()) {
      await deps.pinnedMessageManager.refresh().catch((refreshError: unknown) => {
        logger.warn("[DeleteSession] Failed to refresh pinned message:", refreshError);
      });
    }
  }

  logger.info(`[DeleteSession] Deleted session: ${sessionId}`);
  await notify(ctx, "delete.session.deleted");

  const view = await sessionListView(0);
  await editView(ctx, view ?? emptyView(t("delete.session.empty")));
}

async function confirmProjectDelete(
  ctx: Context,
  deps: AppContainer,
  projectId: string,
): Promise<void> {
  const project = await getProjectById(projectId);
  const removed = await removeProjectSessions(project.worktree);
  hideProject(project.worktree);

  const currentProject = getCurrentProject();
  if (currentProject && projectMatches(currentProject.worktree, project.worktree)) {
    detachAttachedSession("project_deleted", deps);
    clearSession();
    clearProject();
    deps.resetAggregator();
    if (deps.pinnedMessageManager.isInitialized()) {
      await deps.pinnedMessageManager.clear().catch((clearError: unknown) => {
        logger.warn("[DeleteProject] Failed to clear pinned message:", clearError);
      });
    }
  }

  logger.info(
    `[DeleteProject] Hidden project ${project.worktree} and deleted ${removed} sessions`,
  );
  await notify(ctx, "delete.project.deleted", { count: removed });

  const view = await projectListView(0);
  await editView(ctx, view ?? emptyView(t("delete.project.empty")));
}

async function handleSessionAction(
  ctx: Context,
  deps: AppContainer,
  payload: DeleteAction,
): Promise<void> {
  if (payload.action === "page") {
    const view = await sessionListView(payload.page);
    if (!view) {
      await notify(ctx, "delete.session.empty");
      await editView(ctx, emptyView(t("delete.session.empty")));
      return;
    }
    await ctx.answerCallbackQuery();
    await editView(ctx, view);
    return;
  }

  const currentProject = getCurrentProject();
  if (!currentProject) {
    await alert(ctx, "sessions.project_not_selected");
    return;
  }

  if (payload.action === "pick") {
    const { data: session, error } = await opencodeClient.session.get({
      sessionID: payload.id,
      directory: currentProject.worktree,
    });
    if (error || !session) {
      await alert(ctx, "delete.session.not_found");
      return;
    }

    const confirmed: SessionListItem = {
      id: session.id,
      title: session.title,
      directory: session.directory,
      time: { created: session.time?.created ?? 0 },
    };
    await ctx.answerCallbackQuery();
    await editView(ctx, buildDeleteSessionConfirmView(confirmed));
    return;
  }

  await confirmSessionDelete(ctx, deps, payload.id);
}

async function handleProjectAction(
  ctx: Context,
  deps: AppContainer,
  payload: DeleteAction,
): Promise<void> {
  if (payload.action === "page") {
    const view = await projectListView(payload.page);
    if (!view) {
      await notify(ctx, "delete.project.empty");
      await editView(ctx, emptyView(t("delete.project.empty")));
      return;
    }
    await ctx.answerCallbackQuery();
    await editView(ctx, view);
    return;
  }

  if (payload.action === "pick") {
    const project = await getProjectById(payload.id);
    await ctx.answerCallbackQuery();
    await editView(ctx, buildDeleteProjectConfirmView(project));
    return;
  }

  await confirmProjectDelete(ctx, deps, payload.id);
}

export async function handleDeleteCallback(ctx: Context, deps: AppContainer): Promise<boolean> {
  const data = ctx.callbackQuery?.data;
  if (!data || !data.startsWith(DELETE_CALLBACK_PREFIX)) {
    return false;
  }

  const payload = parseDeleteCallback(data);
  if (!payload) {
    return false;
  }

  if (isForegroundBusy(deps)) {
    await replyBusyBlocked(ctx);
    return true;
  }

  const isActiveMenu = await ensureActiveInlineMenu(ctx, "delete", deps);
  if (!isActiveMenu) {
    return true;
  }

  try {
    if (payload.action === "back") {
      if (payload.kind === "session") {
        const view = await sessionListView(0);
        await ctx.answerCallbackQuery();
        await editView(ctx, view ?? emptyView(t("delete.session.empty")));
      } else {
        const view = await projectListView(0);
        await ctx.answerCallbackQuery();
        await editView(ctx, view ?? emptyView(t("delete.project.empty")));
      }
      return true;
    }

    if (payload.kind === "session") {
      await handleSessionAction(ctx, deps, payload);
    } else {
      await handleProjectAction(ctx, deps, payload);
    }
  } catch (error) {
    logger.error("[Delete] Error handling delete callback:", error);
    await failure(ctx, "delete.error");
  }

  return true;
}
