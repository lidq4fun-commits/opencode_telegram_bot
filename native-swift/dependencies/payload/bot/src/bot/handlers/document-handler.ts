import type { Context } from "grammy";
import fs from "node:fs/promises";
import path from "node:path";
import { getCurrentProject } from "../../app/stores/settings-store.js";
import { processUserPrompt, type ProcessPromptDeps } from "./prompt.js";
import {
  downloadTelegramFile,
  toDataUri,
  isTextMimeType,
} from "../../app/services/file-download-service.js";
import { logger } from "../../utils/logger.js";
import { t } from "../../i18n/index.js";
import type { FilePartInput } from "@opencode-ai/sdk/v2";
import { flushPendingPrompt } from "./message-merger.js";
import { createIncomingPrompt, type IncomingPrompt } from "../../app/types/prompt.js";
import {
  rejectQueuedMediaBeforePreparation,
  tryEnqueuePromptIfBusy,
} from "./prompt-queue-dispatch.js";

export interface DocumentHandlerDeps extends ProcessPromptDeps {
  downloadFile?: (
    api: Context["api"],
    fileId: string,
  ) => Promise<{ buffer: Buffer; filePath: string }>;
  processPrompt?: (
    ctx: Context,
    input: IncomingPrompt,
    deps: ProcessPromptDeps,
  ) => Promise<boolean>;
}

export async function handleDocumentMessage(
  ctx: Context,
  deps: DocumentHandlerDeps,
): Promise<void> {
  const downloadFile = deps.downloadFile ?? downloadTelegramFile;
  const processPrompt = deps.processPrompt ?? processUserPrompt;

  const doc = ctx.message?.document;
  if (!doc) {
    return;
  }

  flushPendingPrompt(ctx.chat!.id);

  const caption = ctx.message.caption || "";
  const mimeType = doc.mime_type || "";
  const filename = doc.file_name || "document";
  const submitPrompt = async (
    text: string,
    fileParts: FilePartInput[] = [],
    mediaBytes: number | undefined = 0,
  ): Promise<void> => {
    const input = createIncomingPrompt(text, { fileParts });
    if (
      await tryEnqueuePromptIfBusy(ctx, {
        ...input,
        displayText: caption.trim() || filename,
        mediaBytes,
      })
    ) {
      return;
    }
    await processPrompt(ctx, input, deps);
  };

  try {
    // Hybrid attachment routing: text-like content is submitted as prompt text,
    // recognized binary types go to OpenCode as file parts, and anything the
    // OpenCode attachment path cannot classify is stored inside the active
    // project so the agent can inspect it with local tools.
    await ctx.reply(t("bot.file_downloading"));
    if (await rejectQueuedMediaBeforePreparation(ctx, doc.file_size)) {
      return;
    }
    const genericAttachmentFile = await downloadFile(ctx.api, doc.file_id);

    const extensionMime: Record<string, string> = {
      pdf: "application/pdf", png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg",
      gif: "image/gif", webp: "image/webp", bmp: "image/bmp", tif: "image/tiff", tiff: "image/tiff",
      mp3: "audio/mpeg", wav: "audio/wav", m4a: "audio/mp4", ogg: "audio/ogg",
      mp4: "video/mp4", mov: "video/quicktime", webm: "video/webm",
      txt: "text/plain", md: "text/markdown", csv: "text/csv", json: "application/json",
      xml: "application/xml", html: "text/html", htm: "text/html", js: "text/javascript",
      ts: "text/typescript", py: "text/x-python", c: "text/x-c", h: "text/x-c",
      cpp: "text/x-c++", java: "text/x-java", log: "text/plain",
      doc: "application/msword",
      docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      rtf: "application/rtf", odt: "application/vnd.oasis.opendocument.text",
      ppt: "application/vnd.ms-powerpoint",
      pptx: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
      xls: "application/vnd.ms-excel",
      xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      zip: "application/zip", gz: "application/gzip", tar: "application/x-tar",
      rar: "application/vnd.rar", "7z": "application/x-7z-compressed",
    };
    const attachmentMime =
      !mimeType || mimeType === "application/octet-stream"
        ? extensionMime[path.extname(filename).slice(1).toLowerCase()] || "application/octet-stream"
        : mimeType;

    let detectedText: string | null = null;
    if (attachmentMime === "application/octet-stream" && genericAttachmentFile.buffer.length > 0) {
      try {
        const decoded = new TextDecoder("utf-8", { fatal: true }).decode(genericAttachmentFile.buffer);
        if (!/[\x00-\x08\x0b\x0c\x0e-\x1f]/.test(decoded)) {
          detectedText = decoded;
        }
      } catch {
        // Invalid UTF-8 stays a binary attachment.
      }
    }

    if (detectedText !== null || isTextMimeType(attachmentMime, filename)) {
      const textContent =
        detectedText !== null ? detectedText : genericAttachmentFile.buffer.toString("utf-8");
      const promptWithFile = `--- Content of ${filename} ---\n${textContent}\n--- End of file ---\n\n${caption}`;
      logger.info(
        `[Document] Sending text file (${genericAttachmentFile.buffer.length} bytes, ${filename}) as prompt`,
      );
      await submitPrompt(promptWithFile, [], doc.file_size);
      return;
    }

    if (attachmentMime !== "application/octet-stream") {
      const attachmentDataUri = toDataUri(genericAttachmentFile.buffer, attachmentMime);
      const filePart: FilePartInput = {
        type: "file",
        mime: attachmentMime,
        filename: filename,
        url: attachmentDataUri,
      };
      logger.info(
        `[Document] Sending file (${genericAttachmentFile.buffer.length} bytes, ${filename}, ${attachmentMime}) with prompt`,
      );
      await submitPrompt(caption, [filePart], doc.file_size);
      return;
    }

    const activeProject = getCurrentProject();
    if (!activeProject?.worktree) {
      await ctx.reply(t("bot.project_not_selected"));
      return;
    }
    const attachmentDirectory = path.join(activeProject.worktree, ".opencode-telegram-attachments");
    await fs.mkdir(attachmentDirectory, { recursive: true });
    const safeFilename =
      path.basename(filename).replace(/[<>:"\\/:*?\x00-\x1f]/g, "_") || "document";
    const storedFilename = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}-${safeFilename}`;
    const storedPath = path.join(attachmentDirectory, storedFilename);
    await fs.writeFile(storedPath, genericAttachmentFile.buffer);
    const relativePath = path.relative(activeProject.worktree, storedPath).split(path.sep).join("/");
    const attachmentPrompt = `${caption.trim()}\n\nTelegram attachment stored verbatim in the active project: ${relativePath}`;
    logger.info(`[Document] Stored unknown binary attachment locally: ${relativePath}`);
    await submitPrompt(attachmentPrompt.trim(), [], doc.file_size);
    return;
  } catch (err) {
    logger.error("[Document] Error handling document message:", err);
    await ctx.reply(t("bot.file_download_error"));
  }
}
