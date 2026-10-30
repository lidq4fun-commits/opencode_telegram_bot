import { stat } from "node:fs/promises";
import { config } from "../../config.js";
import { t } from "../../i18n/index.js";

/** Remote server paths cannot be checked against the bot's filesystem. */
export async function isProjectDirectoryMissing(directory: string): Promise<boolean> {
  const host = new URL(config.opencode.apiUrl).hostname;
  if (!["localhost", "127.0.0.1", "[::1]"].includes(host)) return false;
  try {
    return !(await stat(directory)).isDirectory();
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === "ENOENT" || code === "ENOTDIR") return true;
    throw error;
  }
}

export class ProjectDirectoryMissingError extends Error {
  constructor(directory: string) {
    super(t("bot.project_directory_missing", { directory }));
    this.name = "ProjectDirectoryMissingError";
  }
}

export async function ensureProjectDirectory(directory: string): Promise<void> {
  if (await isProjectDirectoryMissing(directory)) {
    throw new ProjectDirectoryMissingError(directory);
  }
}
