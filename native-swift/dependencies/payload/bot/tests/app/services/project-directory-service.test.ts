import { afterEach, describe, expect, it } from "vitest";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { config } from "../../../src/config.js";
import {
  ensureProjectDirectory,
  isProjectDirectoryMissing,
  ProjectDirectoryMissingError,
} from "../../../src/app/services/project-directory-service.js";

const roots: string[] = [];
const originalApiUrl = config.opencode.apiUrl;
afterEach(async () => {
  config.opencode.apiUrl = originalApiUrl;
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

describe("project directory availability", () => {
  it("detects removed directories and files while accepting existing folders", async () => {
    config.opencode.apiUrl = "http://127.0.0.1:4096";
    const root = await mkdtemp(path.join(tmpdir(), "project-directory-"));
    roots.push(root);
    expect(await isProjectDirectoryMissing(root)).toBe(false);
    const file = path.join(root, "file");
    await writeFile(file, "source");
    expect(await isProjectDirectoryMissing(file)).toBe(true);
    await expect(ensureProjectDirectory(path.join(root, "removed"))).rejects.toBeInstanceOf(
      ProjectDirectoryMissingError,
    );
  });
  it.each(["localhost", "[::1]"])("checks loopback server %s", async (host) => {
    config.opencode.apiUrl = `http://${host}:4096`;
    expect(await isProjectDirectoryMissing(path.join(tmpdir(), "nonexistent-project-123"))).toBe(
      true,
    );
  });
  it("does not reject remote server directories missing on the bot machine", async () => {
    config.opencode.apiUrl = "https://remote.example";
    await expect(ensureProjectDirectory("/remote/project")).resolves.toBeUndefined();
  });
});
