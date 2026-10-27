// Runs only in the embedded, configured OpenCode origin. No prompt text is sent
// to the host: commands contain navigation and model selection metadata only.
(() => {
  if (window.top && window.top !== window) return;
  if (window.__opencodeTelegramSync) return;
  const nativeFetch = window.fetch.bind(window);
  const nativeGet = Storage.prototype.getItem;
  const nativeSet = Storage.prototype.setItem;
  const pending = new Map();
  let state, target, version, locale, lastInput = 0, applying = false;
  let hiddenProjects = new Set();
  let modelTimer, routeTimer, lastRoute = location.pathname + location.search;
  let sequence = 0;
  const parse = (value) => { try { return JSON.parse(value); } catch { return undefined; } };
  const pathKey = (value) => typeof value === "string" ? value.replace(/[\\/]+$/, "").toLowerCase() : "";
  const modelKey = (model) => model ? `${model.providerID}/${model.modelID || model.id}/${model.variant || "default"}` : "";
  const model = (item, variant) => item?.providerID && (item.modelID || item.id)
    ? { providerID: item.providerID, modelID: item.modelID || item.id, variant: variant ?? item.variant ?? "default" } : undefined;
  const sessionID = () => /\/session\/(ses_[\w-]+)/.exec(location.pathname)?.[1];
  const decode = (value) => {
    try { return new TextDecoder().decode(Uint8Array.from(atob(value.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0))); }
    catch { return undefined; }
  };
  const directory = () => {
    if (version === "v1") return decode(location.pathname.split("/")[1] || "");
    const draftId = new URLSearchParams(location.search).get("draftId");
    if (draftId) {
      const find = (value) => {
        if (!value || typeof value !== "object") return undefined;
        if (value.draftID === draftId && value.directory) return value.directory;
        for (const item of Object.values(value)) { const result = find(item); if (result) return result; }
      };
      for (let index = 0; index < localStorage.length; index++) {
        const value = parse(nativeGet.call(localStorage, localStorage.key(index)));
        const result = find(value);
        if (result) return result;
      }
    }
    return new URLSearchParams(location.search).get("directory") || state?.currentProject?.worktree;
  };
  const notice = (reason) => {
    const text = locale === "en"
      ? ({ busy: "AI is still responding. Stay in this conversation until it finishes.", mobile: "The phone just changed the shared workspace. Following its latest action.", unavailable: "Synchronization is unavailable. Reconnect the bot before continuing.", stale: "Workspace changed. Following the latest synchronized state." }[reason] || "Synchronization failed. Please reconnect.")
      : ({ busy: "AI 还在回答，请等待回答结束后再切换项目或会话。", mobile: "手机刚刚操作了共享工作区，正在跟随手机的最新操作。", unavailable: "同步暂时不可用，请重新连接机器人后继续。", stale: "工作区状态已更新，正在跟随最新同步状态。" }[reason] || "同步失败，请重新连接机器人。");
    let element = document.getElementById("opencode-telegram-sync-notice");
    if (!element) {
      element = document.createElement("div");
      element.id = "opencode-telegram-sync-notice";
      element.setAttribute("role", "status");
      element.style.cssText = "position:fixed;top:52px;left:50%;transform:translateX(-50%);z-index:2147483647;background:#fff3d6;color:#6d4700;border:1px solid #d9ad55;border-radius:8px;padding:12px 18px;max-width:80%;font:14px sans-serif;box-shadow:0 4px 16px #0002;pointer-events:none";
      document.body?.appendChild(element);
    }
    element.textContent = text;
    clearTimeout(element.hideTimer);
    element.hideTimer = setTimeout(() => element.remove(), 5500);
  };
  const send = (kind, details = {}) => new Promise((resolve, reject) => {
    if (!state || Date.now() - state.updatedAt > 6000) { notice("unavailable"); reject(new Error("Synchronization unavailable")); return; }
    const id = `${Date.now()}-${String(++sequence).padStart(6, "0")}-${Math.random().toString(36).slice(2)}`;
    const timer = setTimeout(() => { pending.delete(id); notice("unavailable"); reject(new Error("Synchronization timed out")); }, 12000);
    const payload = { id, kind, instance: state.instance, mobileRevision: state.mobileRevision,
      createdAt: Date.now(), directory: directory(), sessionID: sessionID(), ...details };
    pending.set(id, { resolve, reject, timer, kind, directory: payload.directory });
    window.chrome.webview.postMessage(payload);
  });
  const fire = (kind, details) => { send(kind, details).catch(() => {}); };
  const follow = (reason) => {
    if (!target || location.pathname === new URL(target).pathname) return;
    sessionStorage.setItem("opencode-telegram-sync-reason", reason);
    location.replace(target);
  };
  const reloadAuthenticated = () => {
    const address = new URL(location.href);
    const token = target && new URL(target).searchParams.get("auth_token");
    if (token) { address.searchParams.set("auth_token", token); location.replace(address.href); }
    else location.reload();
  };
  const readOverride = () => parse(sessionStorage.getItem("opencode-telegram-sync-model"));
  const mergeModel = (key, value, override) => {
    if (!override?.model) return value;
    if (key.endsWith(":workspace:model-selection") || key === "model-selection.v1") {
      const result = value || { session: {} };
      const records = result.session || result.pick;
      if (!records || typeof records !== "object" || Array.isArray(records) || !override.sessionID) return value;
      const previous = records[override.sessionID] || {};
      records[override.sessionID] = { ...previous, model: { providerID: override.model.providerID, modelID: override.model.modelID },
        variant: override.model.variant === "default" ? null : override.model.variant };
      return result;
    }
    if (override.sessionID && key.includes(`session:${override.sessionID}:prompt`)) {
      return { ...value, model: override.model };
    }
    if (!override.sessionID && key.endsWith(":draft:prompt") && location.pathname === "/new-session") {
      return { ...value, model: override.model };
    }
    return value;
  };
  Storage.prototype.getItem = function (key) {
    const raw = nativeGet.call(this, key);
    if (this !== localStorage) return raw;
    const updated = mergeModel(key, parse(raw), readOverride());
    return updated ? JSON.stringify(updated) : raw;
  };
  Storage.prototype.setItem = function (key, raw) {
    nativeSet.call(this, key, raw);
    if (this !== localStorage || applying || Date.now() - lastInput > 2500 || !state) return;
    const value = parse(raw);
    if (!value) return;
    // V2's home project selection is part of the persisted layout, not its URL.
    const selection = value.home?.selection;
    if (selection?.directory && location.pathname === "/") {
      fire("selection", { directory: selection.directory, sessionID: undefined });
      return;
    }
    const id = sessionID();
    const selected = (key.endsWith(":workspace:model-selection") || key === "model-selection.v1") ? (value.session || value.pick)?.[id] : undefined;
    const picked = selected ? model(selected.model, selected.variant ?? "default")
      : key.includes(":prompt") ? model(value.model) : undefined;
    if (!picked || modelKey(picked) === modelKey(state.currentModel)) return;
    clearTimeout(modelTimer);
    modelTimer = setTimeout(() => {
      sessionStorage.removeItem("opencode-telegram-sync-model");
      fire("model", { model: picked });
    }, 100);
  };
  for (const event of ["pointerdown", "keydown"]) document.addEventListener(event, (e) => { if (e.isTrusted) lastInput = Date.now(); }, true);

  const routeChanged = () => {
    if (!state || Date.now() - state.updatedAt > 6000 || applying || location.pathname + location.search === lastRoute) return;
    lastRoute = location.pathname + location.search;
    if ((state.busy || state.mobileActive) && state.currentSession?.id) { notice(state.busy ? "busy" : "mobile"); follow(state.busy ? "busy" : "mobile"); return; }
    if (sessionID() || location.pathname === "/new-session" || (version === "v1" && directory())) fire("selection");
  };
  for (const name of ["pushState", "replaceState"]) {
    const original = history[name];
    history[name] = function (...args) { const result = original.apply(this, args); clearTimeout(routeTimer); routeTimer = setTimeout(routeChanged, 0); return result; };
  }
  window.addEventListener("popstate", routeChanged);

  window.fetch = async function (input, init) {
    const url = new URL(typeof input === "string" || input instanceof URL ? input : input.url, location.href);
    const method = (init?.method || (input instanceof Request ? input.method : "GET")).toUpperCase();
    const native = url.origin === location.origin;
    if (native && method === "POST" && /\/(api\/)?session$/.test(url.pathname) && (!state || Date.now() - state.updatedAt > 6000)) {
      notice("unavailable"); throw new Error("Synchronization unavailable");
    }
    // Creating a session in another project is allowed while the shared session answers; only
    // the busy project itself stays locked.
    if (native && method === "POST" && /\/(api\/)?session$/.test(url.pathname) && state?.mobileActive) {
      notice("mobile"); follow("mobile");
      throw new Error("Follow the phone's current operation before creating a session");
    }
    if (native && method === "POST" && /\/(api\/)?session$/.test(url.pathname) && state?.busy) {
      const rawCreation = init?.body ?? (input instanceof Request ? await input.clone().text() : undefined);
      const creation = typeof rawCreation === "string" ? parse(rawCreation) : undefined;
      const creationDirectory = creation?.location?.directory || creation?.directory;
      if (!creationDirectory || creationDirectory === state.currentProject?.worktree) {
        notice("busy"); follow("busy");
        throw new Error("Finish the current synchronized operation before creating a session");
      }
    }
    const prompt = native && method === "POST" && /\/session\/ses_[\w-]+\/(prompt|prompt_async|command|shell)$/.test(url.pathname);
    const modelUpdate = native && method === "POST" && /\/session\/ses_[\w-]+\/model$/.test(url.pathname);
    const raw = prompt || modelUpdate ? (init?.body ?? (input instanceof Request ? await input.clone().text() : undefined)) : undefined;
    const body = typeof raw === "string" ? parse(raw) : undefined;
    if (prompt) {
      await send("prompt", { sessionID: /\/session\/(ses_[\w-]+)/.exec(url.pathname)?.[1],
        directory: body?.location?.directory || url.searchParams.get("directory") || directory(),
        model: model(body?.model, body?.variant) });
    }
    const response = await nativeFetch(input, init);
    // Projects deleted on the phone are hidden here too: the engine has no delete API,
    // so the shared state carries their canonical paths and the UI never sees them.
    if (native && response.ok && method === "GET" && hiddenProjects.size > 0
      && /^\/(api\/)?project$/.test(url.pathname)) {
      const list = await response.clone().json().catch(() => undefined);
      if (Array.isArray(list)) {
        const visible = list.filter((project) => !hiddenProjects.has(pathKey(project?.canonical || project?.worktree)));
        if (visible.length !== list.length) {
          return new Response(JSON.stringify(visible), { status: response.status, statusText: response.statusText, headers: response.headers });
        }
      }
    }
    if (modelUpdate && response.ok) {
      if (model(body?.model)) await send("model", { sessionID: /\/session\/(ses_[\w-]+)/.exec(url.pathname)?.[1], model: model(body.model) });
    }
    if (native && response.ok && method === "POST" && /\/(api\/)?session$/.test(url.pathname)) {
      const created = await response.clone().json().catch(() => undefined);
      if (created?.id) await send("selection", { sessionID: created.id, directory: created.location?.directory || created.directory });
    }
    return response;
  };
  window.__opencodeTelegramSync = {
    accept(next, address, apiVersion, language) {
      const previous = state;
      const hiddenBefore = (previous?.hiddenProjects || []).join("|");
      state = next; target = address; version = apiVersion; locale = language;
      hiddenProjects = new Set((next.hiddenProjects || []).map(pathKey));
      const hiddenChanged = hiddenBefore !== (next.hiddenProjects || []).join("|");
      let selectedDraft;
      for (const ack of next.acknowledgements || []) {
        const request = pending.get(ack.id);
        if (!request) continue;
        clearTimeout(request.timer); pending.delete(ack.id);
        if (ack.reason) { notice(ack.reason); follow(ack.reason); request.reject(new Error(`Synchronization: ${ack.reason}`)); }
        else {
          request.resolve();
          if (request.kind === "selection" && request.directory) selectedDraft = request.directory;
        }
      }
      // A project picked from this page without a session opens its draft, so the page never
      // stays behind on the project list after the phone has already switched to it.
      const sharedSession = next.currentSession?.id;
      if (!sharedSession && location.pathname === "/" && version === "v2") {
        const directory = selectedDraft
          || (previous && next.currentProject?.worktree !== previous.currentProject?.worktree ? next.currentProject?.worktree : undefined);
        if (directory) {
          sessionStorage.setItem("opencode-telegram-sync-reason", "stale");
          location.replace(new URL(target || location.href).origin + "/new-session?directory=" + encodeURIComponent(directory));
        }
      }
      const reason = sessionStorage.getItem("opencode-telegram-sync-reason");
      if (reason) { sessionStorage.removeItem("opencode-telegram-sync-reason"); notice(reason); }
      const mobile = previous && (previous.instance !== next.instance || previous.mobileRevision !== next.mobileRevision);
      if (pending.size === 0 && (!previous || mobile || next.busy || next.mobileActive)) {
        if (mobile) notice("mobile");
        const id = next.currentSession?.id;
        const override = { sessionID: id, model: next.currentModel };
        const saved = readOverride();
        const changed = (!previous || mobile) && model(next.currentModel) && (saved?.sessionID !== id || modelKey(saved?.model) !== modelKey(next.currentModel));
        const projectChanged = (!previous || mobile) && !id && next.currentProject?.worktree && previous?.currentProject?.worktree !== next.currentProject.worktree;
        if (!id && location.pathname === "/new-session" && directory() === next.currentProject?.worktree) target = location.href;
        applying = true;
        try {
          if (changed) {
            sessionStorage.setItem("opencode-telegram-sync-model", JSON.stringify(override));
            for (let index = 0; index < localStorage.length; index++) {
              const key = localStorage.key(index);
              const raw = nativeGet.call(localStorage, key);
              const value = mergeModel(key, parse(raw), override);
              if (value && JSON.stringify(value) !== raw) nativeSet.call(localStorage, key, JSON.stringify(value));
            }
          }
          if (projectChanged) {
            for (let index = 0; index < localStorage.length; index++) {
              const key = localStorage.key(index), value = parse(nativeGet.call(localStorage, key));
              if (value?.home?.selection) {
                value.home.selection = { ...value.home.selection, directory: next.currentProject.worktree };
                nativeSet.call(localStorage, key, JSON.stringify(value));
              }
            }
          }
          const sharedSessionID = next.currentSession?.id;
          if (version === "v2" && projectChanged && !sharedSessionID && next.currentProject?.worktree) {
            // A project was chosen without a session: open its draft instead of the project list.
            target = new URL(target || location.href).origin + "/new-session?directory=" + encodeURIComponent(next.currentProject.worktree);
          }
          // Without a shared session there is nothing to follow: never pull the page to the list.
          const mayFollow = Boolean(sharedSessionID) || projectChanged;
          if (mayFollow && target && location.pathname !== new URL(target).pathname) { follow(mobile ? "mobile" : next.busy ? "busy" : "stale"); return; }
          if (changed || (mobile && projectChanged)) reloadAuthenticated();
        } finally { applying = false; }
      }
      lastRoute = location.pathname + location.search;
      if (next.deletedSession && Date.now() - next.deletedSession.at < 20000
        && sessionID() === next.deletedSession.id) {
        // The phone deleted the session this page is showing.
        location.replace(new URL(target || location.href).origin + "/");
        return;
      }
      if (hiddenChanged) {
        // The project list only refreshes on load; reload so a project deleted on the
        // phone disappears here without waiting for the next manual navigation.
        if (!next.currentSession?.id && version === "v2") {
          location.replace(new URL(target || location.href).origin + "/");
        } else {
          reloadAuthenticated();
        }
      }
    },
  };
})();
