import { createCipheriv, createDecipheriv, pbkdf2Sync, randomBytes } from "node:crypto";

const PREFIX = "EOT10:";
const PASSWORD_ENV = "OPENCODE_TELEGRAM_EOT10_PASSWORD";
const PBKDF2_ITERATIONS = 250000;

function deriveKey(salt) {
  const password = process.env[PASSWORD_ENV];
  if (!password || password.length < 12) throw new Error(`${PASSWORD_ENV} must contain a password of at least 12 characters`);
  return pbkdf2Sync(password.normalize("NFKC"), salt, PBKDF2_ITERATIONS, 32, "sha256");
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
  const salt = randomBytes(16);
  const key = deriveKey(salt);
  try {
    return encryptEot10WithKey(plaintext, salt, key);
  } finally {
    key.fill(0);
  }
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

// Rich messages keep their block structure and visible buttons; one salt is shared
// across text leaves so clients only need one PBKDF2 derivation per rich message.
export function encryptEot10RichMessage(richMessage) {
  const salt = randomBytes(16);
  const key = deriveKey(salt);
  try {
    return encryptRichValue(richMessage, null, salt, key);
  } finally {
    key.fill(0);
  }
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
