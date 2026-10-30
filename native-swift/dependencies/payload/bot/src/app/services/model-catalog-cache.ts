import fs from "node:fs/promises";
import path from "node:path";
import { getRuntimePaths } from "../../runtime/paths.js";
import { logger } from "../../utils/logger.js";

const CACHE_VERSION = 1;
const CACHE_FILE_NAME = "model-catalog-cache.json";

export interface ModelCatalogCacheProvider {
  id: string;
  name: string;
  models: string[];
}

export interface ModelCatalogCachePayload {
  version: number;
  savedAt: number;
  providers: ModelCatalogCacheProvider[];
}

export function getModelCatalogCachePath(): string {
  return path.join(getRuntimePaths().appHome, CACHE_FILE_NAME);
}

/**
 * Reads the catalog written by the previous run so the first model menu after a restart does
 * not wait for the server to register its providers.
 */
export async function readModelCatalogCache(): Promise<ModelCatalogCachePayload | null> {
  try {
    const content = await fs.readFile(getModelCatalogCachePath(), "utf-8");
    const payload = JSON.parse(content) as ModelCatalogCachePayload;
    if (
      payload?.version !== CACHE_VERSION ||
      !Array.isArray(payload.providers) ||
      payload.providers.length === 0
    ) {
      return null;
    }

    return payload;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT") {
      logger.debug("[ModelManager] Model catalog cache is unreadable", error);
    }
    return null;
  }
}

export async function writeModelCatalogCache(
  providers: ModelCatalogCacheProvider[],
): Promise<void> {
  if (providers.length === 0) {
    return;
  }

  const payload: ModelCatalogCachePayload = {
    version: CACHE_VERSION,
    savedAt: Date.now(),
    providers,
  };
  const cachePath = getModelCatalogCachePath();

  try {
    await fs.mkdir(path.dirname(cachePath), { recursive: true });
    const tempPath = `${cachePath}.tmp`;
    await fs.writeFile(tempPath, JSON.stringify(payload));
    await fs.rename(tempPath, cachePath);
  } catch (error) {
    logger.debug("[ModelManager] Could not write the model catalog cache", error);
  }
}
