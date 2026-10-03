import { config, type OpencodeServerVersion } from "../config.js";
import {
  createV2OpencodeClient,
  findRegisteredV2ServerUrl,
  type V2ClientExtension,
} from "./v2/client.js";

const PROBE_TIMEOUT_MS = 5000;

const getAuth = () => {
  if (!config.opencode.password) {
    return undefined;
  }
  const credentials = `${config.opencode.username}:${config.opencode.password}`;
  return `Basic ${Buffer.from(credentials).toString("base64")}`;
};

const authHeaders = (): Record<string, string> | undefined => {
  const auth = getAuth();
  return auth ? { Authorization: auth } : undefined;
};

/** The API version the bot was configured for; there is no detection or switching. */
export const opencodeServerVersion: OpencodeServerVersion = "v2";

function createClient() {
  const headers = authHeaders();
  return createV2OpencodeClient({
    baseUrl: config.opencode.apiUrl,
    ...(headers ? { headers } : {}),
  });
}

export const opencodeClient = createClient();

/** The V2-only operations of the same client; use only when `opencodeServerVersion` is "v2". */
export const opencodeV2Client = opencodeClient as unknown as V2ClientExtension;

export type OpencodeServerProbe =
  | { kind: "found"; version: OpencodeServerVersion; serverVersion: string }
  | { kind: "unauthorized" }
  | { kind: "none" };

function readServerVersion(body: unknown): string | null {
  if (typeof body !== "object" || body === null) {
    return null;
  }
  const record = body as Record<string, unknown>;
  if (typeof record.version !== "string") {
    return null;
  }
  return typeof record.pid === "number" ? record.version : null;
}

/**
 * Asks the configured URL whether it answers the given API version, with the configured
 * credentials. Used to explain a failed health check, never to switch clients.
 */
export async function probeOpencodeServer(
  version: OpencodeServerVersion,
): Promise<OpencodeServerProbe> {
  if (version !== "v2") {
    return { kind: "none" };
  }
  const baseUrl = config.opencode.apiUrl.endsWith("/")
    ? config.opencode.apiUrl
    : `${config.opencode.apiUrl}/`;
  try {
    const headers = authHeaders();
    const response = await fetch(new URL("api/info", baseUrl), {
      ...(headers ? { headers } : {}),
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
    });
    if (response.status === 401) {
      return { kind: "unauthorized" };
    }
    const contentType = response.headers.get("content-type") ?? "";
    if (!response.ok || !contentType.includes("json")) {
      return { kind: "none" };
    }
    const serverVersion = readServerVersion(await response.json());
    return serverVersion ? { kind: "found", version, serverVersion } : { kind: "none" };
  } catch {
    return { kind: "none" };
  }
}

/** URL of the registered OpenCode V2 background server, or null when none answers. */
export async function findRegisteredOpencodeServerUrl(): Promise<string | null> {
  return findRegisteredV2ServerUrl();
}
