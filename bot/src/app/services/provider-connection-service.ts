import type {
  IntegrationAttempt,
  IntegrationAttemptStatus,
  IntegrationInfo,
} from "@opencode/client";
import { opencodeV2Client } from "../../opencode/client.js";
import { logger } from "../../utils/logger.js";

export interface ConnectMethod {
  id: string;
  type: "oauth" | "key";
  label: string;
  /** True when the flow can finish on a phone (device code); false needs the PC browser. */
  mobile?: boolean;
  answer?: Record<string, string | number | boolean | ReadonlyArray<string>>;
}

function isMobileFriendlyOauth(method: { id?: string; label?: string }): boolean {
  const probe = `${method.id ?? ""} ${method.label ?? ""}`.toLowerCase();
  return probe.includes("device") || probe.includes("headless");
}

export interface ConnectIntegration {
  id: string;
  name: string;
  connected: boolean;
  methods: ConnectMethod[];
  /** Provider names (for example "OpenCode Go") that use this integration. */
  providers?: string[];
}

type FormAnswer = Record<string, string | number | boolean | ReadonlyArray<string>>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function conditionsMet(when: unknown, answer: FormAnswer): boolean {
  if (!Array.isArray(when)) {
    return true;
  }

  return when.every((condition) => {
    if (!isRecord(condition) || typeof condition.key !== "string") {
      return false;
    }
    return condition.op === "eq" && answer[condition.key] === condition.value;
  });
}

/**
 * OAuth methods may declare answers the server needs before the login starts. Hidden fields
 * carry defaults; required choices fall back to their first option. A required free-text
 * answer is not supported from the bot, so that method is left out instead of failing late.
 */
function buildMethodAnswer(form: unknown): FormAnswer | null {
  if (!Array.isArray(form)) {
    return {};
  }

  const answer: FormAnswer = {};
  const pending: unknown[] = [...form];

  for (let index = 0; index < pending.length; index += 1) {
    const field = pending[index];
    if (!isRecord(field) || typeof field.key !== "string") {
      continue;
    }
    if (field.when !== undefined && !conditionsMet(field.when, answer)) {
      continue;
    }

    const key = field.key;
    const required = field.required === true;
    const hidden = field.hidden === true;

    if (field.default !== undefined && typeof field.default !== "object") {
      answer[key] = field.default as string | number | boolean;
      continue;
    }

    if (!required && !hidden) {
      continue;
    }

    const options = field.options;
    if (Array.isArray(options) && options.length > 0) {
      const first = options[0];
      if (isRecord(first) && typeof first.value === "string") {
        answer[key] = first.value;
        continue;
      }
    }

    if (required) {
      return null;
    }
  }

  return answer;
}

function toConnectIntegration(
  integration: IntegrationInfo,
  providers: string[],
): ConnectIntegration | null {
  const methods: ConnectMethod[] = [];
  let keyIndex = 0;

  for (const method of integration.methods) {
    if (method.type === "oauth") {
      if (!method.id) {
        continue;
      }

      const answer = buildMethodAnswer(method.form);
      if (answer === null) {
        logger.debug(
          `[ProviderConnect] Skipping OAuth method with unsupported form: ${integration.id}/${method.id}`,
        );
        continue;
      }

      methods.push({
        id: method.id,
        type: "oauth",
        label: method.label || method.id,
        mobile: isMobileFriendlyOauth(method),
        ...(Object.keys(answer).length > 0 ? { answer } : {}),
      });
      continue;
    }

    if (method.type !== "key") {
      continue;
    }

    const answer = buildMethodAnswer(method.form);
    if (answer === null) {
      logger.debug(
        `[ProviderConnect] Skipping key method with unsupported form: ${integration.id}`,
      );
      continue;
    }

    keyIndex += 1;
    methods.push({
      id: keyIndex === 1 ? "key" : `key${keyIndex}`,
      type: "key",
      label: method.label || "",
      ...(Object.keys(answer).length > 0 ? { answer } : {}),
    });
  }

  if (methods.length === 0) {
    return null;
  }

  // Device-code methods first: those are the ones that work from the phone.
  methods.sort((left, right) => Number(right.mobile === true) - Number(left.mobile === true));

  return {
    id: integration.id,
    name: integration.name || integration.id,
    connected: integration.connections.some((connection) => "method" in connection),
    methods,
    ...(providers.length > 0 ? { providers } : {}),
  };
}

export async function listConnectableIntegrations(): Promise<ConnectIntegration[]> {
  const [integrations, providers] = await Promise.all([
    opencodeV2Client.integration.list(),
    opencodeV2Client.integration.listProviders(),
  ]);

  if (integrations.error || !integrations.data) {
    throw integrations.error || new Error("No integrations received from server");
  }

  const providersByIntegration = new Map<string, string[]>();
  for (const provider of providers.data ?? []) {
    if (!provider.integrationID) {
      continue;
    }
    const names = providersByIntegration.get(provider.integrationID) ?? [];
    names.push(provider.name);
    providersByIntegration.set(provider.integrationID, names);
  }

  return integrations.data
    .map((integration) => toConnectIntegration(integration, providersByIntegration.get(integration.id) ?? []))
    .filter((integration): integration is ConnectIntegration => integration !== null)
    .sort((left, right) => {
      if (left.connected !== right.connected) {
        return left.connected ? -1 : 1;
      }
      return left.name.localeCompare(right.name);
    });
}

export async function startOauthConnection(
  integration: ConnectIntegration,
  method: ConnectMethod,
): Promise<IntegrationAttempt> {
  const { data, error } = await opencodeV2Client.integration.connectOauth({
    integrationID: integration.id,
    methodID: method.id,
    ...(method.answer ? { answer: method.answer } : {}),
  });

  if (error || !data) {
    throw error || new Error("No login attempt received from server");
  }
  return data;
}

export async function connectProviderKey(
  integration: ConnectIntegration,
  method: ConnectMethod,
  key: string,
): Promise<void> {
  const { error } = await opencodeV2Client.integration.connectKey({
    integrationID: integration.id,
    key,
    ...(method.answer ? { answer: method.answer } : {}),
  });

  if (error) {
    throw error;
  }
}

export async function readOauthStatus(
  integrationID: string,
  attemptID: string,
): Promise<IntegrationAttemptStatus> {
  const { data, error } = await opencodeV2Client.integration.oauthStatus({
    integrationID,
    attemptID,
  });

  if (error || !data) {
    throw error || new Error("No login status received from server");
  }
  return data;
}

export async function completeOauthConnection(
  integrationID: string,
  attemptID: string,
  code?: string,
): Promise<void> {
  const { error } = await opencodeV2Client.integration.oauthComplete({
    integrationID,
    attemptID,
    ...(code !== undefined ? { code } : {}),
  });

  if (error) {
    throw error;
  }
}

export async function cancelOauthConnection(
  integrationID: string,
  attemptID: string,
): Promise<void> {
  const { error } = await opencodeV2Client.integration.oauthCancel({
    integrationID,
    attemptID,
  });

  if (error) {
    throw error;
  }
}
