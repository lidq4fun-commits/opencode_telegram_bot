import path from "node:path";
import { mkdir } from "node:fs/promises";
import type { Context } from "grammy";
import type { AppContainer } from "../../app/bootstrap/app-container.js";
import type { I18nKey } from "../../i18n/en.js";
import { t } from "../../i18n/index.js";
import { logger } from "../../utils/logger.js";
import {
  CREATE_PROJECT_CANCEL_CALLBACK,
  openProjectDirectory,
} from "./file-browser-callback-handler.js";

export type ProjectFolderNameError = "empty" | "invalid" | "reserved" | "too_long";

const NAME_ERROR_KEYS: Record<ProjectFolderNameError, I18nKey> = {
  empty: "create_project.empty_name",
  invalid: "create_project.invalid_name",
  reserved: "create_project.reserved_name",
  too_long: "create_project.too_long",
};

const RESERVED_NAMES =
  /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$/i;
const INVALID_NAME_CHARS = /[<>:"/\\|?*\u0000-\u001f]/;
const MAX_FOLDER_NAME_LENGTH = 120;

export function validateProjectFolderName(rawName: string): ProjectFolderNameError | null {
  const name = rawName.trim();
  if (!name) {
    return "empty";
  }
  if (name === "." || name === ".." || INVALID_NAME_CHARS.test(name)) {
    return "invalid";
  }
  // Windows drops these silently, so a name that ends with one is not a folder name.
  if (/[. ]$/.test(name)) {
    return "invalid";
  }
  if (RESERVED_NAMES.test(name)) {
    return "reserved";
  }
  if (name.length > MAX_FOLDER_NAME_LENGTH) {
    return "too_long";
  }
  return null;
}

interface CreateProjectPromptState {
  parentPath: string;
  messageId: number;
}

function getCreateProjectPromptState(deps: AppContainer): CreateProjectPromptState | null {
  const state = deps.interactionManager.getSnapshot();
  if (
    state?.kind !== "custom" ||
    state.metadata.flow !== "create_project" ||
    typeof state.metadata.parentPath !== "string" ||
    typeof state.metadata.messageId !== "number"
  ) {
    return null;
  }

  return {
    parentPath: state.metadata.parentPath,
    messageId: state.metadata.messageId,
  };
}

async function deletePromptMessage(ctx: Context, messageId: number): Promise<void> {
  if (!ctx.chat) {
    return;
  }
  await ctx.api.deleteMessage(ctx.chat.id, messageId).catch(() => {});
}

export async function handleCreateProjectTextAnswer(
  ctx: Context,
  deps: AppContainer,
): Promise<boolean> {
  const state = getCreateProjectPromptState(deps);
  if (!state) {
    return false;
  }

  const text = ctx.message?.text;
  if (text === undefined || text.startsWith("/")) {
    return false;
  }

  const name = text.trim();
  const nameError = validateProjectFolderName(name);
  if (nameError) {
    await ctx.reply(t(NAME_ERROR_KEYS[nameError]));
    return true;
  }

  const targetPath = path.join(state.parentPath, name);

  try {
    await mkdir(targetPath);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === "EEXIST") {
      await ctx.reply(t("create_project.exists"));
      return true;
    }

    if (code === "EACCES" || code === "EPERM" || code === "EROFS") {
      logger.error(`[CreateProject] No write permission for ${state.parentPath}:`, error);
      deps.interactionManager.clear("create_project_permission_denied");
      await deletePromptMessage(ctx, state.messageId);
      await ctx.reply(t("create_project.permission_denied"));
      return true;
    }

    logger.error(`[CreateProject] Could not create folder ${targetPath}:`, error);
    await ctx.reply(t("create_project.error"));
    return true;
  }

  logger.info(`[CreateProject] Created folder: ${targetPath}`);
  deps.interactionManager.clear("create_project_folder_created");
  await deletePromptMessage(ctx, state.messageId);

  try {
    const keyboard = await openProjectDirectory(ctx, targetPath, deps);
    await ctx.reply(t("create_project.created", { name, path: targetPath }), {
      ...(keyboard ? { reply_markup: keyboard } : {}),
    });
  } catch (error) {
    logger.error(`[CreateProject] Folder created but project switch failed: ${targetPath}`, error);
    await ctx.reply(t("create_project.switch_error", { path: targetPath }));
  }

  return true;
}

export async function handleCreateProjectCancel(
  ctx: Context,
  deps: AppContainer,
): Promise<boolean> {
  if (ctx.callbackQuery?.data !== CREATE_PROJECT_CANCEL_CALLBACK) {
    return false;
  }

  const state = getCreateProjectPromptState(deps);
  const callbackMessageId = ctx.callbackQuery.message?.message_id;

  if (!state || state.messageId !== callbackMessageId) {
    await ctx
      .answerCallbackQuery({ text: t("create_project.inactive"), show_alert: true })
      .catch(() => {});
    return true;
  }

  deps.interactionManager.clear("create_project_cancelled");
  await ctx.answerCallbackQuery({ text: t("create_project.cancelled") }).catch(() => {});
  await ctx.deleteMessage().catch(() => {});

  logger.info(`[CreateProject] Cancelled for: ${state.parentPath}`);
  return true;
}
