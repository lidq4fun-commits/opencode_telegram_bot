import fs from "node:fs/promises";
import path from "node:path";
import { randomUUID } from "node:crypto";
import type { Bot, Context } from "grammy";
import type { AppContainer } from "../bootstrap/app-container.js";
import type { ModelInfo } from "../types/model.js";
import { config } from "../../config.js";
import { getRuntimePaths } from "../../runtime/paths.js";
import { opencodeClient } from "../../opencode/client.js";
import { getCurrentProject, setCurrentProject } from "../stores/settings-store.js";
import { clearSession, getCurrentSession, setCurrentSession } from "./session-service.js";
import { getStoredModel, selectModel } from "./model-selection-service.js";
import { applySessionSettings } from "./session-settings-service.js";
import { getProjectByWorktree } from "./project-service.js";
import { attachToSession } from "./attach-service.js";
import { isForegroundBusy } from "./run-control-service.js";
import { logger } from "../../utils/logger.js";
import { t } from "../../i18n/index.js";
import { safeBackgroundTask } from "../../utils/safe-background-task.js";

let mobileRevision = 0;
let mobileOperations = 0;
let desktopOperation: Promise<void> | undefined;

/** Phone mutations run after an already admitted desktop attach has finished. */
export async function waitForDesktopSyncIdle(): Promise<void> {
  await desktopOperation;
}

/** Called only after authorization and stale-update filtering. */
export function beginDesktopMobileActivity(): () => void {
  mobileRevision += 1;
  mobileOperations += 1;
  return () => {
    mobileOperations -= 1;
    mobileRevision += 1;
  };
}

export interface DesktopSyncCommand {
  id: string;
  instance: string;
  mobileRevision: number;
  createdAt: number;
  kind: "selection" | "model" | "prompt";
  directory?: string;
  sessionID?: string;
  model?: ModelInfo;
}

export function validateDesktopSyncCommand(value: unknown): value is DesktopSyncCommand {
  if (!value || typeof value !== "object") return false;
  const item = value as DesktopSyncCommand;
  return (
    typeof item.id === "string" &&
    /^[a-zA-Z0-9-]{1,80}$/.test(item.id) &&
    typeof item.instance === "string" &&
    Number.isSafeInteger(item.mobileRevision) &&
    Number.isFinite(item.createdAt) &&
    ["selection", "model", "prompt"].includes(item.kind) &&
    (item.kind !== "model" || item.model !== undefined) &&
    (item.directory === undefined ||
      (typeof item.directory === "string" && item.directory.length > 0)) &&
    (item.sessionID === undefined ||
      (typeof item.sessionID === "string" && /^ses_[\w-]+$/.test(item.sessionID))) &&
    (item.model === undefined ||
      (item.model !== null &&
        typeof item.model === "object" &&
        typeof item.model.providerID === "string" &&
        item.model.providerID.length > 0 &&
        typeof item.model.modelID === "string" &&
        item.model.modelID.length > 0 &&
        (item.model.variant === undefined || typeof item.model.variant === "string")))
  );
}

export function desktopCommandBlockReason(
  command: DesktopSyncCommand,
  state: {
    instance: string;
    mobileRevision: number;
    mobileActive: boolean;
    busy: boolean;
    sessionID?: string | undefined;
  },
  now = Date.now(),
): "stale" | "mobile" | "busy" | null {
  if (
    command.instance !== state.instance ||
    now - command.createdAt > 30_000 ||
    command.createdAt > now + 5_000
  )
    return "stale";
  if (state.mobileActive || command.mobileRevision !== state.mobileRevision) return "mobile";
  if (state.busy && (command.kind === "selection" || command.sessionID !== state.sessionID))
    return "busy";
  return null;
}

/** Single writer for bot state. The browser never edits settings.json. */
export function startDesktopSync(container: AppContainer, bot: Bot<Context>): () => Promise<void> {
  if (process.env.OPENCODE_TELEGRAM_DESKTOP_SYNC !== "1") return async () => {};
  const root = path.join(path.dirname(getRuntimePaths().settingsFilePath), "desktop-sync");
  const instance = randomUUID();
  let stopped = false;
  let running: Promise<void> | undefined;
  const acknowledgements: Array<{ id: string; reason: string | null }> = [];

  const context = () => ({
    currentProject: getCurrentProject(),
    currentSession: getCurrentSession() ?? undefined,
    currentModel: getStoredModel(),
  });

  async function apply(command: DesktopSyncCommand): Promise<void> {
    const before = JSON.stringify(context());
    const current = getCurrentSession();
    let session;
    if (command.sessionID && command.sessionID !== current?.id) {
      const result = await opencodeClient.session.get({
        sessionID: command.sessionID,
        ...(command.directory ? { directory: command.directory } : {}),
      });
      if (result.error || !result.data || result.data.parentID)
        throw new Error("Cannot follow desktop session");
      session = result.data;
    }
    const directory = session?.directory ?? command.directory ?? current?.directory;
    const project =
      directory && directory !== getCurrentProject()?.worktree
        ? await getProjectByWorktree(directory)
        : undefined;
    // A mobile operation or a running turn may have started during API reads.
    const reason = desktopCommandBlockReason(command, state());
    if (reason) throw new Error(reason);
    if (project) setCurrentProject(project);
    if (session) {
      container.resetInteractions("desktop_session_switch");
      setCurrentSession({ id: session.id, title: session.title, directory: session.directory });
      applySessionSettings(session);
      if (command.model) {
        selectModel({ ...command.model, variant: command.model.variant || "default" });
      }
    } else if (
      command.kind === "selection" &&
      !command.sessionID &&
      directory &&
      (project || current)
    ) {
      container.resetInteractions("desktop_project_switch");
      container.attachManager.clear("desktop_project_switch");
      container.resetAggregator();
      clearSession();
      await container.ensureEventSubscription(directory);
    }
    if (command.model && !session) {
      selectModel({ ...command.model, variant: command.model.variant || "default" });
    }
    const selectedSession = getCurrentSession();
    if (
      command.sessionID &&
      selectedSession &&
      !container.attachManager.isAttachedSession(selectedSession.id, selectedSession.directory)
    ) {
      await attachToSession({
        ...container,
        bot,
        chatId: config.telegram.allowedUserId,
        session: selectedSession,
        ensureEventSubscription: container.ensureEventSubscription,
      });
    }
    container.keyboardManager.updateModel(getStoredModel());
    if (before !== JSON.stringify(context())) {
      const presentationContext = JSON.stringify(context());
      const project = getCurrentProject(),
        selected = getCurrentSession(),
        picked = getStoredModel();
      const text = [
        t("status.project_selected", { project: project?.name || project?.worktree || "" }),
        selected
          ? t("status.session_selected", { title: selected.title })
          : t("status.session_not_selected"),
        t("model.changed_message", { name: `${picked.providerID}/${picked.modelID}` }),
        t("variant.changed_message", { name: picked.variant || "default" }),
      ].join("\n");
      const keyboard = container.keyboardManager.getKeyboard();
      safeBackgroundTask({
        taskName: "desktop.sync.presentation",
        task: async () => {
          await container.pinnedMessageManager.refreshContextLimit();
          await container.pinnedMessageManager.refresh();
          if (presentationContext !== JSON.stringify(context())) return;
          await bot.api.sendMessage(
            config.telegram.allowedUserId,
            text,
            keyboard ? { reply_markup: keyboard } : {},
          );
        },
      });
    }
  }

  const state = () => ({
    instance,
    mobileRevision,
    mobileActive: mobileOperations > 0,
    busy: isForegroundBusy(container),
    sessionID: getCurrentSession()?.id,
  });

  async function tick(): Promise<void> {
    await fs.mkdir(root, { recursive: true });
    const names = (await fs.readdir(root))
      .filter((name) => /^command-[\w-]+\.json$/.test(name))
      .sort();
    for (const name of names) {
      const file = path.join(root, name);
      let id = name.slice(8, -5);
      let reason: string | null = "invalid";
      try {
        const value: unknown = JSON.parse(await fs.readFile(file, "utf8"));
        if (validateDesktopSyncCommand(value)) {
          id = value.id;
          reason = desktopCommandBlockReason(value, state());
          if (!reason) {
            await apply(value);
          }
        }
      } catch (error) {
        const message = error instanceof Error ? error.message : "";
        reason = ["mobile", "busy", "stale"].includes(message) ? message : "unavailable";
        logger.warn("[DesktopSync] Could not apply desktop action", { id, reason });
      }
      acknowledgements.push({ id, reason });
      if (acknowledgements.length > 64) acknowledgements.shift();
      await fs.unlink(file);
    }
    const output = JSON.stringify({
      ...context(),
      ...state(),
      acknowledgements,
      updatedAt: Date.now(),
    });
    // A fresh timestamp lets the desktop detect a stopped/disconnected bot.
    await fs.writeFile(path.join(root, "state.tmp"), output);
    await fs.rename(path.join(root, "state.tmp"), path.join(root, "state.json"));
  }
  const run = () => {
    if (stopped || running) return;
    running = tick()
      .catch((error: unknown) => logger.warn("[DesktopSync] State refresh failed", error))
      .finally(() => {
        running = undefined;
        desktopOperation = undefined;
      });
    desktopOperation = running;
  };
  const timer = setInterval(run, 500);
  timer.unref();
  run();
  return async () => {
    stopped = true;
    clearInterval(timer);
    await running;
  };
}
