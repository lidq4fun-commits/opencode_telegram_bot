import { describe, expect, it } from "vitest";
import { decryptEot10, encryptEot10Parts } from "../EOT10Crypto.mjs";

describe("EOT10 long text delivery", () => {
  it.each(["Conclusion ".repeat(5000), "超长结论中文测试。".repeat(5000), "中文😀🚀abc".repeat(5000)])(
    "keeps every encrypted envelope within Telegram limits and preserves the full text",
    (text) => {
      process.env.OPENCODE_TELEGRAM_EOT10_PASSWORD = "long-text-test-password";
      const parts = encryptEot10Parts(text);
      expect(parts.length).toBeGreaterThan(1);
      expect(parts.every((part) => part.length <= 4096)).toBe(true);
      expect(parts.map(decryptEot10).join("")).toBe(text);
    },
  );
});
