import { describe, expect, it } from "vitest";
import { createOpencodeServeSpawnCommand, getOpencodeApiVersion } from "../../src/opencode/process.js";
import { getWizardServerVersionDefault } from "../../src/runtime/bootstrap.js";

describe("V2-only runtime", () => {
  it("rejects starting a V1 server", () => {
    expect(() => createOpencodeServeSpawnCommand({ port: 49374, host: "127.0.0.1" }, "v1")).toThrow("Only OpenCode V2");
  });

  it("does not classify legacy releases as supported", () => {
    expect(getOpencodeApiVersion("1.18.34")).toBeNull();
    expect(getOpencodeApiVersion("2.0.22")).toBe("v2");
  });

  it("uses V2 for both new and previously configured setups", () => {
    expect(getWizardServerVersionDefault(null)).toBe("v2");
    expect(getWizardServerVersionDefault("OPENCODE_SERVER_VERSION=v1")).toBe("v2");
  });
});
