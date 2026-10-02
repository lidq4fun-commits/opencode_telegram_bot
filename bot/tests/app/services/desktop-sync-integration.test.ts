import path from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Bot, Context } from "grammy";
import type { AppContainer } from "../../../src/app/bootstrap/app-container.js";
import type { ModelInfo } from "../../../src/app/types/model.js";
import type { ProjectInfo } from "../../../src/app/types/project.js";
import type { SessionInfo } from "../../../src/app/types/session.js";

const mocked = vi.hoisted(() => ({
  files: new Map<string, string>(),
  project: { id: "project", worktree: "/project", name: "Project" } as ProjectInfo,
  session: { id: "ses_phone", directory: "/project", title: "Phone" } as SessionInfo | null,
  model: { providerID: "provider", modelID: "phone", variant: "high" } as ModelInfo,
  busy: false,
  getSession: vi.fn(),
  getProject: vi.fn(),
  attach: vi.fn(),
  notify: vi.fn(),
}));
vi.mock("node:fs/promises", () => ({
  default: {
    mkdir: vi.fn(),
    readdir: async (root: string) =>
      [...mocked.files.keys()]
        .filter((key) => path.dirname(key) === root)
        .map((key) => path.basename(key)),
    readFile: async (file: string) => mocked.files.get(file),
    writeFile: async (file: string, value: string) => {
      mocked.files.set(file, value);
    },
    rename: async (from: string, to: string) => {
      mocked.files.set(to, mocked.files.get(from)!);
      mocked.files.delete(from);
    },
    unlink: async (file: string) => {
      mocked.files.delete(file);
    },
  },
}));
vi.mock("../../../src/runtime/paths.js", () => ({
  getRuntimePaths: () => ({ settingsFilePath: path.join("fixture", "settings.json") }),
}));
vi.mock("../../../src/app/stores/settings-store.js", () => ({
  getCurrentProject: () => mocked.project,
  setCurrentProject: (project: ProjectInfo) => {
    mocked.project = project;
  },
}));
vi.mock("../../../src/app/services/session-service.js", () => ({
  getCurrentSession: () => mocked.session,
  setCurrentSession: (session: SessionInfo) => {
    mocked.session = session;
  },
  clearSession: () => {
    mocked.session = null;
  },
}));
vi.mock("../../../src/app/services/model-selection-service.js", () => ({
  getStoredModel: () => mocked.model,
  selectModel: (model: ModelInfo) => {
    mocked.model = model;
  },
}));
vi.mock("../../../src/app/services/session-settings-service.js", () => ({
  applySessionSettings: vi.fn(),
}));
vi.mock("../../../src/app/services/project-service.js", () => ({
  getProjectByWorktree: mocked.getProject,
}));
vi.mock("../../../src/app/services/attach-service.js", () => ({ attachToSession: mocked.attach }));
vi.mock("../../../src/app/services/run-control-service.js", () => ({
  isForegroundBusy: () => mocked.busy,
}));
vi.mock("../../../src/opencode/client.js", () => ({
  opencodeClient: { session: { get: mocked.getSession } },
}));

import {
  beginDesktopMobileActivity,
  startDesktopSync,
  waitForDesktopSyncIdle,
} from "../../../src/app/services/desktop-sync-service.js";

const root = path.join("fixture", "desktop-sync");
const stateFile = path.join(root, "state.json");
type SyncState = {
  instance: string;
  mobileRevision: number;
  currentSession?: SessionInfo;
  currentModel: ModelInfo;
  acknowledgements: Array<{ id: string; reason: string | null }>;
};
let stop: (() => Promise<void>) | undefined;
const container = {
  resetInteractions: vi.fn(),
  resetAggregator: vi.fn(),
  ensureEventSubscription: vi.fn(),
  attachManager: { clear: vi.fn(), isAttachedSession: vi.fn(() => false) },
  keyboardManager: { updateModel: vi.fn(), getKeyboard: vi.fn(() => null) },
  pinnedMessageManager: { refresh: vi.fn(), refreshContextLimit: vi.fn() },
} as unknown as AppContainer;
const bot = { api: { sendMessage: mocked.notify } } as unknown as Bot<Context>;
function readState(): SyncState {
  return JSON.parse(mocked.files.get(stateFile)!);
}
function command(details: Record<string, unknown>) {
  const state = readState(),
    id = "100-000001-desktop";
  mocked.files.set(
    path.join(root, `command-${id}.json`),
    JSON.stringify({
      id,
      instance: state.instance,
      mobileRevision: state.mobileRevision,
      createdAt: Date.now(),
      kind: "selection",
      ...details,
    }),
  );
  return id;
}

beforeEach(async () => {
  vi.useFakeTimers();
  vi.stubEnv("OPENCODE_TELEGRAM_DESKTOP_SYNC", "1");
  vi.clearAllMocks();
  mocked.files.clear();
  mocked.busy = false;
  mocked.project = { id: "project", worktree: "/project", name: "Project" };
  mocked.session = { id: "ses_phone", directory: "/project", title: "Phone" };
  mocked.model = { providerID: "provider", modelID: "phone", variant: "high" };
  mocked.getSession.mockResolvedValue({
    data: { id: "ses_desktop", directory: "/other", title: "Desktop" },
  });
  mocked.getProject.mockResolvedValue({ id: "other", worktree: "/other", name: "Other" });
  stop = startDesktopSync(container, bot);
  await vi.advanceTimersByTimeAsync(0);
});
afterEach(async () => {
  await stop?.();
  vi.useRealTimers();
  vi.unstubAllEnvs();
});

describe("desktop synchronization single-writer integration", () => {
  it("switches project and attaches the desktop session, acknowledging without a mobile echo", async () => {
    const before = readState(),
      id = command({ sessionID: "ses_desktop" });
    await vi.advanceTimersByTimeAsync(500);
    expect(mocked.project.worktree).toBe("/other");
    expect(mocked.session?.id).toBe("ses_desktop");
    expect(mocked.attach).toHaveBeenCalledWith(
      expect.objectContaining({ session: expect.objectContaining({ id: "ses_desktop" }) }),
    );
    expect(readState().acknowledgements).toContainEqual({ id, reason: null });
    expect(readState().mobileRevision).toBe(before.mobileRevision);
    await vi.advanceTimersByTimeAsync(500);
    expect(readState().mobileRevision).toBe(before.mobileRevision);
  });
  it("clears the old conversation when choosing a new draft in the same project", async () => {
    command({ directory: "/project" });
    await vi.advanceTimersByTimeAsync(500);
    expect(mocked.session).toBeNull();
    expect(container.attachManager.clear).toHaveBeenCalled();
    expect(container.ensureEventSubscription).toHaveBeenCalledWith("/project");
  });
  it("adopts model and thinking strength and refreshes phone presentation", async () => {
    const model = { providerID: "provider", modelID: "desktop", variant: "low" };
    const id = command({ kind: "model", sessionID: "ses_phone", model });
    await vi.advanceTimersByTimeAsync(500);
    expect(readState().currentModel).toEqual(model);
    expect(readState().acknowledgements).toContainEqual({ id, reason: null });
    expect(mocked.notify).toHaveBeenCalled();
  });
  it("rejects a desktop selection if the phone acts during a session API read", async () => {
    let finish: (() => void) | undefined;
    mocked.getSession.mockImplementation(async () => {
      finish = beginDesktopMobileActivity();
      return { data: { id: "ses_desktop", directory: "/other", title: "Desktop" } };
    });
    const id = command({ sessionID: "ses_desktop" });
    await vi.advanceTimersByTimeAsync(500);
    expect(mocked.session?.id).toBe("ses_phone");
    expect(mocked.attach).not.toHaveBeenCalled();
    expect(readState().acknowledgements).toContainEqual({ id, reason: "mobile" });
    finish?.();
  });
  it("rejects switching while answering, then accepts switching after completion", async () => {
    mocked.busy = true;
    let id = command({ sessionID: "ses_desktop" });
    await vi.advanceTimersByTimeAsync(500);
    expect(readState().acknowledgements).toContainEqual({ id, reason: "busy" });
    expect(mocked.session?.id).toBe("ses_phone");
    mocked.busy = false;
    id = command({ sessionID: "ses_desktop" });
    await vi.advanceTimersByTimeAsync(500);
    expect(readState().acknowledgements.at(-1)).toEqual({ id, reason: null });
    expect(mocked.session?.id).toBe("ses_desktop");
  });
  it("does not treat automatic session renaming as a phone action", async () => {
    const before = readState();
    mocked.session!.title = "Automatic title";
    await vi.advanceTimersByTimeAsync(500);
    expect(readState().mobileRevision).toBe(before.mobileRevision);
  });
  it("holds phone mutations until an admitted desktop attachment finishes", async () => {
    let finishAttach: (() => void) | undefined;
    mocked.attach.mockImplementationOnce(
      () =>
        new Promise<void>((resolve) => {
          finishAttach = resolve;
        }),
    );
    command({ sessionID: "ses_desktop" });
    await vi.advanceTimersByTimeAsync(500);
    expect(mocked.session?.id).toBe("ses_desktop");
    const finishMobile = beginDesktopMobileActivity();
    let phoneReady = false;
    const waiting = waitForDesktopSyncIdle().then(() => {
      phoneReady = true;
    });
    await Promise.resolve();
    expect(phoneReady).toBe(false);
    finishAttach?.();
    await waiting;
    expect(phoneReady).toBe(true);
    finishMobile();
  });
  it("reattaches a saved current session before its first desktop prompt after restart", async () => {
    command({ kind: "prompt", sessionID: "ses_phone" });
    await vi.advanceTimersByTimeAsync(500);
    expect(mocked.getSession).not.toHaveBeenCalled();
    expect(mocked.attach).toHaveBeenCalledWith(
      expect.objectContaining({ session: expect.objectContaining({ id: "ses_phone" }) }),
    );
    expect(readState().acknowledgements.at(-1)?.reason).toBeNull();
  });
});
