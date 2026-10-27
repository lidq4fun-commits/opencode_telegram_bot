import type { CommandContext, Context } from "grammy";
import type { AppContainer } from "../../app/bootstrap/app-container.js";
import { openCommand } from "./open-command.js";

export type CreateProjectCommandDeps = Pick<
  AppContainer,
  "attachManager" | "foregroundSessionState" | "interactionManager"
>;

export async function createProjectCommand(
  ctx: CommandContext<Context>,
  deps: CreateProjectCommandDeps,
): Promise<void> {
  await openCommand(ctx, deps, { createProject: true });
}
