import { createCipheriv, createDecipheriv, pbkdf2, pbkdf2Sync, randomBytes } from "node:crypto";
import { readFile } from "node:fs/promises";
import { basename, extname } from "node:path";
import { Worker } from "node:worker_threads";

const PREFIX = "EOT10:";
const PASSWORD_ENV = "OPENCODE_TELEGRAM_EOT10_PASSWORD";
const PBKDF2_ITERATIONS = 250000;
const MEDIA_MAGIC = Buffer.from("EOT10M01", "ascii");
const MEDIA_HEADER_LENGTH = MEDIA_MAGIC.length + 16 + 12;
// One fresh session salt per Bot process lets both endpoints reuse the PBKDF2
// result across messages; every envelope still gets its own AES-GCM nonce.
const SESSION_SALT = randomBytes(16);
const derivedKeys = new Map();
const decryptedMediaFiles = new Map();

function deriveKey(salt) {
  const keyId = salt.toString("base64url");
  const cached = derivedKeys.get(keyId);
  if (cached) return cached;
  const password = process.env[PASSWORD_ENV];
  if (!password || password.length < 12) throw new Error(`${PASSWORD_ENV} must contain a password of at least 12 characters`);
  const key = pbkdf2Sync(password.normalize("NFKC"), salt, PBKDF2_ITERATIONS, 32, "sha256");
  rememberKey(keyId, key);
  return key;
}

function rememberKey(keyId, key) {
  derivedKeys.set(keyId, key);
  while (derivedKeys.size > 128) {
    const oldestKeyId = derivedKeys.keys().next().value;
    derivedKeys.get(oldestKeyId)?.fill(0);
    derivedKeys.delete(oldestKeyId);
  }
}

/**
 * Derives the session key on the libuv thread pool (a second core) instead of blocking the
 * event loop for ~100 ms. Call once at startup; `deriveKey` then finds the cached key.
 */
export function prepareEot10Key() {
  const salt = SESSION_SALT;
  const keyId = salt.toString("base64url");
  if (derivedKeys.has(keyId)) return Promise.resolve();
  const password = process.env[PASSWORD_ENV];
  if (!password || password.length < 12) {
    return Promise.reject(new Error(`${PASSWORD_ENV} must contain a password of at least 12 characters`));
  }
  return new Promise((resolve, reject) => {
    pbkdf2(password.normalize("NFKC"), salt, PBKDF2_ITERATIONS, 32, "sha256", (error, key) => {
      if (error) { reject(error); return; }
      if (!derivedKeys.has(keyId)) rememberKey(keyId, key);
      resolve();
    });
  });
}

function encryptEot10WithKey(plaintext, salt, key) {
  const iv = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", key, iv);
  const ciphertext = Buffer.concat([cipher.update(plaintext, "utf8"), cipher.final()]);
  const envelope = {
    v: 10,
    alg: "A256GCM",
    kdf: "PBKDF2-SHA256",
    iterations: PBKDF2_ITERATIONS,
    salt: salt.toString("base64url"),
    iv: iv.toString("base64url"),
    tag: cipher.getAuthTag().toString("base64url"),
    data: ciphertext.toString("base64url"),
  };
  return PREFIX + Buffer.from(JSON.stringify(envelope), "utf8").toString("base64url");
}

export function encryptEot10(plaintext) {
  const salt = SESSION_SALT;
  const key = deriveKey(salt);
  return encryptEot10WithKey(plaintext, salt, key);
}

const richTextKeys = new Set(["text", "caption", "credit", "summary", "title", "expression"]);

function encryptRichText(value, salt, derivedKey) {
  if (typeof value === "string") return value.length > 0 ? encryptEot10WithKey(value, salt, derivedKey) : value;
  if (Array.isArray(value)) {
    // Telegram coalesces adjacent string leaves into one TL text field. Merge
    // plaintext first so that field contains one complete authenticated envelope.
    const merged = [];
    for (const item of value) {
      if (typeof item === "string" && typeof merged[merged.length - 1] === "string") {
        merged[merged.length - 1] += item;
      } else {
        merged.push(item);
      }
    }
    return merged.map(item => encryptRichText(item, salt, derivedKey));
  }
  if (!value || typeof value !== "object") return value;
  if (value.type === "button" || value.type === "buttons") return value;
  return Object.fromEntries(Object.entries(value).map(([fieldName, item]) => [
    fieldName,
    richTextKeys.has(fieldName) ? encryptRichText(item, salt, derivedKey) : encryptRichValue(item, fieldName, salt, derivedKey),
  ]));
}

function encryptRichValue(value, key, salt, derivedKey) {
  if (typeof value === "string") return value;
  if (Array.isArray(value)) return value.map(item => encryptRichValue(item, key, salt, derivedKey));
  if (!value || typeof value !== "object") return value;
  if (value.type === "button" || value.type === "buttons") return value;
  return Object.fromEntries(Object.entries(value).map(([childKey, item]) => [
    childKey,
    richTextKeys.has(childKey) ? encryptRichText(item, salt, derivedKey) : encryptRichValue(item, childKey, salt, derivedKey),
  ]));
}

// Rich messages keep their block structure and visible buttons; the Bot-process
// salt is shared across text leaves so clients derive the session key only once.
export function encryptEot10RichMessage(richMessage) {
  const salt = SESSION_SALT;
  const key = deriveKey(salt);
  return encryptRichValue(richMessage, null, salt, key);
}

export async function encryptEot10MediaInput(input) {
  const source = input?.fileData;
  let fileBytes;
  if (Buffer.isBuffer(source) || source instanceof Uint8Array) {
    fileBytes = Buffer.from(source);
  } else if (typeof source === "string") {
    // GrammY InputFile uses strings for local paths and file IDs. Only local paths
    // may be encrypted; server file IDs cannot be forwarded into EOT10.
    if (!source.startsWith(".") && !/^[A-Za-z]:[\\/]/.test(source) && !source.startsWith("/")) {
      throw new Error("EOT10 requires a local media file, not a Telegram file_id");
    }
    fileBytes = await readFile(source);
  } else if (source && source[Symbol.asyncIterator]) {
    const chunks = [];
    for await (const chunk of source) chunks.push(Buffer.from(chunk));
    fileBytes = Buffer.concat(chunks);
  } else {
    throw new Error("Unsupported EOT10 media upload source");
  }
  const filename = input?.filename || (typeof source === "string" ? basename(source) : "attachment");
  const extension = extname(filename).toLowerCase();
  const mimeType = ({
    ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png", ".webp": "image/webp", ".gif": "image/gif",
    ".pdf": "application/pdf", ".txt": "text/plain", ".md": "text/markdown", ".doc": "application/msword",
    ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  })[extension] || "application/octet-stream";
  const buffer = await encryptEot10MediaBufferAsync(fileBytes, { filename, mimeType, kind: mimeType.startsWith("image/") ? "photo" : "document" });
  fileBytes.fill(0);
  return { buffer, filename: `EOT10-${randomBytes(8).toString("hex")}.eotm` };
}

export function cacheDecryptedEot10Media(fileId, media) {
  const key = String(fileId);
  decryptedMediaFiles.set(key, media);
  const timer = setTimeout(() => {
    if (decryptedMediaFiles.get(key) === media) decryptedMediaFiles.delete(key);
  }, 120000);
  timer.unref?.();
}

export function takeDecryptedEot10Media(fileId) {
  const key = String(fileId);
  const media = decryptedMediaFiles.get(key);
  if (media) decryptedMediaFiles.delete(key);
  return media;
}

export function encryptEot10MediaBuffer(fileBytes, metadata = {}) {
  const salt = SESSION_SALT;
  const key = deriveKey(salt);
  const iv = randomBytes(12);
  const meta = Buffer.from(JSON.stringify({
    v: 1,
    filename: typeof metadata.filename === "string" ? metadata.filename : "attachment",
    mimeType: typeof metadata.mimeType === "string" ? metadata.mimeType : "application/octet-stream",
    kind: typeof metadata.kind === "string" ? metadata.kind : "document",
  }), "utf8");
  if (meta.length > 65535) throw new Error("EOT10 media metadata too large");
  const length = Buffer.allocUnsafe(4);
  length.writeUInt32BE(meta.length, 0);
  const cipher = createCipheriv("aes-256-gcm", key, iv);
  const ciphertext = Buffer.concat([cipher.update(length), cipher.update(meta), cipher.update(Buffer.from(fileBytes)), cipher.final()]);
  return Buffer.concat([MEDIA_MAGIC, salt, iv, ciphertext, cipher.getAuthTag()]);
}

export function isEot10MediaBuffer(value) {
  return Buffer.isBuffer(value) && value.length >= MEDIA_HEADER_LENGTH + 20 && value.subarray(0, MEDIA_MAGIC.length).equals(MEDIA_MAGIC);
}

// AES-GCM runs on the main thread for small files. A large attachment is handed to one
// worker thread so Telegram polling and SSE handling stay responsive while it is encrypted.
const MEDIA_WORKER_THRESHOLD_BYTES = 2 * 1024 * 1024;
let mediaWorker = null;
let mediaJobSequence = 0;
const mediaJobs = new Map();

function failMediaJobs(error) {
  for (const job of mediaJobs.values()) job.reject(error);
  mediaJobs.clear();
}

function getMediaWorker() {
  if (mediaWorker) return mediaWorker;
  mediaWorker = new Worker(new URL("./EOT10CryptoWorker.mjs", import.meta.url));
  mediaWorker.unref();
  mediaWorker.on("message", (message) => {
    const job = mediaJobs.get(message.id);
    if (!job) return;
    mediaJobs.delete(message.id);
    if (message.error) job.reject(new Error(message.error));
    else job.resolve(message.result);
    if (mediaJobs.size === 0) mediaWorker?.unref();
  });
  mediaWorker.on("error", (error) => failMediaJobs(error));
  mediaWorker.on("exit", (code) => {
    mediaWorker = null;
    if (code !== 0) failMediaJobs(new Error(`EOT10 media worker exited with code ${code}`));
  });
  return mediaWorker;
}

function runMediaJob(kind, bytes, metadata) {
  return new Promise((resolve, reject) => {
    let worker;
    try {
      worker = getMediaWorker();
    } catch (error) {
      reject(error);
      return;
    }
    const id = ++mediaJobSequence;
    mediaJobs.set(id, { resolve, reject });
    worker.ref();
    const copy = new Uint8Array(bytes);
    worker.postMessage({ id, kind, bytes: copy, metadata }, [copy.buffer]);
  });
}

function toBuffer(value) {
  if (Buffer.isBuffer(value)) return value;
  if (value instanceof Uint8Array) return Buffer.from(value.buffer, value.byteOffset, value.byteLength);
  return Buffer.from(value);
}

export async function encryptEot10MediaBufferAsync(fileBytes, metadata = {}) {
  const input = toBuffer(fileBytes);
  if (input.length < MEDIA_WORKER_THRESHOLD_BYTES) return encryptEot10MediaBuffer(input, metadata);
  const result = await runMediaJob("encrypt", input, metadata);
  return toBuffer(result);
}

export async function decryptEot10MediaBufferAsync(value) {
  const packed = toBuffer(value);
  if (packed.length < MEDIA_WORKER_THRESHOLD_BYTES) return decryptEot10MediaBuffer(packed);
  const result = await runMediaJob("decrypt", packed, null);
  return { buffer: toBuffer(result.buffer), metadata: result.metadata };
}

/** Stops the media worker; the Bot calls this on shutdown so no thread outlives it. */
export async function stopEot10Workers() {
  if (!mediaWorker) return;
  const worker = mediaWorker;
  mediaWorker = null;
  failMediaJobs(new Error("EOT10 media worker stopped"));
  await worker.terminate();
}

export function isEot10MediaDocument(document) {
  return typeof document?.file_name === "string" && document.file_name.toLowerCase().endsWith(".eotm");
}

export function decryptEot10MediaBuffer(value) {
  const packed = Buffer.from(value);
  if (!isEot10MediaBuffer(packed)) throw new Error("File is not an EOT10 media envelope");
  const offset = MEDIA_MAGIC.length;
  const salt = packed.subarray(offset, offset + 16);
  const iv = packed.subarray(offset + 16, offset + 28);
  const tag = packed.subarray(packed.length - 16);
  const data = packed.subarray(offset + 28, packed.length - 16);
  const decipher = createDecipheriv("aes-256-gcm", deriveKey(salt), iv);
  decipher.setAuthTag(tag);
  const clear = Buffer.concat([decipher.update(data), decipher.final()]);
  if (clear.length < 4) throw new Error("Invalid EOT10 media payload");
  const metadataLength = clear.readUInt32BE(0);
  if (metadataLength < 2 || metadataLength > 65535 || metadataLength + 4 > clear.length) throw new Error("Invalid EOT10 media metadata");
  let metadata;
  try {
    metadata = JSON.parse(clear.subarray(4, metadataLength + 4).toString("utf8"));
  } catch {
    throw new Error("Malformed EOT10 media metadata");
  }
  if (metadata?.v !== 1 || typeof metadata.filename !== "string" || typeof metadata.mimeType !== "string") throw new Error("Unsupported EOT10 media metadata");
  return { buffer: Buffer.from(clear.subarray(metadataLength + 4)), metadata };
}

export function decryptEot10(value) {
  if (typeof value !== "string" || !value.startsWith(PREFIX)) {
    throw new Error("Message is not an EOT10 encrypted message");
  }
  let envelope;
  try {
    envelope = JSON.parse(Buffer.from(value.slice(PREFIX.length), "base64url").toString("utf8"));
  } catch {
    throw new Error("Malformed EOT10 envelope");
  }
  if (envelope?.v !== 10 || envelope?.alg !== "A256GCM" || envelope?.kdf !== "PBKDF2-SHA256" ||
      envelope?.iterations !== PBKDF2_ITERATIONS || typeof envelope.salt !== "string" ||
      typeof envelope.iv !== "string" || typeof envelope.tag !== "string" || typeof envelope.data !== "string") {
    throw new Error("Unsupported EOT10 envelope");
  }
  const salt = Buffer.from(envelope.salt, "base64url");
  const iv = Buffer.from(envelope.iv, "base64url");
  const tag = Buffer.from(envelope.tag, "base64url");
  const data = Buffer.from(envelope.data, "base64url");
  if (salt.length !== 16 || iv.length !== 12 || tag.length !== 16 || data.length === 0) throw new Error("Invalid EOT10 envelope lengths");
  const decipher = createDecipheriv("aes-256-gcm", deriveKey(salt), iv);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(data), decipher.final()]).toString("utf8");
}

export function isEot10Message(value) {
  return typeof value === "string" && value.startsWith(PREFIX);
}

// Keep encrypted envelopes below Telegram's 4096-character message ceiling.
export function encryptEot10Parts(plaintext, maxPlaintextChars = 2850) {
  if (!Number.isInteger(maxPlaintextChars) || maxPlaintextChars < 1) throw new Error("Invalid EOT10 chunk size");
  const parts = [];
  const overhead = encryptEot10("").length;
  // Both the ciphertext and JSON envelope are base64 encoded.
  const maxBytes = Math.floor((4096 - overhead - 4) * 9 / 16);
  let chunk = "";
  let bytes = 0;
  for (const character of plaintext) {
    const size = Buffer.byteLength(character, "utf8");
    if (chunk && (bytes + size > maxBytes || chunk.length + character.length > maxPlaintextChars)) {
      parts.push(encryptEot10(chunk));
      chunk = "";
      bytes = 0;
    }
    chunk += character;
    bytes += size;
  }
  if (chunk) parts.push(encryptEot10(chunk));
  return parts;
}
