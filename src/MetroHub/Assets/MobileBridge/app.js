/* MetroHub Phone Bridge client. Plain ES2017, no dependencies, no external requests.
 *
 * Hosted PWA + Direct LAN Transfer model:
 * The PWA shell can run from any origin (e.g. GitHub Pages or local HTTP).
 * Pair tokens and LAN address are persisted in localStorage.
 * Local API calls travel directly over LAN using W3C Private Network Access (PNA).
 */
(() => {
  "use strict";

  const ROUTES = {
    health: "/m/api/health",
    context: "/m/api/context",
    pin: "/m/api/pin",
    upload: "/m/api/upload",
    unpair: "/m/api/unpair",
    events: "/m/api/events"
  };

  const SVG_ICONS = {
    photo: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="4"/><circle cx="8.5" cy="8.5" r="1.5"/><path d="m21 15-5-5L5 21"/></svg>',
    link: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></svg>',
    note: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/></svg>',
    text: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>'
  };
  const PREFS = {
    workspace: "metrohub.workspace",
    size: "metrohub.size",
    host: "metrohub.host",
    token: "metrohub.token",
    deviceToken: "metrohub.deviceToken"
  };
  const SUPPORTED_IMAGE_TYPES = new Set(["image/jpeg", "image/png", "image/webp", "image/gif", "image/bmp"]);
  const TOAST_MS = 3800;
  const POLL_MS = 60000;

  const state = {
    checked: false,
    reachable: false,
    paired: false,
    live: false,
    context: null,
    sizes: [],
    workspaces: [],
    workspaceSignature: "",
    recent: [],
    mode: "link",
    span: null,
    workspaceId: null,
    file: null,
    previewUrl: null,
    busy: false,
    events: null,
    installPrompt: null,
    host: "",
    token: ""
  };

  const el = {};
  let toastTimer = 0;
  let pollTimer = 0;

  const $ = (id) => document.getElementById(id);

  // ── Small helpers ─────────────────────────────────────────────────────────

  function store(key, value) {
    try { window.localStorage.setItem(key, value); } catch { /* private mode */ }
  }

  function stored(key) {
    try { return window.localStorage.getItem(key); } catch { return null; }
  }

  function resolveUrl(route) {
    if (state.host) {
      return `http://${state.host}${route}`;
    }
    return route;
  }

  async function request(route, options = {}, timeoutMs = 15000) {
    const url = resolveUrl(route);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    const headers = Object.assign({}, options.headers || {});
    if (state.token && !headers["X-MetroHub-Token"]) {
      headers["X-MetroHub-Token"] = state.token;
    }

    try {
      return await fetch(url, {
        ...options,
        headers,
        targetAddressSpace: "private",
        signal: controller.signal
      });
    } finally {
      clearTimeout(timer);
    }
  }

  async function readJson(response) {
    try { return await response.json(); } catch { return null; }
  }

  function describeNetworkError(error) {
    if (error && error.name === "AbortError") {
      return "Your PC did not answer in time.";
    }

    return "Cannot reach your PC. Check that MetroHub is running and that this phone is on the same Wi-Fi.";
  }

  function formatSize(bytes) {
    if (bytes >= 1024 * 1024) {
      return `${Math.round(bytes / (1024 * 1024))} MB`;
    }

    return `${Math.max(1, Math.round(bytes / 1024))} kB`;
  }

  function relativeTime(millis) {
    const seconds = Math.max(0, Math.round((Date.now() - millis) / 1000));
    if (seconds < 60) return "just now";
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return `${minutes} min ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours} h ago`;
    return `${Math.round(hours / 24)} d ago`;
  }

  function toast(message, kind = "info") {
    el.toast.textContent = message;
    el.toast.className = kind === "info" ? "toast" : `toast is-${kind}`;
    el.toast.hidden = false;

    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.toast.hidden = true; }, TOAST_MS);
  }

  // ── Connection state ──────────────────────────────────────────────────────

  async function refresh({ silent = false } = {}) {
    if (!state.host) {
      state.reachable = false;
      state.checked = true;
      state.paired = false;
      render();
      if (!silent) {
        el.bannerText.textContent = "Scan the QR code on your PC to connect.";
        el.banner.hidden = false;
      }
      return false;
    }

    let reachable = false;
    try {
      const response = await request(ROUTES.health, {}, 6000);
      reachable = response.ok;
    } catch {
      reachable = false;
    }

    state.reachable = reachable;
    state.checked = true;

    if (!reachable) {
      stopEvents();
      render();
      if (!silent) {
        el.bannerText.textContent = "Cannot reach your PC. Check the Wi-Fi and that MetroHub is running.";
      }

      el.banner.hidden = false;
      return false;
    }

    el.banner.hidden = true;

    try {
      const response = await request(ROUTES.context);

      if (response.status === 401) {
        state.paired = false;
        state.context = null;
        stopEvents();
        render();
        return false;
      }

      if (!response.ok) {
        state.live = false;
        render();
        return false;
      }

      const context = await readJson(response);
      if (!context) {
        state.live = false;
        render();
        return false;
      }

      applyContext(context);
      state.paired = true;
      render();
      startEvents();
      return true;
    } catch {
      state.live = false;
      render();
      return false;
    }
  }

  function applyContext(context) {
    if (context.deviceToken) {
      store(PREFS.deviceToken, context.deviceToken);
      state.token = context.deviceToken;
    }

    state.context = context;
    state.sizes = Array.isArray(context.allowedSizes) && context.allowedSizes.length > 0
      ? context.allowedSizes
      : [{ spanX: 2, spanY: 2, label: "2x2" }];

    el.pcName.textContent = context.deviceName || "MetroHub";

    const preferred = stored(PREFS.size);
    const wanted = state.sizes.find((size) => size.label === preferred) ?? state.span ?? state.sizes[0];
    state.span = state.sizes.find((size) => size.spanX === wanted.spanX && size.spanY === wanted.spanY) ?? state.sizes[0];

    renderSizes();

    const workspaces = Array.isArray(context.workspaces) ? context.workspaces : [];
    const signature = workspaces.map((workspace) => `${workspace.id}:${workspace.name}:${workspace.isActive}`).join("|");

    if (signature !== state.workspaceSignature) {
      state.workspaceSignature = signature;
      state.workspaces = workspaces;
      const remembered = stored(PREFS.workspace);
      const known = workspaces.some((workspace) => workspace.id === remembered);
      const active = workspaces.find((workspace) => workspace.isActive);
      state.workspaceId = known ? remembered : (active ? active.id : (workspaces[0] ? workspaces[0].id : null));
      renderWorkspaces();
    }

    state.recent = Array.isArray(context.recent) ? context.recent : [];
  }

  function renderSizes() {
    el.sizeSelect.replaceChildren(...state.sizes.map((size) => {
      const option = document.createElement("option");
      option.value = size.label;
      option.textContent = size.label;
      option.selected = state.span !== null && size.spanX === state.span.spanX && size.spanY === state.span.spanY;
      return option;
    }));
  }

  function renderWorkspaces() {
    el.workspaceSelect.replaceChildren(...state.workspaces.map((workspace) => {
      const option = document.createElement("option");
      option.value = workspace.id;
      option.textContent = workspace.name;
      option.selected = workspace.id === state.workspaceId;
      return option;
    }));
  }

  function startEvents() {
    if (!state.paired || state.events || !state.host) {
      return;
    }

    const eventsUrl = resolveUrl(ROUTES.events) + (state.token ? `?t=${encodeURIComponent(state.token)}` : "");
    const source = new EventSource(eventsUrl);
    state.events = source;

    source.addEventListener("open", () => {
      state.live = true;
      render();
    });

    source.addEventListener("error", () => {
      // EventSource reconnects on its own; the browser retry hint comes from the server.
      state.live = false;
      render();
    });

    source.addEventListener("push", (event) => {
      let item = null;
      try { item = JSON.parse(event.data); } catch { item = null; }
      if (item) {
        onPush(item);
      }
    });
  }

  function stopEvents() {
    if (state.events) {
      state.events.close();
      state.events = null;
    }

    state.live = false;
  }

  function onPush(item) {
    const title = item.title || item.url || item.note || "Item";
    toast(`From ${item.deviceName || "your PC"}: ${title}`, "ok");

    state.recent = [{
      id: item.id,
      direction: "toPhone",
      kind: item.kind,
      title,
      url: item.url ?? null,
      note: item.note ?? null,
      sentAtUtc: item.sentAtUtc,
      duplicate: false
    }, ...state.recent.filter((entry) => entry.id !== item.id)].slice(0, 24);

    renderFeed();
  }

  // ── Rendering ─────────────────────────────────────────────────────────────

  function render() {
    const live = state.paired && state.reachable && state.live;
    const offline = state.checked && !state.reachable;

    el.statusPill.classList.toggle("is-live", live);
    el.statusPill.classList.toggle("is-warn", !live && !offline && state.checked);
    el.statusPill.classList.toggle("is-off", offline);

    el.statusText.textContent = !state.checked
      ? "Connecting…"
      : (!state.reachable
        ? "Offline"
        : (!state.paired
          ? "Not paired"
          : (state.live ? "Live" : (state.events ? "Reconnecting" : "Connecting…"))));

    if (el.radarSubtext) {
      if (!state.paired) {
        el.radarSubtext.textContent = "Not paired · Scan QR or enter IP below";
      } else if (offline) {
        el.radarSubtext.textContent = "PC unreachable · Check same Wi-Fi";
      } else if (live) {
        el.radarSubtext.textContent = "Ready to receive · Tap to drop";
      } else {
        el.radarSubtext.textContent = "Connecting to desktop…";
      }
    }

    el.pairCard.hidden = state.paired;
    el.actions.hidden = false;
    el.options.hidden = !state.paired;
    el.feedSection.hidden = !state.paired;

    const blocked = !state.paired || offline || state.busy;
    el.linkSend.disabled = blocked;
    el.noteSend.disabled = blocked;
    el.photoSend.disabled = blocked || state.file === null;
    el.refreshBtn.disabled = false;

    renderFeed();
    renderFacts();
    renderInstall();
  }

  function renderFeed() {
    const items = state.recent.slice(0, 20);
    el.feedList.replaceChildren(...items.map(buildFeedItem));
    el.feedEmpty.hidden = items.length > 0;
  }

  function buildFeedItem(item) {
    const toPhone = item.direction === "toPhone";
    const li = document.createElement("li");
    li.className = toPhone ? "to-phone" : "to-pc";

    const kind = document.createElement("span");
    kind.className = "kind";
    kind.innerHTML = SVG_ICONS[item.kind] || SVG_ICONS.text;

    const body = document.createElement("div");
    body.className = "body";

    const title = document.createElement("div");
    title.className = "title";
    title.textContent = item.title || item.url || item.note || "Item";
    body.append(title);

    const sub = document.createElement("div");
    sub.className = "sub";
    sub.textContent = `${toPhone ? "From PC" : "Sent"} · ${relativeTime(item.sentAtUtc)}`;
    body.append(sub);

    if (toPhone && item.url) {
      const open = document.createElement("a");
      open.className = "open";
      open.href = item.url;
      open.target = "_blank";
      open.rel = "noreferrer";
      open.textContent = "Open";
      body.append(open);
    }

    li.append(kind, body);

    if (item.duplicate) {
      const badge = document.createElement("span");
      badge.className = "badge";
      badge.textContent = "again";
      li.append(badge);
    }

    return li;
  }

  function renderFacts() {
    const context = state.context;
    el.factPaired.textContent = !context
      ? "Not paired"
      : (context.pairedAs || "This phone");
    el.factPc.textContent = context?.deviceName || "—";
    el.factAddress.textContent = state.host || window.location.host;
    el.factSecure.textContent = window.isSecureContext ? "Secure (HTTPS PWA)" : "Plain HTTP";
  }

  function renderInstall() {
    el.installCard.hidden = false;
    el.installTitle.textContent = "Install as an app";

    if (state.installPrompt) {
      el.installBody.textContent = "Install MetroHub to your home screen to share photos and links directly from the share menu of any app.";
      el.installBtn.hidden = false;
      return;
    }

    if (window.matchMedia && window.matchMedia("(display-mode: standalone)").matches) {
      el.installBody.textContent = "MetroHub is installed on your home screen.";
      el.installBtn.hidden = true;
      return;
    }

    el.installBody.textContent = "To share directly from other apps, install MetroHub via your browser menu (Add to Home screen / Install app).";
    el.installBtn.hidden = true;
  }

  // ── Sending ───────────────────────────────────────────────────────────────

  function normalizeUrl(value) {
    const candidate = /^[a-z][a-z0-9+.-]*:\/\//i.test(value) ? value : `https://${value}`;

    try {
      const url = new URL(candidate);
      if (url.protocol !== "http:" && url.protocol !== "https:") return null;
      if (!url.hostname.includes(".")) return null;
      return url.toString();
    } catch {
      return null;
    }
  }

  async function withBusy(button, label, work) {
    if (state.busy) {
      return;
    }

    state.busy = true;
    const original = button.textContent;
    button.disabled = true;
    button.textContent = label;

    try {
      await work();
    } catch (error) {
      state.reachable = false;
      el.banner.hidden = false;
      render();
      toast(describeNetworkError(error), "error");
    } finally {
      state.busy = false;
      button.textContent = original;
      button.disabled = false;
      render();
    }
  }

  function currentSpan() {
    return state.span ?? state.sizes[0] ?? null;
  }

  function onUnauthorized() {
    state.paired = false;
    stopEvents();
    render();
    toast("Pairing is no longer valid. Scan the QR code on the PC again.", "error");
  }

  async function sendPin({ url = null, title = null, note = null }, button, busyLabel) {
    if (!state.paired) {
      toast("Pair with your PC first.", "error");
      return;
    }

    const span = currentSpan();

    await withBusy(button, busyLabel, async () => {
      const response = await request(ROUTES.pin, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          url,
          title,
          note,
          workspaceId: state.workspaceId,
          spanX: span ? span.spanX : null,
          spanY: span ? span.spanY : null
        })
      }, 40000);

      if (response.status === 401) {
        onUnauthorized();
        return;
      }

      const result = await readJson(response);
      if (!response.ok || !result || !result.success) {
        toast(result?.error || result?.message || "Your PC could not pin that.", "error");
        return;
      }

      toast(result.duplicate ? "Already pinned — same spot as before." : "Pinned to the canvas.", "ok");
      closeComposer();

      if (!result.duplicate) {
        el.linkUrl.value = "";
        el.linkTitle.value = "";
        el.noteText.value = "";
        el.noteTitle.value = "";
      }

      state.reachable = true;
      await refresh({ silent: true });
    });
  }

  async function sendLink() {
    const raw = el.linkUrl.value.trim();
    if (!raw) {
      toast("Enter a web address first.", "error");
      return;
    }

    const url = normalizeUrl(raw);
    if (!url) {
      toast("That does not look like a web address.", "error");
      return;
    }

    await sendPin({ url, title: el.linkTitle.value.trim() || null }, el.linkSend, "Sending…");
  }

  async function sendNote() {
    const note = el.noteText.value.trim();
    if (!note) {
      toast("Write something first.", "error");
      return;
    }

    await sendPin({ note, title: el.noteTitle.value.trim() || null }, el.noteSend, "Sending…");
  }

  async function preparePhoto(file) {
    if (!file) return null;
    let type = (file.type || "").toLowerCase();

    // Deduce type from file name extension if MIME type is missing
    if (!type && file.name) {
      const ext = file.name.split(".").pop().toLowerCase();
      if (ext === "jpg" || ext === "jpeg") type = "image/jpeg";
      else if (ext === "png") type = "image/png";
      else if (ext === "webp") type = "image/webp";
      else if (ext === "gif") type = "image/gif";
      else if (ext === "bmp") type = "image/bmp";
    }

    const isStandard = SUPPORTED_IMAGE_TYPES.has(type);
    const isHuge = file.size > 10 * 1024 * 1024; // > 10 MB

    if (!isStandard || isHuge) {
      try {
        const bmp = await createImageBitmap(file);
        const maxDim = 2560;
        let width = bmp.width;
        let height = bmp.height;
        if (width > maxDim || height > maxDim) {
          if (width > height) {
            height = Math.round((height * maxDim) / width);
            width = maxDim;
          } else {
            width = Math.round((width * maxDim) / height);
            height = maxDim;
          }
        }
        const canvas = document.createElement("canvas");
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext("2d");
        ctx.drawImage(bmp, 0, 0, width, height);
        bmp.close?.();

        const blob = await new Promise((res) => canvas.toBlob(res, "image/jpeg", 0.90));
        if (blob) {
          const baseName = (file.name || "photo").replace(/\.[^.]+$/, "");
          return new File([blob], `${baseName}.jpg`, { type: "image/jpeg" });
        }
      } catch {
        // Fall back to original file if canvas decoding is not supported
      }
    }

    if (!file.type && type) {
      return new File([file], file.name || "photo.jpg", { type });
    }

    return file;
  }

  async function sendPhoto() {
    if (!state.paired) {
      toast("Pair with your PC first.", "error");
      return;
    }

    if (!state.file) {
      toast("Choose a photo first.", "error");
      return;
    }

    await withBusy(el.photoSend, "Optimizing…", async () => {
      const file = await preparePhoto(state.file);
      if (!file) {
        toast("Could not process this photo.", "error");
        return;
      }

      const type = (file.type || "").toLowerCase();
      if (!SUPPORTED_IMAGE_TYPES.has(type)) {
        toast("Unsupported image format. Send JPEG, PNG, or WebP.", "error");
        return;
      }

      const limit = state.context?.maxUploadBytes ?? 0;
      if (limit > 0 && file.size > limit) {
        toast(`Photo is ${formatSize(file.size)}; limit is ${formatSize(limit)}.`, "error");
        return;
      }

      el.photoSend.textContent = "Uploading…";

      const span = currentSpan();
      const params = new URLSearchParams();

      const title = el.photoTitle.value.trim() || file.name || "";
      if (title) params.set("name", title);
      if (state.workspaceId) params.set("workspaceId", state.workspaceId);
      if (span) {
        params.set("spanX", String(span.spanX));
        params.set("spanY", String(span.spanY));
      }

      const response = await request(`${ROUTES.upload}?${params.toString()}`, {
        method: "POST",
        headers: { "Content-Type": file.type || "image/jpeg" },
        body: file
      }, 90000);

      if (response.status === 401) {
        onUnauthorized();
        return;
      }

      const result = await readJson(response);
      if (!response.ok || !result || !result.success) {
        toast(result?.error || result?.message || "The upload failed.", "error");
        return;
      }

      toast(result.duplicate ? "Photo is already on the canvas." : "Dropped photo to PC.", "ok");
      resetPhoto();
      closeComposer();
      state.reachable = true;
      await refresh({ silent: true });
    });
  }

  async function unpair() {
    if (!window.confirm("Unpair this phone? You will have to scan the QR code again.")) {
      return;
    }

    try {
      const response = await request(ROUTES.unpair, { method: "POST" }, 10000);
      const result = await readJson(response);

      stopEvents();
      state.paired = false;
      state.context = null;
      state.token = "";
      try {
        window.localStorage.removeItem(PREFS.token);
        window.localStorage.removeItem(PREFS.deviceToken);
      } catch { /* private mode */ }

      closeSettings();
      closeComposer();
      render();

      // Either way this browser is done being paired: the server cleared the cookie, or the token
      // was already gone.
      if (response.ok && result && result.success) {
        toast(result.message || "This phone is unpaired.", "ok");
      } else {
        toast(result?.error || "This phone was already unpaired.", "warn");
      }
    } catch (error) {
      toast(describeNetworkError(error), "error");
    }
  }

  // ── Photos ────────────────────────────────────────────────────────────────

  function setPhoto(file) {
    if (state.previewUrl) {
      URL.revokeObjectURL(state.previewUrl);
      state.previewUrl = null;
    }

    state.file = file;

    if (!file) {
      el.photoPreview.hidden = true;
      el.photoPreview.removeAttribute("src");
      el.photoPickText.hidden = false;
      el.photoPick.classList.remove("has-photo");
      return;
    }

    state.previewUrl = URL.createObjectURL(file);
    el.photoPreview.src = state.previewUrl;
    el.photoPreview.hidden = false;
    el.photoPickText.hidden = true;
    el.photoPick.classList.add("has-photo");
  }

  function resetPhoto() {
    setPhoto(null);
    el.photoInput.value = "";
    el.photoTitle.value = "";
  }

  // ── Panels: settings sheet and composer sheet ─────────────────────────────

  function openPanel(panel) {
    panel.hidden = false;
    document.body.style.overflow = "hidden";
  }

  function closePanel(panel) {
    panel.hidden = true;
    document.body.style.overflow = "";
  }

  function openSettings() {
    openPanel(el.sheet);
    renderFacts();
    renderInstall();
  }

  function closeSettings() {
    closePanel(el.sheet);
  }

  const COMPOSER_TITLES = { link: "Send a link", note: "Write a note", photo: "Send a photo" };

  function openComposer(mode, focus = true) {
    state.mode = mode;

    el.composerTitle.textContent = COMPOSER_TITLES[mode] ?? COMPOSER_TITLES.link;
    el.linkForm.hidden = mode !== "link";
    el.noteForm.hidden = mode !== "note";
    el.photoForm.hidden = mode !== "photo";

    openPanel(el.composerSheet);
    render();

    if (!focus) {
      return;
    }

    if (mode === "link") el.linkUrl.focus({ preventScroll: true });
    if (mode === "note") el.noteText.focus({ preventScroll: true });
  }

  function closeComposer() {
    closePanel(el.composerSheet);
    render();
  }

  // ── Share-target prefill ──────────────────────────────────────────────────

  function readSharedData() {
    return new Promise((resolve) => {
      if (!("indexedDB" in window)) {
        resolve(null);
        return;
      }
      try {
        const req = indexedDB.open("metrohub-share-db", 1);
        req.onupgradeneeded = (e) => {
          const db = e.target.result;
          if (!db.objectStoreNames.contains("shares")) {
            db.createObjectStore("shares", { keyPath: "id" });
          }
        };
        req.onsuccess = (e) => {
          const db = e.target.result;
          if (!db.objectStoreNames.contains("shares")) {
            resolve(null);
            return;
          }
          const tx = db.transaction("shares", "readwrite");
          const store = tx.objectStore("shares");
          const getReq = store.get("pending");
          getReq.onsuccess = () => {
            const result = getReq.result;
            if (result) {
              store.delete("pending");
            }
            resolve(result || null);
          };
          getReq.onerror = () => resolve(null);
        };
        req.onerror = () => resolve(null);
      } catch {
        resolve(null);
      }
    });
  }

  async function applySharedContent() {
    const params = new URLSearchParams(window.location.search);
    let sharedUrl = params.get("url") ?? "";
    let sharedText = params.get("text") ?? "";
    let sharedTitle = params.get("title") ?? "";
    let sharedFiles = null;

    if (params.get("shared") === "1") {
      const data = await readSharedData();
      if (data) {
        sharedUrl = data.url || sharedUrl;
        sharedText = data.text || sharedText;
        sharedTitle = data.title || sharedTitle;
        sharedFiles = data.files || null;
      }
    }

    if (sharedFiles && sharedFiles.length > 0) {
      const file = sharedFiles[0];
      setPhoto(file);
      if (sharedTitle) {
        el.photoTitle.value = sharedTitle;
      }
      openComposer("photo", false);
      toast("Ready to send the shared photo.", "ok");
      return;
    }

    const looksLikeLink = sharedUrl !== "" || /https?:\/\//i.test(sharedText);

    if (looksLikeLink) {
      const candidate = sharedUrl !== "" ? sharedUrl : sharedText;
      el.linkUrl.value = (candidate.match(/https?:\/\/\S+/i)?.[0] ?? candidate).trim();
      el.linkTitle.value = sharedTitle;
      openComposer("link", false);
      toast("Ready to send the shared link.", "ok");
      return;
    }

    if (sharedText !== "" || sharedTitle !== "") {
      el.noteText.value = sharedText;
      el.noteTitle.value = sharedTitle;
      openComposer("note", false);
      toast("Ready to send the shared text.", "ok");
    }
  }

  function readPairingParams() {
    let params = new URLSearchParams(window.location.hash.startsWith("#") ? window.location.hash.slice(1) : "");
    if (!params.get("host")) {
      params = new URLSearchParams(window.location.search);
    }

    const hostParam = params.get("host");
    const tokenParam = params.get("t") || params.get("token");

    if (hostParam) {
      store(PREFS.host, hostParam);
      state.host = hostParam;
    } else {
      state.host = stored(PREFS.host) || (window.location.protocol.startsWith("http") && window.location.port ? window.location.host : "");
    }

    if (tokenParam) {
      store(PREFS.token, tokenParam);
      state.token = tokenParam;
    } else {
      state.token = stored(PREFS.deviceToken) || stored(PREFS.token) || "";
    }
  }

  function cleanAddressBar() {
    if (window.location.hash !== "" || (window.location.search !== "" && !window.location.search.includes("shared="))) {
      window.history.replaceState(null, "", window.location.pathname);
    }
  }

  // ── Wiring ────────────────────────────────────────────────────────────────

  function cacheElements() {
    const ids = [
      "statusPill", "statusText", "pcName", "moreBtn", "banner", "bannerText", "bannerRetry",
      "pairCard", "pairCheckBtn", "actions", "composerSheet", "composerBackdrop", "composerTitle",
      "composerClose", "linkForm", "linkUrl", "linkTitle", "linkSend",
      "noteForm", "noteText", "noteTitle", "noteSend", "photoForm", "photoInput", "photoPick",
      "photoPreview", "photoPickText", "photoTitle", "photoSend", "options", "sizeSelect",
      "workspaceSelect", "feedSection", "feedList", "feedEmpty", "refreshBtn", "sheet",
      "sheetBackdrop", "closeSheetBtn", "factPaired", "factPc", "factAddress", "factSecure",
      "installCard", "installTitle", "installBody", "installBtn", "unpairBtn", "toast",
      "manualHostInput", "manualConnectBtn", "radarSubtext", "radarTarget", "radarHero"
    ];

    for (const id of ids) {
      el[id] = $(id);
    }
  }

  function bindEvents() {
    el.moreBtn.addEventListener("click", openSettings);
    el.closeSheetBtn.addEventListener("click", closeSettings);
    el.sheetBackdrop.addEventListener("click", closeSettings);
    el.unpairBtn.addEventListener("click", unpair);
    el.refreshBtn.addEventListener("click", () => refresh());
    el.pairCheckBtn.addEventListener("click", () => refresh());
    el.bannerRetry.addEventListener("click", () => refresh());

    if (el.manualConnectBtn && el.manualHostInput) {
      const handleManualConnect = () => {
        let host = el.manualHostInput.value.trim();
        if (!host) {
          toast("Enter PC address (e.g. 192.168.1.5:48850)", "error");
          return;
        }
        host = host.replace(/^https?:\/\//i, "").replace(/\/+$/, "");
        store(PREFS.host, host);
        state.host = host;
        toast(`Connecting to ${host}…`, "info");
        refresh();
      };

      el.manualConnectBtn.addEventListener("click", handleManualConnect);
      el.manualHostInput.addEventListener("keydown", (e) => {
        if (e.key === "Enter") {
          e.preventDefault();
          handleManualConnect();
        }
      });
    }

    if (el.radarTarget) {
      el.radarTarget.addEventListener("click", () => {
        if (!state.paired) {
          el.manualHostInput?.focus();
        } else {
          openComposer("photo");
        }
      });
    }

    el.actions.addEventListener("click", (event) => {
      const action = event.target.closest(".action");
      if (action) {
        openComposer(action.dataset.action);
      }
    });

    el.composerClose.addEventListener("click", closeComposer);
    el.composerBackdrop.addEventListener("click", closeComposer);

    el.linkSend.addEventListener("click", sendLink);
    el.noteSend.addEventListener("click", sendNote);
    el.photoSend.addEventListener("click", sendPhoto);

    el.linkUrl.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        sendLink();
      }
    });

    el.photoPick.addEventListener("click", () => el.photoInput.click());
    el.photoInput.addEventListener("change", () => {
      setPhoto(el.photoInput.files && el.photoInput.files[0] ? el.photoInput.files[0] : null);
      render();
    });

    el.sizeSelect.addEventListener("change", () => {
      const chosen = state.sizes.find((size) => size.label === el.sizeSelect.value);
      if (chosen) {
        state.span = chosen;
        store(PREFS.size, chosen.label);
      }
    });

    el.workspaceSelect.addEventListener("change", () => {
      state.workspaceId = el.workspaceSelect.value;
      store(PREFS.workspace, state.workspaceId);
    });

    el.installBtn.addEventListener("click", async () => {
      const prompt = state.installPrompt;
      if (!prompt) return;

      state.installPrompt = null;
      prompt.prompt();
      try { await prompt.userChoice; } catch { /* dismissed */ }
      renderInstall();
    });

    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") {
        // A resumed page can hold a socket the OS already dropped, so start the stream over.
        stopEvents();
        refresh({ silent: true });
      }
    });

    window.addEventListener("online", () => {
      stopEvents();
      refresh({ silent: true });
    });
    window.addEventListener("beforeinstallprompt", (event) => {
      event.preventDefault();
      state.installPrompt = event;
      renderInstall();
    });

    window.addEventListener("appinstalled", () => {
      state.installPrompt = null;
      toast("MetroHub is installed.", "ok");
      renderInstall();
    });
  }

  function registerServiceWorker() {
    if (!("serviceWorker" in navigator)) {
      return;
    }

    navigator.serviceWorker.register("sw.js").catch(() => { /* offline support is optional */ });
  }

  async function start() {
    cacheElements();
    bindEvents();
    readPairingParams();

    // The pair card is visible until the first successful context load answers otherwise.
    state.paired = false;
    render();
    await applySharedContent();
    cleanAddressBar();
    registerServiceWorker();

    refresh({ silent: true }).then(() => {
      pollTimer = window.setInterval(() => refresh({ silent: true }), POLL_MS);
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", start);
  } else {
    start();
  }
})();
