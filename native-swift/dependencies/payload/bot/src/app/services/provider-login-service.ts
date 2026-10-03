import { opencodeV2Client } from "../../opencode/client.js";
import { providerLoginReminder } from "../../utils/provider-auth.js";
import { getCurrentProject } from "../stores/settings-store.js";
import { getStoredModel } from "./model-selection-service.js";
import { logger } from "../../utils/logger.js";

export async function needsProviderLogin(
  model: { providerID: string; modelID: string },
  directory?: string,
): Promise<boolean> {
  if (!opencodeV2Client.provider?.connectionStatus) return false;
  try {
    const result = await opencodeV2Client.provider.connectionStatus({
      ...model,
      ...(directory ? { directory } : {}),
    });
    return !result.error && result.data === "needs_auth";
  } catch {
    // Failure to query the service is not proof that the provider needs a login.
    return false;
  }
}

export async function notifyProviderLoginIfNeeded(
  send: (message: string) => Promise<unknown>,
): Promise<void> {
  const model = getStoredModel();
  if (!model.providerID || !model.modelID) return;
  if (await needsProviderLogin(model, getCurrentProject()?.worktree)) {
    await send(providerLoginReminder());
    logger.info("[Bot] Sent provider login reminder; credentials were not printed");
  }
}
