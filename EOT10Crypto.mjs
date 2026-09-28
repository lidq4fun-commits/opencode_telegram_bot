import { createCipheriv, createDecipheriv, pbkdf2Sync, randomBytes } from "node:crypto";
import { readFile } from "node:fs/promises";
import { basename, extname } from "node:path";

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
  derivedKeys.set(keyId, key);
  while (derivedKeys.size > 128) {
    const oldestKeyId = derivedKeys.keys().next().value;
    derivedKeys.get(oldestKeyId)?.fill(0);
    derivedKeys.delete(oldestKeyId);
  }
  return key;
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
  if (Array.isArray(value)) return value.map(item => encryptRichText(item, salt, derivedKey));
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
  const buffer = encryptEot10MediaBuffer(fileBytes, { filename, mimeType, kind: mimeType.startsWith("image/") ? "photo" : "document" });
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
  const parts = [];
  for (let offset = 0; offset < plaintext.length; offset += maxPlaintextChars) {
    parts.push(encryptEot10(plaintext.slice(offset, offset + maxPlaintextChars)));
  }
  return parts;
}
