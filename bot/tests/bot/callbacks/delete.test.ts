import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Context } from "grammy";
import type { AppContainer } from "../../../src/app/bootstrap/app-container.js";
import { createTestAppContainer } from "../../helpers/app-container.js";

const mocks = vi.hoisted(() => ({
  isForegroundBusy: vi.fn(() => false),
  getCurrentProject: vi.fn(),
  clearProject: vi.fn(),
  hideProject: vi.fn(),
  getCurrentSession: vi.fn(),
  clearSession: vi.fn(),
  detachAttachedSession: vi.fn(),
  getProjects: vi.fn(),
  getProjectById: vi.fn(),
  loadSessionPage: vi.fn(),
  syncSessionDirectoryCache: vi.fn(),
  sessionList: vi.fn(),
  sessionDelete: vi.fn(),
  sessionGet: vi.fn(),
  ensureActiveInlineMenu: vi.fn(async () => true),
  replyWithInlineMenu: vi.fn(),
}));

vi.mock("../../../src/app/services/run-control-service.js", () => ({
  isForegroundBusy: mocks.isForegroundBusy,
}));

vi.mock("../../../src/app/stores/settings-store.js", () => ({
  getCurrentProject: mocks.getCurrentProject,
  clearProject: mocks.clearProject,
  hideProject: mocks.hideProject,
}));

vi.mock("../../../src/app/services/session-service.js", () => ({
  getCurrentSession: mocks.getCurrentSession,
  clearSession: mocks.clearSession,
}));

vi.mock("../../../src/app/services/attach-service.js", () => ({
  detachAttachedSession: mocks.detachAttachedSession,
}));

vi.mock("../../../src/app/services/project-service.js", () => ({
  getProjects: mocks.getProjects,
  getProjectById: mocks.getProjectById,
}));

vi.mock("../../../src/app/services/session-cache-service.js", () => ({
  syncSessionDirectoryCache: mocks.syncSessionDirectoryCache,
  __resetSessionDirectoryCacheForTests: vi.fn(),
}));

vi.mock("../../../src/opencode/client.js", () => ({
  opencodeClient: {
    session: {
      list: mocks.sessionList,
      delete: mocks.sessionDelete,
      get: mocks.sessionGet,
    },
  },
}));

vi.mock("../../../src/bot/menus/session-selection-menu.js", () => ({
  loadSessionPage: mocks.loadSessionPage,
}));

vi.mock("../../../src/bot/menus/inline-menu.js", async () => {
  const actual = await vi.importActual<typeof import("../../../src/bot/menus/inline-menu.js")>(
    "../../../src/bot/menus/inline-menu.js",
  );
  return {
    ...actual,
    ensureActiveInlineMenu: mocks.ensureActiveInlineMenu,
    replyWithInlineMenu: mocks.replyWithInlineMenu,
  };
});

import { deleteProjectCommand } from "../../../src/bot/commands/delete-project-command.js";
import { deleteSessionCommand } from "../../../src/bot/commands/delete-session-command.js";
import { handleDeleteCallback } from "../../../src/bot/callbacks/delete-callback-handler.js";

function createContext(data?: string): Context {
  return {
    chat: { id: 1 },
    from: { id: 1 },
    callbackQuery: data ? { data, message: { message_id: 10 }, from: { id: 1 } } : undefined,
    answerCallbackQuery: vi.fn().mockResolvedValue(undefined),
    editMessageText: vi.fn().mockResolvedValue(undefined),
    reply: vi.fn().mockResolvedValue({ message_id: 1 }),
  } as unknown as Context;
}

const project = { id: "proj-1", worktree: "E:\\repo", name: "Repo" };
const session = {
  id: "ses_1",
  title: "Fix the bug",
  directory: "E:\\repo",
  time: { created: 0 },
};

let container: AppContainer;

beforeEach(() => {
  container = createTestAppContainer();
  mocks.isForegroundBusy.mockReturnValue(false);
  mocks.getCurrentProject.mockReturnValue(project);
  mocks.getCurrentSession.mockReturnValue(null);
  mocks.getProjects.mockResolvedValue([project]);
  mocks.getProjectById.mockResolvedValue(project);
  mocks.loadSessionPage.mockResolvedValue({ sessions: [session], hasNext: false, page: 0 });
  mocks.sessionDelete.mockResolvedValue({ data: true, error: null });
  mocks.sessionGet.mockResolvedValue({ data: session, error: null });
  mocks.ensureActiveInlineMenu.mockResolvedValue(true);
});

describe("bot/commands/delete", () => {
  it("lists current project sessions for deletion", async () => {
    const ctx = createContext();
    await deleteSessionCommand(ctx as never, container);

    expect(mocks.replyWithInlineMenu).toHaveBeenCalledWith(
      ctx,
      expect.objectContaining({ menuKind: "delete" }),
      container,
    );
    const keyboard = mocks.replyWithInlineMenu.mock.calls[0]?.[1]?.keyboard as {
      inline_keyboard: Array<Array<{ callback_data?: string }>>;
    };
    expect(keyboard.inline_keyboard[0]?.[0]?.callback_data).toBe("delete:s:pick:ses_1");
  });

  it("requires a selected project before deleting sessions", async () => {
    mocks.getCurrentProject.mockReturnValue(undefined);
    const ctx = createContext();
    await deleteSessionCommand(ctx as never, container);

    expect(ctx.reply).toHaveBeenCalled();
    expect(mocks.replyWithInlineMenu).not.toHaveBeenCalled();
  });

  it("lists projects for deletion", async () => {
    const ctx = createContext();
    await deleteProjectCommand(ctx as never, container);

    expect(mocks.syncSessionDirectoryCache).toHaveBeenCalled();
    const keyboard = mocks.replyWithInlineMenu.mock.calls[0]?.[1]?.keyboard as {
      inline_keyboard: Array<Array<{ callback_data?: string }>>;
    };
    expect(keyboard.inline_keyboard[0]?.[0]?.callback_data).toBe("delete:p:pick:proj-1");
  });
});

describe("bot/callbacks/delete", () => {
  it("deletes a confirmed session and clears it when it is the current one", async () => {
    mocks.getCurrentSession.mockReturnValue({ id: "ses_1", title: session.title, directory: project.worktree });
    mocks.loadSessionPage.mockResolvedValue({ sessions: [], hasNext: false, page: 0 });
    const ctx = createContext("delete:s:confirm:ses_1");

    const handled = await handleDeleteCallback(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.sessionDelete).toHaveBeenCalledWith({ sessionID: "ses_1" });
    expect(mocks.detachAttachedSession).toHaveBeenCalledWith("session_deleted", container);
    expect(mocks.clearSession).toHaveBeenCalled();
    expect(ctx.answerCallbackQuery).toHaveBeenCalled();
    expect(ctx.editMessageText).toHaveBeenCalled();
  });

  it("removes all sessions of a confirmed project and hides it", async () => {
    mocks.getCurrentProject.mockReturnValue(undefined);
    mocks.sessionList
      .mockResolvedValueOnce({ data: [{ id: "ses_a" }, { id: "ses_b" }], error: null })
      .mockResolvedValueOnce({ data: [], error: null });
    const ctx = createContext("delete:p:confirm:proj-1");

    const handled = await handleDeleteCallback(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.sessionDelete).toHaveBeenCalledTimes(2);
    expect(mocks.hideProject).toHaveBeenCalledWith(project.worktree);
    expect(mocks.clearProject).not.toHaveBeenCalled();
    expect(ctx.editMessageText).toHaveBeenCalled();
  });

  it("ignores callbacks from another menu", async () => {
    mocks.ensureActiveInlineMenu.mockResolvedValue(false);
    const ctx = createContext("delete:s:pick:ses_1");

    const handled = await handleDeleteCallback(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.sessionGet).not.toHaveBeenCalled();
  });
});
