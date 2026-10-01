import { describe, expect, it } from "vitest";
import { OpenCode } from "@opencode/client";

describe("OpenCode V2 prompt client contract", () => {
  it("posts text and files at the top level of the V2 session prompt payload", async () => {
    const requests: Array<{ url: string; method: string; body: unknown }> = [];
    const client = OpenCode.make({
      baseUrl: "http://127.0.0.1:49374",
      fetch: async (input, init) => {
        const request = input instanceof Request ? input : new Request(input, init);
        requests.push({
          url: request.url,
          method: request.method,
          body: await request.clone().json(),
        });
        return new Response(
          JSON.stringify({
            id: "inbox-1",
            sessionID: "ses-1",
            time: { created: 1 },
            type: "user",
            payload: { text: "hello" },
            delivery: "queue",
          }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        );
      },
    });

    await client.session.prompt({
      sessionID: "ses-1",
      text: "hello",
      files: [{ uri: "file:///tmp/a.txt", name: "a.txt" }],
      delivery: "queue",
    });

    expect(requests).toEqual([
      {
        url: "http://127.0.0.1:49374/api/session/ses-1/prompt",
        method: "POST",
        body: {
          text: "hello",
          files: [{ uri: "file:///tmp/a.txt", name: "a.txt" }],
          delivery: "queue",
        },
      },
    ]);
  });
});
