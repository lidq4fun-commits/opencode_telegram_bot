import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import test from "node:test";

const script = readFileSync(new URL("../EmbeddedWebSync.js", import.meta.url), "utf8");
const server = "http://127.0.0.1:49374";
const sessionPath = "/server/key/session/ses_shared";
const picked = { providerID: "provider", modelID: "model", variant: "high" };
function fixture(apiVersion = "v2") {
  class Storage {
    values = new Map();
    get length() { return this.values.size; }
    key(index) { return [...this.values.keys()][index]; }
    getItem(key) { return this.values.get(key) ?? null; }
    setItem(key, value) { this.values.set(key, String(value)); }
    removeItem(key) { this.values.delete(key); }
  }
  const commands = [], calls = [], notices = [], timers = new Map(), listeners = new Map();
  const localStorage = new Storage(), sessionStorage = new Storage();
  const initialPath = apiVersion === "v1" ? `/${Buffer.from("/project").toString("base64url")}/session/ses_shared` : sessionPath;
  const location = { origin: server, href: server + initialPath, pathname: initialPath, search: "", replacements: [], reloads: 0,
    replace(url) { this.replacements.push(url); this.go(url); }, reload() { this.reloads++; },
    go(url) { const value = new URL(url, server); this.href = value.href; this.pathname = value.pathname; this.search = value.search; } };
  const history = { pushState(_state, _title, url) { location.go(url); }, replaceState(_state, _title, url) { location.go(url); } };
  let nextTimer = 0, response = new Response("{}", { status: 200 });
  const document = { body: { appendChild(element) { notices.push(element); } },
    createElement() { return { style: {}, setAttribute() {}, remove() {} }; }, getElementById() {},
    addEventListener(event, fn) { listeners.set(event, fn); } };
  const window = { history, location, chrome: { webview: { postMessage(value) { commands.push(value); } } },
    addEventListener(event, fn) { listeners.set(event, fn); },
    async fetch(input, init) { calls.push({ input, init }); if (input instanceof Request) await input.text(); return response; } };
  vm.runInNewContext(script, { window, document, Storage, localStorage, sessionStorage, location, history,
    URL, URLSearchParams, Request, TextDecoder, Uint8Array, atob, Date, Math,
    setTimeout(fn, ms) { const id = ++nextTimer; timers.set(id, { fn, ms }); return id; }, clearTimeout(id) { timers.delete(id); } });
  const state = { instance: "instance", mobileRevision: 1, mobileActive: false, busy: false, updatedAt: Date.now(),
    currentProject: { worktree: "/project" }, currentSession: { id: "ses_shared" }, currentModel: picked, acknowledgements: [] };
  const accept = (next = state, target = server + initialPath) => window.__opencodeTelegramSync.accept(next, target, apiVersion, "en");
  accept(); location.reloads = 0;
  return { window, history, location, state, localStorage, sessionStorage, commands, calls, notices, accept,
    input() { listeners.get("pointerdown")({ isTrusted: true }); },
    flush(ms = 100) { for (const [id, timer] of [...timers]) if (timer.ms <= ms) { timers.delete(id); timer.fn(); } },
    response(value) { response = new Response(JSON.stringify(value)); },
    ack(reason = null) { accept({ ...state, acknowledgements: commands.map(({ id }) => ({ id, reason })) }); } };
}

test("idle project list is not pulled back by unchanged polling", () => {
  const f = fixture(); f.history.pushState({}, "", "/"); f.flush(); f.accept(); f.accept();
  assert.equal(f.location.pathname, "/"); assert.equal(f.location.replacements.length, 0);
});
test("phone action follows once, not on every subsequent idle poll", () => {
  const f = fixture(); f.history.pushState({}, "", "/"); f.flush();
  const next = { ...f.state, mobileRevision: 2 }; f.accept(next);
  assert.equal(f.location.pathname, sessionPath); assert.match(f.notices.at(-1).textContent, /phone/);
  f.history.pushState({}, "", "/"); f.flush(); f.accept(next);
  assert.equal(f.location.pathname, "/");
});
test("answering blocks navigation with a reason and completion releases it", () => {
  const f = fixture(); f.accept({ ...f.state, busy: true });
  f.history.pushState({}, "", "/"); f.flush(); assert.equal(f.location.pathname, sessionPath);
  assert.match(f.notices.at(-1).textContent, /AI is still responding/);
  f.accept(f.state); f.history.pushState({}, "", "/"); f.flush(); f.accept(); assert.equal(f.location.pathname, "/");
});
test("desktop session navigation publishes metadata without a mobile echo", () => {
  const f = fixture(); f.history.pushState({}, "", "/server/key/session/ses_desktop"); f.flush();
  assert.equal(f.commands[0].sessionID, "ses_desktop"); assert.equal(f.commands[0].mobileRevision, 1);
  f.ack(); assert.equal(f.location.pathname, "/server/key/session/ses_desktop");
});
test("desktop prompt waits for phone attachment before sending to OpenCode", async () => {
  const f = fixture();
  const promise = f.window.fetch(server + "/api/session/ses_shared/prompt", { method: "POST", body: JSON.stringify({ text: "private prompt" }) });
  assert.equal(f.calls.length, 0); assert.equal(f.commands[0].kind, "prompt");
  assert.ok(!JSON.stringify(f.commands).includes("private prompt"));
  f.ack(); await promise; assert.equal(f.calls.length, 1);
});
test("a rejected stale prompt is not executed", async () => {
  const f = fixture(); const promise = f.window.fetch(server + "/api/session/ses_shared/prompt", { method: "POST", body: "{}" });
  f.ack("mobile"); await assert.rejects(promise, /mobile/); assert.equal(f.calls.length, 0);
});
test("model and thinking selections are sent after user changes, not on storage initialization", () => {
  const f = fixture(); const key = "opencode.workspace.test.dat:workspace:model-selection";
  const raw = JSON.stringify({ session: { ses_shared: { model: { providerID: "provider", modelID: "different" }, variant: "low" } } });
  f.localStorage.setItem(key, raw); f.flush(); assert.equal(f.commands.length, 0);
  f.input(); f.localStorage.setItem(key, raw); f.flush();
  assert.equal(f.commands[0].kind, "model"); assert.equal(f.commands[0].model.variant, "low"); f.ack();
});
test("phone model applies without erasing a persisted desktop prompt", () => {
  const f = fixture(); const key = "opencode.workspace.test.dat:session:ses_shared:prompt";
  f.localStorage.setItem(key, JSON.stringify({ prompt: [{ type: "text", content: "unsent" }], cursor: 6, model: picked }));
  f.accept({ ...f.state, mobileRevision: 2, currentModel: { ...picked, variant: "low" } });
  const stored = JSON.parse(f.localStorage.getItem(key));
  assert.equal(stored.prompt[0].content, "unsent"); assert.equal(stored.model.variant, "low"); assert.equal(f.location.reloads, 1);
});
test("V2 home project selection has no stale conversation ID", () => {
  const f = fixture(); f.history.pushState({}, "", "/"); f.flush(); f.input();
  f.localStorage.setItem("opencode.global.dat:layout", JSON.stringify({ home: { selection: { server, directory: "/other" } } }));
  assert.equal(f.commands[0].directory, "/other"); assert.equal(f.commands[0].sessionID, undefined); f.ack();
});
test("an existing desktop draft adopts the phone model and preserves its prompt", () => {
  const f = fixture();
  f.localStorage.setItem("opencode.window.dat:tabs", JSON.stringify([{ type: "draft", draftID: "draft-active", directory: "/project" }]));
  f.history.pushState({}, "", "/new-session?draftId=draft-active"); f.flush(); f.ack();
  const key = "opencode.draft.draft-active.dat:draft:prompt";
  f.localStorage.setItem(key, JSON.stringify({ prompt: [{ type: "text", content: "draft" }], model: picked }));
  f.accept({ ...f.state, currentSession: undefined, mobileRevision: 2, currentModel: { ...picked, variant: "low" } });
  assert.equal(f.location.pathname, "/new-session");
  assert.equal(JSON.parse(f.localStorage.getItem(key)).model.variant, "low");
  assert.equal(JSON.parse(f.localStorage.getItem(key)).prompt[0].content, "draft");
});
test("new conversation creation is not sent while an answer is running", async () => {
  const f = fixture(); f.accept({ ...f.state, busy: true });
  await assert.rejects(f.window.fetch(server + "/api/session", { method: "POST", body: "{}" }), /current synchronized operation/);
  assert.equal(f.calls.length, 0);
});
test("new conversations are attached before their create response is returned to the UI", async () => {
  const f = fixture(); f.response({ id: "ses_created", location: { directory: "/new-project" } });
  const promise = f.window.fetch(server + "/api/session", { method: "POST", body: "{}" });
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(f.commands[0].sessionID, "ses_created"); assert.equal(f.commands[0].directory, "/new-project");
  f.ack(); await promise;
});
test("V1 desktop project routes preserve Unicode and do not reuse a phone session", () => {
  const f = fixture("v1"), directory = "E:\\项目\\demo";
  f.history.pushState({}, "", `/${Buffer.from(directory).toString("base64url")}/session`); f.flush();
  assert.equal(f.commands[0].directory, directory); assert.equal(f.commands[0].sessionID, undefined); f.ack();
});
test("a disconnected bot cannot leave project browsing permanently locked", () => {
  const f = fixture(); f.accept({ ...f.state, busy: true, updatedAt: Date.now() - 10000 });
  f.history.pushState({}, "", "/"); f.flush();
  assert.equal(f.location.pathname, "/");
});
test("pending desktop prompt is not navigated away by busy polling", async () => {
  const f = fixture();
  f.history.pushState({}, "", "/server/key/session/ses_desktop");
  const promise = f.window.fetch(server + "/api/session/ses_desktop/prompt", { method: "POST", body: "{}" });
  f.accept({ ...f.state, busy: true });
  assert.equal(f.location.pathname, "/server/key/session/ses_desktop");
  assert.equal(f.location.replacements.length, 0);
  f.ack(); await promise;
  assert.equal(f.calls.length, 1);
});
test("model refresh retains authentication and the current route", () => {
  const f = fixture();
  f.accept({ ...f.state, mobileRevision: 2, currentModel: { ...picked, variant: "low" } }, server + sessionPath + "?auth_token=test-token");
  const address = new URL(f.location.replacements.at(-1));
  assert.equal(address.pathname, sessionPath);
  assert.equal(address.searchParams.get("auth_token"), "test-token");
  assert.equal(f.location.reloads, 0);
});
test("Request-based model update reads metadata before fetch consumes its body", async () => {
  const f = fixture();
  const request = new Request(server + "/api/session/ses_shared/model", { method: "POST", body: JSON.stringify({ model: picked }) });
  const promise = f.window.fetch(request);
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(request.bodyUsed, true);
  assert.equal(f.commands[0].kind, "model");
  f.ack(); await promise;
  assert.equal(f.calls.length, 1);
});
test("Request-based prompt preserves its body for the native fetch", async () => {
  const f = fixture();
  const request = new Request(server + "/api/session/ses_shared/prompt", { method: "POST", body: JSON.stringify({ text: "private prompt" }) });
  const promise = f.window.fetch(request);
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(request.bodyUsed, false);
  assert.equal(f.commands[0].kind, "prompt");
  f.ack(); await promise;
  assert.equal(request.bodyUsed, true);
  assert.equal(f.calls.length, 1);
});
