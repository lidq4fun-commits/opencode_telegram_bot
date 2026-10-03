import { describe, expect, it } from "vitest";
import {
  desktopCommandBlockReason,
  validateDesktopSyncCommand,
  type DesktopSyncCommand,
} from "../../../src/app/services/desktop-sync-service.js";

const command: DesktopSyncCommand = {
  id: "123-000001-test",
  instance: "instance",
  mobileRevision: 3,
  createdAt: 100_000,
  kind: "selection",
  sessionID: "ses_desktop",
  directory: "/project",
};
const state = {
  instance: "instance",
  mobileRevision: 3,
  mobileActive: false,
  busy: false,
  sessionID: "ses_current",
};

describe("desktop synchronization arbitration", () => {
  it("accepts selections while idle", () => {
    expect(desktopCommandBlockReason(command, state, 100_000)).toBeNull();
  });
  it("blocks project/session switches during an answer but releases them afterward", () => {
    expect(desktopCommandBlockReason(command, { ...state, busy: true }, 100_000)).toBe("busy");
    expect(desktopCommandBlockReason(command, state, 100_000)).toBeNull();
  });
  it("allows prompts and next-turn model changes in the running shared session", () => {
    for (const kind of ["prompt", "model"] as const) {
      expect(
        desktopCommandBlockReason(
          { ...command, kind, sessionID: "ses_current" },
          { ...state, busy: true },
          100_000,
        ),
      ).toBeNull();
      expect(
        desktopCommandBlockReason({ ...command, kind }, { ...state, busy: true }, 100_000),
      ).toBe("busy");
    }
  });
  it("phone activity wins over a stale desktop selection", () => {
    expect(desktopCommandBlockReason(command, { ...state, mobileRevision: 4 }, 100_000)).toBe(
      "mobile",
    );
    expect(desktopCommandBlockReason(command, { ...state, mobileActive: true }, 100_000)).toBe(
      "mobile",
    );
  });
  it("never replays commands from an earlier bot process or old browser", () => {
    expect(desktopCommandBlockReason(command, { ...state, instance: "restarted" }, 100_000)).toBe(
      "stale",
    );
    expect(desktopCommandBlockReason(command, state, 131_000)).toBe("stale");
    expect(desktopCommandBlockReason(command, state, 90_000)).toBe("stale");
  });
  it("validates metadata without accepting arbitrary command kinds or filenames", () => {
    expect(validateDesktopSyncCommand(command)).toBe(true);
    expect(
      validateDesktopSyncCommand({
        ...command,
        model: { providerID: "openai", modelID: "model", variant: "high" },
      }),
    ).toBe(true);
    for (const value of [
      null,
      { ...command, id: "../settings" },
      { ...command, kind: "execute" },
      { ...command, sessionID: "arbitrary" },
      { ...command, directory: 1 },
      { ...command, model: null },
      { ...command, model: { providerID: "p", modelID: "m", variant: 5 } },
    ]) {
      expect(validateDesktopSyncCommand(value)).toBe(false);
    }
  });
});
