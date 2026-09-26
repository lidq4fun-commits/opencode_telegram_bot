import { createCipheriv, createDecipheriv, pbkdf2Sync, randomBytes } from "node:crypto";

const PREFIX = "EOT10:";
const PASSWORD_ENV = "OPENCODE_TELEGRAM_EOT10_PASSWORD";
const PBKDF2_ITERATIONS = 250000;

function deriveKey(salt) {
  const password = process.env[PASSWORD_ENV];
  if (!password || password.length < 12) throw new Error(`${PASSWORD_ENV} must contain a password of at least 12 characters`);
  return pbkdf2Sync(password.normalize("NFKC"), salt, PBKDF2_ITERATIONS, 32, "sha256");
}

export function encryptEot10(plaintext) {
  const iv = randomBytes(12);
  const salt = randomBytes(16);
  const cipher = createCipheriv("aes-256-gcm", deriveKey(salt), iv);
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
