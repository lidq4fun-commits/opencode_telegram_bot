import path from "node:path";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Context } from "grammy";
import type { AppContainer } from "../../../src/app/bootstrap/app-container.js";
import { t } from "../../../src/i18n/index.js";
import { createTestAppContainer } from "../../helpers/app-container.js";

const mocks = vi.hoisted(() => ({
  mkdir: vi.fn(),
  openProjectDirectory: vi.fn(),
}));

vi.mock("node:fs/promises", async (importOriginal) => ({
  ...(await importOriginal<typeof import("node:fs/promises")>()),
  mkdir: mocks.mkdir,
}));

vi.mock("../../../src/bot/callbacks/file-browser-callback-handler.js", () => ({
  CREATE_PROJECT_CANCEL_CALLBACK: "create_project:cancel",
  openProjectDirectory: mocks.openProjectDirectory,
}));

import {
  handleCreateProjectCancel,
  handleCreateProjectTextAnswer,
  validateProjectFolderName,
} from "../../../src/bot/callbacks/create-project-callback-handler.js";

const parentPath = path.join("E:", "work");

function createContext(text?: string, callbackData?: string): Context {
  return {
    chat: { id: 1 },
    from: { id: 1 },
    message: text === undefined ? undefined : { text },
    callbackQuery: callbackData
      ? { data: callbackData, message: { message_id: 55 }, from: { id: 1 } }
      : undefined,
    api: { deleteMessage: vi.fn().mockResolvedValue(undefined) },
    reply: vi.fn().mockResolvedValue({ message_id: 2 }),
    answerCallbackQuery: vi.fn().mockResolvedValue(undefined),
    deleteMessage: vi.fn().mockResolvedValue(undefined),
  } as unknown as Context;
}

let container: AppContainer;

beforeEach(() => {
  container = createTestAppContainer();
  mocks.mkdir.mockReset().mockResolvedValue(undefined);
  mocks.openProjectDirectory.mockReset().mockResolvedValue({ keyboard: true });
});

function startPrompt(): void {
  container.interactionManager.start({
    kind: "custom",
    expectedInput: "text",
    metadata: { flow: "create_project", parentPath, messageId: 55 },
  });
}

describe("create_project name validation", () => {
  it("accepts a normal folder name", () => {
    expect(validateProjectFolderName("  my-project  ")).toBeNull();
  });

  it("rejects empty, reserved and invalid names", () => {
    expect(validateProjectFolderName("   ")).toBe("empty");
    expect(validateProjectFolderName("a/b")).toBe("invalid");
    expect(validateProjectFolderName("a:b")).toBe("invalid");
    expect(validateProjectFolderName("..")).toBe("invalid");
    expect(validateProjectFolderName("name.")).toBe("invalid");
    expect(validateProjectFolderName("name. ")).toBe("invalid");
    expect(validateProjectFolderName("CON")).toBe("reserved");
    expect(validateProjectFolderName("lpt9")).toBe("reserved");
    expect(validateProjectFolderName("x".repeat(121))).toBe("too_long");
  });
});

describe("create_project flow", () => {
  it("creates the folder and switches to the new project", async () => {
    startPrompt();
    const ctx = createContext("demo-app");

    const handled = await handleCreateProjectTextAnswer(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.mkdir).toHaveBeenCalledWith(path.join(parentPath, "demo-app"));
    expect(container.interactionManager.getSnapshot()).toBeNull();
    expect(ctx.api.deleteMessage).toHaveBeenCalledWith(1, 55);
    expect(mocks.openProjectDirectory).toHaveBeenCalledWith(
      ctx,
      path.join(parentPath, "demo-app"),
      container,
    );
    expect(ctx.reply).toHaveBeenCalledWith(
      expect.stringContaining("demo-app"),
      expect.anything(),
    );
  });

  it("keeps waiting when the name is invalid", async () => {
    startPrompt();
    const ctx = createContext("bad/name");

    const handled = await handleCreateProjectTextAnswer(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.mkdir).not.toHaveBeenCalled();
    expect(container.interactionManager.getSnapshot()?.kind).toBe("custom");
  });

  it("keeps waiting when the folder already exists", async () => {
    startPrompt();
    mocks.mkdir.mockRejectedValue(Object.assign(new Error("exists"), { code: "EEXIST" }));
    const ctx = createContext("existing");

    const handled = await handleCreateProjectTextAnswer(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.openProjectDirectory).not.toHaveBeenCalled();
    expect(container.interactionManager.getSnapshot()?.kind).toBe("custom");
  });

  it("reports a permission error and closes the prompt when the parent is not writable", async () => {
    startPrompt();
    mocks.mkdir.mockRejectedValue(Object.assign(new Error("denied"), { code: "EACCES" }));
    const ctx = createContext("forbidden");

    const handled = await handleCreateProjectTextAnswer(ctx, container);

    expect(handled).toBe(true);
    expect(mocks.openProjectDirectory).not.toHaveBeenCalled();
    expect(container.interactionManager.getSnapshot()).toBeNull();
    expect(ctx.api.deleteMessage).toHaveBeenCalledWith(1, 55);
    expect(ctx.reply).toHaveBeenCalledWith(t("create_project.permission_denied"));
  });

  it("cancels the name prompt", async () => {
    startPrompt();
    const ctx = createContext(undefined, "create_project:cancel");

    const handled = await handleCreateProjectCancel(ctx, container);

    expect(handled).toBe(true);
    expect(container.interactionManager.getSnapshot()).toBeNull();
    expect(ctx.deleteMessage).toHaveBeenCalled();
  });

  it("ignores text when no prompt is active", async () => {
    const ctx = createContext("demo-app");
    expect(await handleCreateProjectTextAnswer(ctx, container)).toBe(false);
    expect(mocks.mkdir).not.toHaveBeenCalled();
  });
});
