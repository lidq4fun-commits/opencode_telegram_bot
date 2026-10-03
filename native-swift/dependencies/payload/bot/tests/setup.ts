import { beforeEach, afterEach, vi } from "vitest";
import { ensureTestEnvironment } from "./helpers/test-environment.js";
import { resetSingletonState } from "./helpers/reset-singleton-state.js";
import { resetRuntimeLocale } from "../src/i18n/index.js";

ensureTestEnvironment();

beforeEach(() => {
  resetRuntimeLocale();
  ensureTestEnvironment();
  return resetSingletonState();
});

afterEach(() => {
  resetRuntimeLocale();
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  ensureTestEnvironment();
  return resetSingletonState();
});
