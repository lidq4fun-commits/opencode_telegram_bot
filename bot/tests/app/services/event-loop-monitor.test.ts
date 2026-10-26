import { afterEach, describe, expect, it } from "vitest";
import {
  __resetEventLoopMonitorForTests,
  readEventLoopLag,
  startEventLoopMonitor,
  stopEventLoopMonitor,
  summarizeEventLoopLag,
} from "../../../src/app/services/event-loop-monitor.js";

describe("app/services/event-loop-monitor", () => {
  afterEach(() => {
    __resetEventLoopMonitorForTests();
  });

  it("converts histogram nanoseconds to milliseconds with one decimal", () => {
    expect(summarizeEventLoopLag(1_000_000, 152_400_000, 320_000_000)).toEqual({
      p50Ms: 1,
      p95Ms: 152.4,
      maxMs: 320,
    });
  });

  it("treats missing samples as zero instead of NaN", () => {
    expect(summarizeEventLoopLag(Number.NaN, 0, -1)).toEqual({
      p50Ms: 0,
      p95Ms: 0,
      maxMs: 0,
    });
  });

  it("reports zeros before the monitor starts", () => {
    expect(readEventLoopLag()).toEqual({ p50Ms: 0, p95Ms: 0, maxMs: 0 });
  });

  it("starts once and stops safely when called twice", () => {
    const stopFirst = startEventLoopMonitor();
    const stopSecond = startEventLoopMonitor();

    expect(typeof stopFirst).toBe("function");
    stopFirst();
    stopSecond();
    stopEventLoopMonitor();

    expect(readEventLoopLag()).toEqual({ p50Ms: 0, p95Ms: 0, maxMs: 0 });
  });
});
