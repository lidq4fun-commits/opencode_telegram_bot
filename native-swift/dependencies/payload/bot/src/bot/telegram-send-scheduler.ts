import { AsyncLocalStorage } from "node:async_hooks";

export type TelegramDeliveryPriority = "important" | "progress";
const priorityContext = new AsyncLocalStorage<TelegramDeliveryPriority>();

export function withTelegramDeliveryPriority<T>(
  priority: TelegramDeliveryPriority,
  operation: () => T,
): T {
  return priorityContext.run(priority, operation);
}

interface PendingSend {
  key: string | undefined;
  priority: number;
  operation: () => Promise<unknown>;
  waiters: { resolve: (value: unknown) => void; reject: (error: unknown) => void }[];
}

interface ChatQueue {
  nextSendAt: number;
  pending: PendingSend[];
  timer: ReturnType<typeof setTimeout> | null;
}

// All message creation and edits share one budget, including encrypted chunks.
// A slow upload must not hold questions or unrelated sends behind its response.
export class TelegramSendScheduler {
  private readonly chats = new Map<string, ChatQueue>();

  constructor(private readonly intervalMs = 1100) {}

  schedule<T>(
    method: string,
    payload: Record<string, unknown>,
    operation: () => Promise<T>,
  ): Promise<T> {
    const chatId = payload.chat_id;
    const writesChat = /^(send|editMessage|deleteMessage|pinChat|unpin)/.test(method);
    if ((typeof chatId !== "number" && typeof chatId !== "string") || !writesChat) {
      return operation();
    }

    const chatKey = String(chatId);
    let chat = this.chats.get(chatKey);
    if (!chat) {
      chat = { nextSendAt: 0, pending: [], timer: null };
      this.chats.set(chatKey, chat);
    }
    const markup = payload.reply_markup;
    const hasInlineKeyboard =
      typeof markup === "object" && markup !== null && "inline_keyboard" in markup;
    const priority =
      priorityContext.getStore() === "important" || hasInlineKeyboard
        ? 0
        : priorityContext.getStore() === "progress" ||
            /^(editMessage|sendChatAction|send.*Draft|deleteMessage)/.test(method) ||
            payload.disable_notification === true
          ? 1
          : 0;
    const messageId = payload.message_id ?? payload.draft_id;
    const key =
      method === "sendChatAction"
        ? method
        : (method.startsWith("editMessage") || method.endsWith("Draft")) && messageId !== undefined
          ? `${method}:${String(messageId)}`
          : undefined;

    return new Promise<T>((resolve, reject) => {
      const waiter = { resolve: (value: unknown) => resolve(value as T), reject };
      const pending = key === undefined ? undefined : chat.pending.find((item) => item.key === key);
      if (pending) {
        pending.operation = operation;
        pending.priority = Math.min(pending.priority, priority);
        pending.waiters.push(waiter);
      } else {
        chat.pending.push({ key, priority, operation, waiters: [waiter] });
      }
      this.pump(chatKey, chat);
    });
  }

  defer(chatId: string | number | undefined, delayMs: number): void {
    if (chatId === undefined) return;
    const chatKey = String(chatId);
    let chat = this.chats.get(chatKey);
    if (!chat) {
      chat = { nextSendAt: 0, pending: [], timer: null };
      this.chats.set(chatKey, chat);
    }
    chat.nextSendAt = Math.max(chat.nextSendAt, Date.now() + delayMs);
    this.pump(chatKey, chat);
  }

  private pump(chatKey: string, chat: ChatQueue): void {
    if (chat.timer) return;
    const waitMs = Math.max(0, chat.nextSendAt - Date.now());
    if (waitMs > 0) {
      chat.timer = setTimeout(() => {
        chat.timer = null;
        this.pump(chatKey, chat);
      }, waitMs);
      return;
    }
    if (chat.pending.length === 0) {
      this.chats.delete(chatKey);
      return;
    }
    const priority = Math.min(...chat.pending.map((item) => item.priority));
    const index = chat.pending.findIndex((item) => item.priority === priority);
    const [pending] = chat.pending.splice(index, 1);
    if (!pending) return;
    chat.nextSendAt = Date.now() + this.intervalMs;
    void Promise.resolve()
      .then(pending.operation)
      .then(
        (value) => pending.waiters.forEach((waiter) => waiter.resolve(value)),
        (error: unknown) => pending.waiters.forEach((waiter) => waiter.reject(error)),
      );
    this.pump(chatKey, chat);
  }
}
