import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Context } from "grammy";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";

const flushPendingPromptMock = vi.hoisted(() => vi.fn());
vi.mock("../../../src/bot/handlers/prompt.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../../src/bot/handlers/prompt.js")>()),
  admitPromptToInbox: vi.fn(async () => ({ sessionId: "session-1", inboxId: "inbox-1" })),
}));

vi.mock("../../../src/bot/handlers/message-merger.js", () => ({
  flushPendingPrompt: flushPendingPromptMock,
  __resetMessageMergerForTests: vi.fn(),
}));

import {
  handleDocumentMessage,
  type DocumentHandlerDeps,
} from "../../../src/bot/handlers/document-handler.js";

import { t } from "../../../src/i18n/index.js";
import { MAX_QUEUED_MEDIA_BYTES, promptQueue } from "../../../src/app/managers/prompt-queue-manager.js";
import * as settingsStore from "../../../src/app/stores/settings-store.js";
import { initializePromptQueueDispatch } from "../../../src/bot/handlers/prompt-queue-dispatch.js";
import { createTestAppContainer } from "../../helpers/app-container.js";
import type { AppContainer } from "../../../src/app/bootstrap/app-container.js";

const DOCX_MIME = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

/** A minimal stored DOCX (one word/document.xml entry) for the extraction path. */
function makeDocxBuffer(text: string): Buffer {
  const nameBytes = Buffer.from("word/document.xml", "utf8");
  const xml = Buffer.from(
    `<w:document><w:body><w:p><w:r><w:t>${text}</w:t></w:r></w:p></w:body></w:document>`,
    "utf8",
  );
  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0);
  local.writeUInt16LE(20, 4);
  local.writeUInt32LE(xml.length, 18);
  local.writeUInt32LE(xml.length, 22);
  local.writeUInt16LE(nameBytes.length, 26);
  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0);
  central.writeUInt16LE(20, 4);
  central.writeUInt16LE(20, 6);
  central.writeUInt32LE(xml.length, 20);
  central.writeUInt32LE(xml.length, 24);
  central.writeUInt16LE(nameBytes.length, 28);
  const centralOffset = local.length + nameBytes.length + xml.length;
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);
  eocd.writeUInt16LE(1, 8);
  eocd.writeUInt16LE(1, 10);
  eocd.writeUInt32LE(central.length + nameBytes.length, 12);
  eocd.writeUInt32LE(centralOffset, 16);
  return Buffer.concat([local, nameBytes, xml, central, nameBytes, eocd]);
}

function createDocumentContext(overrides: Partial<Context["message"]> = {}): {
  ctx: Context;
  replyMock: ReturnType<typeof vi.fn>;
} {
  const replyMock = vi.fn().mockResolvedValue({ message_id: 101 });

  const ctx = {
    chat: { id: 777 },
    message: {
      document: {
        file_id: "doc-file-id",
        file_unique_id: "unique-id",
        file_name: "test.txt",
        mime_type: "text/plain",
        file_size: 1024,
      },
      caption: "",
      ...overrides,
    },
    reply: replyMock,
    api: {
      getFile: vi.fn().mockResolvedValue({
        file_path: "documents/test.txt",
        file_size: 1024,
      }),
    },
  } as unknown as Context;

  return { ctx, replyMock };
}

function createDocumentDeps(overrides: Partial<DocumentHandlerDeps> = {}): {
  deps: DocumentHandlerDeps;
  processPromptMock: ReturnType<typeof vi.fn>;
  downloadMock: ReturnType<typeof vi.fn>;
} {
  const processPromptMock = vi.fn().mockResolvedValue(true);
  const downloadMock = vi.fn().mockResolvedValue({
    buffer: Buffer.from("file content here"),
    filePath: "documents/test.txt",
  });

  const deps: DocumentHandlerDeps = {
    ...container,
    bot: {} as DocumentHandlerDeps["bot"],
    ensureEventSubscription: vi.fn().mockResolvedValue(undefined),
    downloadFile: downloadMock,
    processPrompt: (ctx, input, promptDeps) =>
      input.fileParts.length > 0
        ? processPromptMock(ctx, input.text, promptDeps, input.fileParts)
        : processPromptMock(ctx, input.text, promptDeps),
    ...overrides,
  };
  initializePromptQueueDispatch(deps);

  return { deps, processPromptMock, downloadMock };
}

let container: AppContainer;

beforeEach(() => {
  container = createTestAppContainer();
});

describe("bot/handlers/document", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    flushPendingPromptMock.mockClear();
    promptQueue.__resetForTests();
  });

  it("does not apply the removed local V1 media cap to V2 documents", async () => {
    vi.spyOn(settingsStore, "getPromptQueueMode").mockReturnValue("queue");
    container.foregroundSessionState.markBusy("session-1", "/repo");
    const { ctx } = createDocumentContext({
      document: {
        file_id: "image-file-id",
        file_unique_id: "image-unique-id",
        file_name: "image.png",
        mime_type: "image/png",
        file_size: MAX_QUEUED_MEDIA_BYTES + 1,
      },
    });
    const { deps, downloadMock, processPromptMock } = createDocumentDeps();

    await handleDocumentMessage(ctx, deps);

    expect(downloadMock).toHaveBeenCalledOnce();
    expect(processPromptMock).not.toHaveBeenCalled();
    expect(promptQueue.mediaSize()).toBe(0);
  });

  it("prepares V2 inbox documents with an unknown source size", async () => {
    vi.spyOn(settingsStore, "getPromptQueueMode").mockReturnValue("queue");
    container.foregroundSessionState.markBusy("session-1", "/repo");
    const { ctx } = createDocumentContext({
      document: {
        file_id: "unknown-file-id",
        file_unique_id: "unknown-unique-id",
        file_name: "unknown.png",
        mime_type: "image/png",
      },
    });
    const { deps, downloadMock } = createDocumentDeps();

    await handleDocumentMessage(ctx, deps);

    expect(downloadMock).toHaveBeenCalledOnce();
    expect(promptQueue.mediaSize()).toBe(0);
  });

  describe("text files", () => {
    it("keeps only an inbox mirror when a text file is queued", async () => {
      vi.spyOn(settingsStore, "getPromptQueueMode").mockReturnValue("queue");
      container.foregroundSessionState.markBusy("session-1", "/repo");
      const { ctx } = createDocumentContext();
      const { deps, downloadMock, processPromptMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(downloadMock).toHaveBeenCalledOnce();
      expect(processPromptMock).not.toHaveBeenCalled();
      expect(promptQueue.list()).toEqual([
        expect.objectContaining({
          inbox: { sessionId: "session-1", inboxId: "inbox-1", delivery: "queue" },
        }),
      ]);
      expect(promptQueue.mediaSize()).toBe(0);
    });

    it("downloads and sends text file content as prompt", async () => {
      const { ctx, replyMock } = createDocumentContext();
      const { deps, processPromptMock, downloadMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(flushPendingPromptMock).toHaveBeenCalledWith(777);
      expect(replyMock).toHaveBeenCalledWith(t("bot.file_downloading"));
      expect(downloadMock).toHaveBeenCalled();
      expect(processPromptMock).toHaveBeenCalledWith(
        ctx,
        "--- Content of test.txt ---\nfile content here\n--- End of file ---\n\n",
        deps,
      );
    });

    it("includes caption in prompt after file content", async () => {
      const { ctx } = createDocumentContext({ caption: "Please review this file" });
      const { deps, processPromptMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(processPromptMock).toHaveBeenCalledWith(
        ctx,
        expect.stringContaining("Please review this file"),
        deps,
      );
    });

    it("stores a DOCX in the project and points the prompt at it", async () => {
      const worktree = await fs.mkdtemp(path.join(os.tmpdir(), "docx-upload-"));
      const docx = makeDocxBuffer("项目周报内容");
      const projectSpy = vi
        .spyOn(settingsStore, "getCurrentProject")
        .mockReturnValue({ worktree } as ReturnType<typeof settingsStore.getCurrentProject>);
      const { ctx } = createDocumentContext({
        document: {
          file_id: "docx-file-id",
          file_unique_id: "docx-unique",
          file_name: "report.docx",
          mime_type: DOCX_MIME,
          file_size: docx.length,
        },
      });
      const { deps, processPromptMock } = createDocumentDeps({
        downloadFile: vi.fn().mockResolvedValue({ buffer: docx, filePath: "documents/report.docx" }),
      });

      await handleDocumentMessage(ctx, deps);

      expect(processPromptMock).toHaveBeenCalledWith(
        ctx,
        expect.stringContaining("Read this file with your tools before answering"),
        deps,
      );
      expect(processPromptMock.mock.calls[0]?.[1]).toContain(".opencode-telegram-attachments/");
      expect(processPromptMock.mock.calls[0]).toHaveLength(3);

      const storedDir = path.join(worktree, ".opencode-telegram-attachments");
      const stored = await fs.readdir(storedDir);
      expect(stored).toHaveLength(1);
      expect(stored[0]).toContain("report.docx");
      expect((await fs.readFile(path.join(storedDir, stored[0]!))).equals(docx)).toBe(true);
      projectSpy.mockRestore();
      await fs.rm(worktree, { recursive: true, force: true });
    });

    it("sends a DOCX as a file part when no project is active", async () => {
      const projectSpy = vi.spyOn(settingsStore, "getCurrentProject").mockReturnValue(undefined);
      const docx = makeDocxBuffer("no project");
      const { ctx } = createDocumentContext({
        document: {
          file_id: "docx-file-id",
          file_unique_id: "docx-unique",
          file_name: "report.docx",
          mime_type: DOCX_MIME,
          file_size: docx.length,
        },
      });
      const { deps, processPromptMock } = createDocumentDeps({
        downloadFile: vi.fn().mockResolvedValue({ buffer: docx, filePath: "documents/report.docx" }),
      });

      await handleDocumentMessage(ctx, deps);

      const fileParts = processPromptMock.mock.calls[0]?.[3] as Array<{ mime: string }> | undefined;
      expect(fileParts).toEqual([expect.objectContaining({ type: "file", mime: DOCX_MIME })]);
      projectSpy.mockRestore();
    });

    it("accepts application/json as text file", async () => {
      const { ctx, replyMock } = createDocumentContext({
        document: {
          file_id: "doc-file-id",
          file_unique_id: "unique-id",
          file_name: "config.json",
          mime_type: "application/json",
          file_size: 500,
        },
      });
      const { deps, processPromptMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(replyMock).toHaveBeenCalledWith(t("bot.file_downloading"));
      expect(processPromptMock).toHaveBeenCalled();
    });

    it("accepts application/xml as text file", async () => {
      const { ctx } = createDocumentContext({
        document: {
          file_id: "doc-file-id",
          file_unique_id: "unique-id",
          file_name: "data.xml",
          mime_type: "application/xml",
          file_size: 500,
        },
      });
      const { deps, processPromptMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(processPromptMock).toHaveBeenCalled();
    });

    it("accepts application/javascript as text file", async () => {
      const { ctx } = createDocumentContext({
        document: {
          file_id: "doc-file-id",
          file_unique_id: "unique-id",
          file_name: "script.js",
          mime_type: "application/javascript",
          file_size: 500,
        },
      });
      const { deps, processPromptMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(processPromptMock).toHaveBeenCalled();
    });

    it("treats unknown-extension valid UTF-8 content as text", async () => {
      const { ctx, replyMock } = createDocumentContext({
        document: {
          file_id: "doc-file-id",
          file_unique_id: "unique-id",
          file_name: "notes.plain",
          mime_type: "application/octet-stream",
          file_size: 500,
        },
      });
      const { deps, processPromptMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(replyMock).toHaveBeenCalledWith(t("bot.file_downloading"));
      expect(processPromptMock).toHaveBeenCalledWith(
        ctx,
        expect.stringContaining("--- Content of notes.plain ---"),
        deps,
      );
    });
  });

  describe("recognized binary types", () => {
    it("sends a PDF as a file part without model capability gating", async () => {
      const { ctx, replyMock } = createDocumentContext({
        document: {
          file_id: "pdf-file-id",
          file_unique_id: "pdf-unique-id",
          file_name: "document.pdf",
          mime_type: "application/pdf",
          file_size: 5000,
        },
      });
      const { deps, processPromptMock, downloadMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(replyMock).toHaveBeenCalledWith(t("bot.file_downloading"));
      expect(downloadMock).toHaveBeenCalled();
      expect(processPromptMock).toHaveBeenCalledWith(
        ctx,
        "",
        deps,
        expect.arrayContaining([
          expect.objectContaining({ type: "file", mime: "application/pdf" }),
        ]),
      );
    });

    it("sends an image document as a file part without model capability gating", async () => {
      const { ctx } = createDocumentContext({
        document: {
          file_id: "image-file-id",
          file_unique_id: "image-unique-id",
          file_name: "image.png",
          mime_type: "image/png",
          file_size: 5000,
        },
      });
      const { deps, processPromptMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(processPromptMock).toHaveBeenCalledWith(
        ctx,
        "",
        deps,
        expect.arrayContaining([
          expect.objectContaining({ type: "file", mime: "image/png" }),
        ]),
      );
    });

    it("infers a known MIME from the file extension when Telegram reports octet-stream", async () => {
      const { ctx } = createDocumentContext({
        document: {
          file_id: "zip-file-id",
          file_unique_id: "zip-unique-id",
          file_name: "bundle.zip",
          mime_type: "application/octet-stream",
          file_size: 5000,
        },
      });
      const { deps, processPromptMock } = createDocumentDeps();

      await handleDocumentMessage(ctx, deps);

      expect(processPromptMock).toHaveBeenCalledWith(
        ctx,
        "",
        deps,
        expect.arrayContaining([
          expect.objectContaining({ type: "file", mime: "application/zip" }),
        ]),
      );
    });
  });

  describe("unknown binary types", () => {
    it("stores the file inside the active project and references it in the prompt", async () => {
      const worktree = await fs.mkdtemp(path.join(os.tmpdir(), "eot10-doc-"));
      const projectSpy = vi
        .spyOn(settingsStore, "getCurrentProject")
        .mockReturnValue({ worktree } as ReturnType<typeof settingsStore.getCurrentProject>);
      const { ctx, replyMock } = createDocumentContext({
        document: {
          file_id: "bin-file-id",
          file_unique_id: "bin-unique-id",
          file_name: "payload.bin",
          mime_type: "application/octet-stream",
          file_size: 500,
        },
      });
      const binaryBuffer = Buffer.from([0x00, 0x01, 0x02, 0xff, 0xfe]);
      const { deps, processPromptMock, downloadMock } = createDocumentDeps({
        downloadFile: vi.fn().mockResolvedValue({
          buffer: binaryBuffer,
          filePath: "documents/payload.bin",
        }),
      } as Partial<DocumentHandlerDeps>);

      await handleDocumentMessage(ctx, deps);

      expect(replyMock).toHaveBeenCalledWith(t("bot.file_downloading"));
      expect(downloadMock).not.toHaveBeenCalled();
      expect(processPromptMock).toHaveBeenCalledWith(
        ctx,
        expect.stringContaining(".opencode-telegram-attachments/"),
        deps,
      );
      const storedDir = path.join(worktree, ".opencode-telegram-attachments");
      const stored = await fs.readdir(storedDir);
      expect(stored).toHaveLength(1);
      expect(stored[0]).toContain("payload.bin");
      const storedBytes = await fs.readFile(path.join(storedDir, stored[0]!));
      expect(storedBytes.equals(binaryBuffer)).toBe(true);
      projectSpy.mockRestore();
      await fs.rm(worktree, { recursive: true, force: true });
    });

    it("replies project_not_selected when no project is active", async () => {
      const projectSpy = vi
        .spyOn(settingsStore, "getCurrentProject")
        .mockReturnValue(undefined);
      const { ctx, replyMock } = createDocumentContext({
        document: {
          file_id: "bin-file-id",
          file_unique_id: "bin-unique-id",
          file_name: "payload.bin",
          mime_type: "application/octet-stream",
          file_size: 500,
        },
      });
      const binaryBuffer = Buffer.from([0x00, 0x01, 0x02, 0xff, 0xfe]);
      const { deps, processPromptMock } = createDocumentDeps({
        downloadFile: vi.fn().mockResolvedValue({
          buffer: binaryBuffer,
          filePath: "documents/payload.bin",
        }),
      } as Partial<DocumentHandlerDeps>);

      await handleDocumentMessage(ctx, deps);

      expect(replyMock).toHaveBeenCalledWith(t("bot.project_not_selected"));
      expect(processPromptMock).not.toHaveBeenCalled();
      projectSpy.mockRestore();
    });
  });
});
