import { InlineKeyboard } from "grammy";
import { config } from "../../config.js";
import { formatSessionTitle } from "../../app/formatters/session-title-formatter.js";
import { getDateLocale, t } from "../../i18n/index.js";
import type { ProjectInfo } from "../../app/types/project.js";
import { getProjectFolderName } from "./project-selection-menu.js";
import type { SessionListItem, SessionPage } from "./session-selection-menu.js";

export const DELETE_CALLBACK_PREFIX = "delete:";

const SESSION_PREFIX = `${DELETE_CALLBACK_PREFIX}s:`;
const PROJECT_PREFIX = `${DELETE_CALLBACK_PREFIX}p:`;
const MAX_BUTTON_LABEL_LENGTH = 64;

export type DeleteCallbackPayload =
  | { kind: "session"; action: "page"; page: number }
  | { kind: "session"; action: "pick" | "confirm"; id: string }
  | { kind: "session"; action: "back" }
  | { kind: "project"; action: "page"; page: number }
  | { kind: "project"; action: "pick" | "confirm"; id: string }
  | { kind: "project"; action: "back" };

function truncateLabel(label: string): string {
  if (label.length <= MAX_BUTTON_LABEL_LENGTH) {
    return label;
  }

  return `${label.slice(0, MAX_BUTTON_LABEL_LENGTH - 3)}...`;
}

export function parseDeleteCallback(data: string): DeleteCallbackPayload | null {
  const isSession = data.startsWith(SESSION_PREFIX);
  if (!isSession && !data.startsWith(PROJECT_PREFIX)) {
    return null;
  }

  const kind = isSession ? "session" : "project";
  const payload = data.slice((isSession ? SESSION_PREFIX : PROJECT_PREFIX).length);

  if (payload === "back") {
    return { kind, action: "back" };
  }

  if (payload.startsWith("page:")) {
    const page = Number.parseInt(payload.slice(5), 10);
    return Number.isInteger(page) && page >= 0 ? { kind, action: "page", page } : null;
  }

  if (payload.startsWith("pick:") || payload.startsWith("confirm:")) {
    const action = payload.startsWith("pick:") ? "pick" : "confirm";
    const id = payload.slice(action.length + 1);
    return id.length > 0 ? { kind, action, id } : null;
  }

  return null;
}

function buildPageRow(
  keyboard: InlineKeyboard,
  kind: "session" | "project",
  page: number,
  totalPages: number,
): void {
  if (totalPages <= 1) {
    return;
  }

  const prefix = kind === "session" ? SESSION_PREFIX : PROJECT_PREFIX;
  if (page > 0) {
    keyboard.text(t("projects.prev_page"), `${prefix}page:${page - 1}`);
  }
  if (page < totalPages - 1) {
    keyboard.text(t("projects.next_page"), `${prefix}page:${page + 1}`);
  }
  keyboard.row();
}

export function buildDeleteSessionListView(
  pageData: SessionPage,
  pageSize: number,
): { text: string; keyboard: InlineKeyboard } {
  const keyboard = new InlineKeyboard();
  const locale = getDateLocale();
  const totalPages = pageData.hasNext ? pageData.page + 2 : pageData.page + 1;
  const startIndex = pageData.page * pageSize;

  pageData.sessions.forEach((session, index) => {
    const date = new Date(session.time.created).toLocaleDateString(locale);
    const label = truncateLabel(
      `🗑 ${startIndex + index + 1}. ${formatSessionTitle(session.title)} (${date})`,
    );
    keyboard.text(label, `${SESSION_PREFIX}pick:${session.id}`).row();
  });

  buildPageRow(keyboard, "session", pageData.page, totalPages);

  return {
    text:
      pageData.page === 0
        ? t("delete.session.select")
        : t("delete.session.select_page", { page: String(pageData.page + 1) }),
    keyboard,
  };
}

export function buildDeleteSessionConfirmView(session: SessionListItem): {
  text: string;
  keyboard: InlineKeyboard;
} {
  return {
    text: t("delete.session.confirm", { title: formatSessionTitle(session.title) }),
    keyboard: new InlineKeyboard()
      .text(t("delete.button.confirm"), `${SESSION_PREFIX}confirm:${session.id}`)
      .row()
      .text(t("delete.button.back"), `${SESSION_PREFIX}back`),
  };
}

export function buildDeleteProjectListView(
  projects: ProjectInfo[],
  page: number,
): { text: string; keyboard: InlineKeyboard } {
  const pageSize = config.bot.projectsListLimit;
  const totalPages = Math.max(1, Math.ceil(projects.length / pageSize));
  const normalizedPage = Math.min(Math.max(0, page), totalPages - 1);
  const startIndex = normalizedPage * pageSize;
  const keyboard = new InlineKeyboard();

  projects.slice(startIndex, startIndex + pageSize).forEach((project, index) => {
    const label = truncateLabel(
      `🗑 ${startIndex + index + 1}. ${getProjectFolderName(project.worktree)} [${project.worktree}]`,
    );
    keyboard.text(label, `${PROJECT_PREFIX}pick:${project.id}`).row();
  });

  buildPageRow(keyboard, "project", normalizedPage, totalPages);

  return {
    text:
      normalizedPage === 0
        ? t("delete.project.select")
        : t("delete.project.select_page", { page: String(normalizedPage + 1) }),
    keyboard,
  };
}

export function buildDeleteProjectConfirmView(project: ProjectInfo): {
  text: string;
  keyboard: InlineKeyboard;
} {
  return {
    text: t("delete.project.confirm", {
      project: project.name || getProjectFolderName(project.worktree),
      path: project.worktree,
    }),
    keyboard: new InlineKeyboard()
      .text(t("delete.button.confirm"), `${PROJECT_PREFIX}confirm:${project.id}`)
      .row()
      .text(t("delete.button.back"), `${PROJECT_PREFIX}back`),
  };
}
