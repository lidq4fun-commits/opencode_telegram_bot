import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  __resetPromptModelRetryForTests,
  forgetPromptForModelRetry,
  getRetryablePrompt,
  isModelUnavailableMessage,
  MODEL_UNAVAILABLE_RETRY_DELAYS_MS,
  rememberPromptForModelRetry,
  retryPromptAfterModelUnavailable,
} from "../../../src/app/services/prompt-model-retry.js";

const REQUEST = {
  sessionID: "ses_test",
  directory: "E:\\project",
  parts: [{ type: "text" as const, text: "hello" }],
  model: { providerID: "opencode-go", modelID: "deepseek-v4.1-flash" },
  agent: "build",
};

function harness(responses: Array<{ error?: unknown }>) {
  let index = 0;
  const waits: number[] = [];
  const attempts: Array<[number, number]> = [];
  const accepted: unknown[] = [];

  return {
    waits,
    attempts,
    accepted,
    handlers: {
      wait: async (milliseconds: number) => {
        waits.push(milliseconds);
      },
      send: async () => responses[Math.min(index++, responses.length - 1)] ?? {},
      onRetryAttempt: async (attempt: number, total: number) => {
        attempts.push([attempt, total]);
      },
      onRetryAccepted: async (request: unknown) => {
        accepted.push(request);
      },
    },
  };
}

describe("app/services/prompt-model-retry", () => {
  beforeEach(() => {
    __resetPromptModelRetryForTests();
    vi.useRealTimers();
  });

  afterEach(() => {
    __resetPromptModelRetryForTests();
    vi.useRealTimers();
  });

  it("recognises the engine's model-unavailable error only", () => {
    expect(isModelUnavailableMessage("Model unavailable: opencode-go/deepseek-v4.1-flash")).toBe(true);
    expect(isModelUnavailableMessage("  model unavailable: opencode-go/x")).toBe(true);
    expect(isModelUnavailableMessage("Model not found: opencode-go/x")).toBe(false);
    expect(isModelUnavailableMessage("Context length exceeded")).toBe(false);
  });

  it("re-sends the remembered prompt until the engine accepts it", async () => {
    rememberPromptForModelRetry(REQUEST);
    const run = harness([{ error: new Error("still unavailable") }, {}]);

    await expect(retryPromptAfterModelUnavailable("ses_test", run.handlers)).resolves.toBe(true);

    expect(run.waits).toEqual([
      MODEL_UNAVAILABLE_RETRY_DELAYS_MS[0],
      MODEL_UNAVAILABLE_RETRY_DELAYS_MS[1],
    ]);
    expect(run.attempts).toEqual([
      [1, 3],
      [2, 3],
    ]);
    expect(run.accepted).toEqual([REQUEST]);
    expect(getRetryablePrompt("ses_test")).toBeUndefined();
  });

  it("gives up after the last attempt and clears the snapshot", async () => {
    rememberPromptForModelRetry(REQUEST);
    const run = harness([{ error: new Error("unavailable") }]);

    await expect(retryPromptAfterModelUnavailable("ses_test", run.handlers)).resolves.toBe(false);

    expect(run.attempts).toEqual([
      [1, 3],
      [2, 3],
      [3, 3],
    ]);
    expect(run.waits).toEqual([...MODEL_UNAVAILABLE_RETRY_DELAYS_MS]);
    expect(run.accepted).toEqual([]);
    expect(getRetryablePrompt("ses_test")).toBeUndefined();
  });

  it("does nothing without a remembered prompt", async () => {
    const run = harness([{}]);

    await expect(retryPromptAfterModelUnavailable("ses_test", run.handlers)).resolves.toBe(false);

    expect(run.waits).toEqual([]);
  });

  it("ignores a prompt remembered too long ago", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-01-01T00:00:00.000Z"));
    rememberPromptForModelRetry(REQUEST);
    vi.setSystemTime(new Date("2026-01-01T00:10:00.000Z"));
    const run = harness([{}]);

    await expect(retryPromptAfterModelUnavailable("ses_test", run.handlers)).resolves.toBe(false);

    expect(run.waits).toEqual([]);
    expect(getRetryablePrompt("ses_test")).toBeUndefined();
  });

  it("forgets a prompt when asked", () => {
    rememberPromptForModelRetry(REQUEST);
    expect(getRetryablePrompt("ses_test")).toEqual(REQUEST);

    forgetPromptForModelRetry("ses_test");

    expect(getRetryablePrompt("ses_test")).toBeUndefined();
  });

  it("keeps only the newest prompt per session", () => {
    rememberPromptForModelRetry(REQUEST);
    rememberPromptForModelRetry({ ...REQUEST, parts: [{ type: "text", text: "second" }] });

    expect(getRetryablePrompt("ses_test")?.parts).toEqual([{ type: "text", text: "second" }]);
  });
});
