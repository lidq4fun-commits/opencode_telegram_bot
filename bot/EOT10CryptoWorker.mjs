import { parentPort } from "node:worker_threads";
import { decryptEot10MediaBuffer, encryptEot10MediaBuffer } from "./EOT10Crypto.mjs";

// Runs the AES-GCM work for large attachments off the Bot's main thread. The worker reads the
// same EOT10 password from the inherited environment and answers one job at a time.
if (!parentPort) {
  throw new Error("EOT10CryptoWorker must run as a worker thread");
}

parentPort.on("message", ({ id, kind, bytes, metadata }) => {
  try {
    const input = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (kind === "encrypt") {
      const encrypted = encryptEot10MediaBuffer(input, metadata || {});
      const transferable = new Uint8Array(encrypted);
      parentPort.postMessage({ id, result: transferable }, [transferable.buffer]);
      return;
    }

    const clear = decryptEot10MediaBuffer(input);
    const buffer = new Uint8Array(clear.buffer);
    parentPort.postMessage(
      { id, result: { buffer, metadata: clear.metadata } },
      [buffer.buffer],
    );
  } catch (error) {
    parentPort.postMessage({ id, error: error instanceof Error ? error.message : String(error) });
  }
});
