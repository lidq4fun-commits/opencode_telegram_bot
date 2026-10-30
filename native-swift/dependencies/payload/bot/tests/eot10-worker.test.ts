import assert from "node:assert/strict";
import { after, describe, it } from "node:test";

process.env.OPENCODE_TELEGRAM_EOT10_PASSWORD = "eot10-worker-test-password";

const crypto = await import("../EOT10Crypto.mjs");

describe("EOT10 media worker", () => {
  after(async () => {
    await crypto.stopEot10Workers();
  });

  it("encrypts and decrypts a large attachment off the main thread", async () => {
    const payload = Buffer.alloc(3 * 1024 * 1024, 7);
    const encrypted = await crypto.encryptEot10MediaBufferAsync(payload, {
      filename: "large.bin",
      mimeType: "application/octet-stream",
      kind: "document",
    });

    assert.equal(crypto.isEot10MediaBuffer(encrypted), true);

    const clear = await crypto.decryptEot10MediaBufferAsync(encrypted);
    assert.equal(clear.metadata.filename, "large.bin");
    assert.equal(clear.metadata.mimeType, "application/octet-stream");
    assert.equal(clear.buffer.length, payload.length);
    assert.equal(clear.buffer.equals(payload), true);
  });

  it("keeps the inline path for small attachments", async () => {
    const payload = Buffer.from("small-eot10-attachment");
    const encrypted = await crypto.encryptEot10MediaBufferAsync(payload, {
      filename: "small.txt",
      mimeType: "text/plain",
    });

    assert.equal(crypto.isEot10MediaBuffer(encrypted), true);

    const clear = await crypto.decryptEot10MediaBufferAsync(encrypted);
    assert.equal(clear.buffer.toString("utf8"), "small-eot10-attachment");
  });

  it("derives the session key asynchronously for the first message", async () => {
    await crypto.prepareEot10Key();
    const envelope = crypto.encryptEot10("hello");
    assert.equal(crypto.decryptEot10(envelope), "hello");
  });
});
