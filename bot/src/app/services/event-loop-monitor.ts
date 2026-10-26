import { monitorEventLoopDelay, type IntervalHistogram } from "node:perf_hooks";
import { logger } from "../../utils/logger.js";

const REPORT_INTERVAL_MS = 60_000;
const WARN_P95_MS = 150;
const WARN_COOLDOWN_MS = 30_000;

export interface EventLoopLagSnapshot {
  p50Ms: number;
  p95Ms: number;
  maxMs: number;
}

let histogram: IntervalHistogram | null = null;
let reportTimer: ReturnType<typeof setInterval> | null = null;
let lastWarnAt = 0;

function toMs(nanoseconds: number): number {
  if (!Number.isFinite(nanoseconds) || nanoseconds <= 0) {
    return 0;
  }

  return Math.round((nanoseconds / 1e6) * 10) / 10;
}

/** Turns raw histogram values (nanoseconds) into the shape logged and reported. */
export function summarizeEventLoopLag(
  p50Nanoseconds: number,
  p95Nanoseconds: number,
  maxNanoseconds: number,
): EventLoopLagSnapshot {
  return {
    p50Ms: toMs(p50Nanoseconds),
    p95Ms: toMs(p95Nanoseconds),
    maxMs: toMs(maxNanoseconds),
  };
}

/**
 * Current event loop delay percentiles. Empty (all zeros) until the first sample arrives.
 */
export function readEventLoopLag(): EventLoopLagSnapshot {
  if (!histogram) {
    return { p50Ms: 0, p95Ms: 0, maxMs: 0 };
  }

  return summarizeEventLoopLag(histogram.percentile(50), histogram.percentile(95), histogram.max);
}

/**
 * Watches event loop delay so blocking work is visible instead of merely felt: a warning when
 * the 95th percentile exceeds 150 ms, and one debug line per minute with the actual numbers.
 * @returns a stop function; calling it twice is safe
 */
export function startEventLoopMonitor(): () => void {
  if (histogram) {
    return stopEventLoopMonitor;
  }

  histogram = monitorEventLoopDelay({ resolution: 20 });
  histogram.enable();
  reportTimer = setInterval(() => {
    const snapshot = readEventLoopLag();
    if (snapshot.p95Ms >= WARN_P95_MS && Date.now() - lastWarnAt >= WARN_COOLDOWN_MS) {
      lastWarnAt = Date.now();
      logger.warn(
        `[Perf] Event loop lag p50=${snapshot.p50Ms}ms p95=${snapshot.p95Ms}ms max=${snapshot.maxMs}ms`,
      );
    } else {
      logger.debug(
        `[Perf] Event loop lag p50=${snapshot.p50Ms}ms p95=${snapshot.p95Ms}ms max=${snapshot.maxMs}ms`,
      );
    }
    histogram?.reset();
  }, REPORT_INTERVAL_MS);
  reportTimer.unref?.();

  return stopEventLoopMonitor;
}

export function stopEventLoopMonitor(): void {
  if (reportTimer) {
    clearInterval(reportTimer);
    reportTimer = null;
  }

  histogram?.disable();
  histogram = null;
}

export function __resetEventLoopMonitorForTests(): void {
  stopEventLoopMonitor();
  lastWarnAt = 0;
}
