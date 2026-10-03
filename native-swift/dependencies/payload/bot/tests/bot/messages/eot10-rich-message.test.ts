import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { decryptEot10, encryptEot10RichMessage } from "../../../EOT10Crypto.mjs";

interface RichNode {
  type: string;
  text?: string | Array<string | RichNode>;
}

function encryptBlocks(blocks: RichNode[]): RichNode[] {
  return (encryptEot10RichMessage({ blocks }) as { blocks: RichNode[] }).blocks;
}

describe("EOT10 rich message text boundaries", () => {
  beforeAll(() => {
    vi.stubEnv("OPENCODE_TELEGRAM_EOT10_PASSWORD", "eot10-regression-passphrase");
  });

  afterAll(() => {
    vi.unstubAllEnvs();
  });

  it("encrypts adjacent list markers and text as one authenticated envelope", () => {
    const text = ["1. ", "First item", "\n2. ", "Second item"];
    const encrypted = encryptBlocks([{ type: "paragraph", text }]);
    const leaves = encrypted[0]?.text as string[];

    // Telegram coalesces adjacent string leaves into one TL text field.
    expect(decryptEot10(leaves.join(""))).toBe(text.join(""));
    expect(leaves).toHaveLength(1);
    expect(text).toEqual(["1. ", "First item", "\n2. ", "Second item"]);
  });

  it("preserves styled boundaries and merges adjacent strings inside styles", () => {
    const encrypted = encryptBlocks([
      {
        type: "paragraph",
        text: ["Before ", "bold: ", { type: "bold", text: ["hello", " world"] }, " after", "."],
      },
    ]);
    const leaves = encrypted[0]?.text as Array<string | RichNode>;
    expect(leaves).toHaveLength(3);
    expect(decryptEot10(leaves[0] as string)).toBe("Before bold: ");
    const bold = leaves[1] as RichNode;
    expect(bold.type).toBe("bold");
    expect(decryptEot10((bold.text as string[]).join(""))).toBe("hello world");
    expect(decryptEot10(leaves[2] as string)).toBe(" after.");
  });

  it("keeps separate blocks and button labels unchanged", () => {
    const encrypted = encryptBlocks([
      { type: "paragraph", text: "First" },
      { type: "paragraph", text: "Second" },
      { type: "button", text: "Visible action" },
    ]);
    expect(decryptEot10(encrypted[0]?.text as string)).toBe("First");
    expect(decryptEot10(encrypted[1]?.text as string)).toBe("Second");
    expect(encrypted[2]).toEqual({ type: "button", text: "Visible action" });
  });
});
