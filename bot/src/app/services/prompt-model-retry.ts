import { logger } from "../../utils/logger.js";

/** Waits before each re-send: a provider-config fetch failure usually clears within seconds. */
export const MODEL_UNAVAILABLE_RETRY_DELAYS_MS = [2000, 5000, 10000] as const;
const SNAPSHOT_TTL_MS = 5 * 60 * 1000;
const MAX_REMEMBERED_SESSIONS = 8;
const SESSION_ERROR_LEAD = /^\s*model unavailable\b/i;

export interface RetryablePromptRequest {
  sessionID: string;
  directory: string;
  parts: unknown[];
  model?: { providerID: string; modelID: string };
  agent?: string;
  variant?: string;
}

interface PromptSnapshot {
  request: RetryablePromptRequest;
  storedAt: number;
  attempts: number;
  running: boolean;
}

const snapshots = new Map<string, PromptSnapshot>();

/** A failed provider-config fetch surfaces as "Model unavailable: provider/model". */
export function isModelUnavailableMessage(message: string): boolean {
  return SESSION_ERROR_LEAD.test(message);
}

/** Keeps the prompt that started the current run so it can be sent again once. */
export function rememberPromptForModelRetry(request: RetryablePromptRequest): void {
  snapshots.delete(request.sessionID);
  snapshots.set(request.sessionID, { request, storedAt: Date.now(), attempts: 0, running: false });
  while (snapshots.size > MAX_REMEMBERED_SESSIONS) {
    const oldestSessionId = snapshots.keys().next().value;
    if (oldestSessionId === undefined) {
      break;
    }
    snapshots.delete(oldestSessionId);
  }
}

export function forgetPromptForModelRetry(sessionID: string): void {
  snapshots.delete(sessionID);
}

export function getRetryablePrompt(sessionID: string): RetryablePromptRequest | undefined {
  return snapshots.get(sessionID)?.request;
}

export interface ModelUnavailableRetryHandlers {
  send: (request: RetryablePromptRequest) => Promise<{ error?: unknown }>;
  onRetryAccepted: (request: RetryablePromptRequest) => Promise<void> | void;
  onRetryAttempt?: (attempt: number, total: number) => Promise<void> | void;
  wait?: (milliseconds: number) => Promise<void>;
}

/**
 * Re-sends the prompt of a run that failed with "Model unavailable" (the engine could not
 * refresh a provider config). Returns true when a retry was accepted, so the caller can keep
 * the run alive instead of reporting the error.
 */
export async function retryPromptAfterModelUnavailable(
  sessionID: string,
  handlers: ModelUnavailableRetryHandlers,
): Promise<boolean> {
  const snapshot = snapshots.get(sessionID);
  if (!snapshot || snapshot.running) {
    return false;
  }

  if (Date.now() - snapshot.storedAt > SNAPSHOT_TTL_MS) {
    snapshots.delete(sessionID);
    return false;
  }

  snapshot.running = true;
  const total = MODEL_UNAVAILABLE_RETRY_DELAYS_MS.length;
  const wait =
    handlers.wait ??
    ((milliseconds: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, milliseconds)));

  try {
    while (snapshot.attempts < total) {
      const attempt = snapshot.attempts + 1;
      snapshot.attempts = attempt;
      await wait(MODEL_UNAVAILABLE_RETRY_DELAYS_MS[attempt - 1] ?? 0);
      await handlers.onRetryAttempt?.(attempt, total);

      try {
        const result = await handlers.send(snapshot.request);
        if (!result.error) {
          snapshots.delete(sessionID);
          await handlers.onRetryAccepted(snapshot.request);
          logger.info(
            `[Bot] Re-sent the prompt after a transient model outage: session=${sessionID}, attempt=${attempt}`,
          );
          return true;
        }

        logger.warn(
          `[Bot] Prompt retry was rejected again: session=${sessionID}, attempt=${attempt}`,
        );
      } catch (error) {
        logger.warn(`[Bot] Prompt retry failed: session=${sessionID}, attempt=${attempt}`, error);
      }
    }

    snapshots.delete(sessionID);
    return false;
  } finally {
    const current = snapshots.get(sessionID);
    if (current) {
      current.running = false;
    }
  }
}

export function __resetPromptModelRetryForTests(): void {
  snapshots.clear();
}
