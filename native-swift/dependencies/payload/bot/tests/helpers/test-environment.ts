import fs from "node:fs";
import os from "node:os";
import path from "node:path";

function getDefaultTestHome(): string {
  const workerId = process.env.VITEST_WORKER_ID || "0";
  const preferredPath = path.join(process.cwd(), ".tmp", "test-home", `${process.pid}-${workerId}`);

  try {
    fs.mkdirSync(preferredPath, { recursive: true });
    return preferredPath;
  } catch {
    const fallbackPath = path.join(
      os.tmpdir(),
      "opencode-telegram-bot",
      "test-home",
      `${process.pid}-${workerId}`,
    );
    fs.mkdirSync(fallbackPath, { recursive: true });
    return fallbackPath;
  }
}

const TEST_ENV_DEFAULTS: Record<string, string> = {
  TELEGRAM_BOT_TOKEN: "test-telegram-token",
  TELEGRAM_ALLOWED_USER_ID: "123456789",
  OPENCODE_API_URL: "http://localhost:4096",
  OPENCODE_MODEL_PROVIDER: "test-provider",
  OPENCODE_MODEL_ID: "test-model",
  LOG_LEVEL: "error",
  LOG_RETENTION: "10",
  OPENCODE_TELEGRAM_HOME: getDefaultTestHome(),
};

export function ensureTestEnvironment(): void {
  // Desktop launch settings must not leak into isolated test workers.
  process.env.BOT_LOCALE = "en";
  process.env.OPENCODE_TELEGRAM_DESKTOP_SYNC = "0";
  process.env.OPENCODE_EXECUTABLE_PATH = "";
  for (const [key, value] of Object.entries(TEST_ENV_DEFAULTS)) {
    if (!process.env[key]) {
      process.env[key] = value;
    }
  }
}
