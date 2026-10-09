// MetroHub Companion WebExtension Popup Controller
const PORTS = [48842, 48843, 48844, 48845, 48846];
const CLIENT_HEADER = 'BrowserExtension';

let activePort = null;
let currentTab = null;
let selectedSpanX = 2;
let selectedSpanY = 2;
let selectedWorkspaceId = null;
let selectedWorkspaceName = 'Main';
let setPlugTargetState = null;

// DOM Elements
const statusBadge = document.getElementById('statusBadge');
const statusText = document.getElementById('statusText');
const tileTitleInput = document.getElementById('tileTitle');
const workspaceDropdown = document.getElementById('workspaceDropdown');
const workspaceTrigger = document.getElementById('workspaceTrigger');
const workspaceSelectedText = document.getElementById('workspaceSelectedText');
const workspaceMenu = document.getElementById('workspaceMenu');
const size2x2Btn = document.getElementById('size2x2');
const size4x2Btn = document.getElementById('size4x2');
const faviconPreview = document.getElementById('faviconPreview');
const faviconDomain = document.getElementById('faviconDomain');
const pinBtn = document.getElementById('pinBtn');
const feedbackBanner = document.getElementById('feedbackBanner');
const themeToggleBtn = document.getElementById('themeToggleBtn');

document.addEventListener('DOMContentLoaded', () => {
  setupTheme();
  setupPlugAnimation();
  setupSizeButtons();
  setupDropdownListeners();
  setupPinButton();

  // Non-blocking parallel initialization for instant popup render
  initializeTabContext();
  discoverCompanionPort();
});

function setupTheme() {
  if (!themeToggleBtn) return;

  function applyTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    const label = theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme';
    themeToggleBtn.setAttribute('title', label);
    themeToggleBtn.setAttribute('aria-label', label);
  }

  // 1. Instant synchronous theme application (0ms delay)
  let initialTheme = 'dark';
  try {
    const saved = localStorage.getItem('metrohub_theme');
    if (saved === 'light' || saved === 'dark') {
      initialTheme = saved;
    } else {
      const prefersDark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
      initialTheme = prefersDark ? 'dark' : 'dark';
    }
  } catch { }

  applyTheme(initialTheme);
  document.body.classList.add('ready');

  // 2. Background sync with chrome.storage.local
  try {
    chrome.storage.local.get('metrohub_theme', (res) => {
      if (res && res.metrohub_theme && res.metrohub_theme !== initialTheme) {
        applyTheme(res.metrohub_theme);
        try { localStorage.setItem('metrohub_theme', res.metrohub_theme); } catch { }
      }
    });
  } catch { }

  themeToggleBtn.addEventListener('click', () => {
    const current = document.documentElement.getAttribute('data-theme') || 'dark';
    const next = current === 'light' ? 'dark' : 'light';
    applyTheme(next);

    try {
      localStorage.setItem('metrohub_theme', next);
      chrome.storage.local.set({ metrohub_theme: next });
    } catch { }
  });
}

function setupSizeButtons() {
  size2x2Btn.addEventListener('click', () => {
    size2x2Btn.classList.add('active');
    size4x2Btn.classList.remove('active');
    selectedSpanX = 2;
    selectedSpanY = 2;
  });

  size4x2Btn.addEventListener('click', () => {
    size4x2Btn.classList.add('active');
    size2x2Btn.classList.remove('active');
    selectedSpanX = 4;
    selectedSpanY = 2;
  });
}

function setupDropdownListeners() {
  if (!workspaceTrigger || !workspaceMenu) return;

  workspaceTrigger.addEventListener('click', (e) => {
    e.stopPropagation();
    const isOpen = workspaceTrigger.classList.toggle('open');
    workspaceMenu.classList.toggle('hidden', !isOpen);
    workspaceTrigger.setAttribute('aria-expanded', String(isOpen));
  });

  document.addEventListener('click', (e) => {
    if (workspaceDropdown && !workspaceDropdown.contains(e.target)) {
      closeDropdown();
    }
  });

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      closeDropdown();
    }
  });
}

function closeDropdown() {
  if (!workspaceTrigger || !workspaceMenu) return;
  workspaceTrigger.classList.remove('open');
  workspaceMenu.classList.add('hidden');
  workspaceTrigger.setAttribute('aria-expanded', 'false');
}

async function initializeTabContext() {
  try {
    const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tabs || tabs.length === 0) return;

    currentTab = tabs[0];
    tileTitleInput.value = currentTab.title || '';

    let domain = '';
    try {
      domain = new URL(currentTab.url).hostname.replace(/^www\./, '');
    } catch { }

    if (faviconDomain) {
      faviconDomain.textContent = domain || 'Website';
    }

    const faviconUrl = currentTab.favIconUrl || (domain ? `https://www.google.com/s2/favicons?domain=${domain}&sz=128` : '');
    if (faviconPreview) {
      faviconPreview.onerror = () => {
        if (domain && !faviconPreview.src.includes('google.com')) {
          faviconPreview.src = `https://www.google.com/s2/favicons?domain=${domain}&sz=128`;
        }
      };
      if (faviconUrl) {
        faviconPreview.src = faviconUrl;
      }
    }
  } catch (err) {
    console.warn('[MetroHub] Error initializing tab context:', err);
  }
}

async function discoverCompanionPort() {
  setConnectionStatus('connecting', 'Connecting...');

  // 1. Check persistent local storage cache first (<2ms)
  try {
    const cached = await chrome.storage.local.get('metrohub_active_port');
    if (cached && cached.metrohub_active_port) {
      if (await probePort(cached.metrohub_active_port, 250)) {
        activePort = cached.metrohub_active_port;
        onConnected();
        return;
      }
    }
  } catch { }

  // 2. Parallel probe across candidate ports with fast 350ms timeout
  const probePromises = PORTS.map(async (port) => {
    const ok = await probePort(port, 350);
    if (ok) return port;
    throw new Error('offline');
  });

  try {
    const foundPort = await Promise.any(probePromises);
    activePort = foundPort;
    try {
      await chrome.storage.local.set({ metrohub_active_port: foundPort });
    } catch { }
    onConnected();
  } catch {
    onDisconnected();
  }
}

async function probePort(port, timeoutMs = 350) {
  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    const res = await fetch(`http://127.0.0.1:${port}/api/health`, {
      method: 'GET',
      headers: {
        'X-MetroHub-Client': CLIENT_HEADER
      },
      signal: controller.signal
    });
    clearTimeout(timer);

    if (res.ok) {
      const data = await res.json();
      return data.app === 'MetroHub';
    }
  } catch {
    return false;
  }
  return false;
}

function onConnected() {
  setConnectionStatus('connected', 'Connected');
  setButtonState('default');
  loadWorkspaces();
}

function onDisconnected() {
  setConnectionStatus('offline', 'Offline');
  setButtonState('default');
  showFeedback('error', 'MetroHub Offline', 'Start MetroHub desktop app to pin tiles.');
}

function setConnectionStatus(type, label) {
  statusBadge.className = `status-indicator ${type}`;
  statusBadge.setAttribute('title', `MetroHub: ${label}`);
  statusBadge.setAttribute('aria-label', `MetroHub: ${label}`);
  if (statusText) {
    statusText.textContent = label;
  }

  if (type === 'connected') {
    if (typeof setPlugTargetState === 'function') setPlugTargetState('on');
    try {
      localStorage.setItem('metrohub_companion_status', 'connected');
      chrome.storage.local.set({ metrohub_companion_status: 'connected' });
    } catch { }
  } else if (type === 'offline') {
    if (typeof setPlugTargetState === 'function') setPlugTargetState('off');
    try {
      localStorage.setItem('metrohub_companion_status', 'offline');
      chrome.storage.local.set({ metrohub_companion_status: 'offline' });
    } catch { }
  }
}

async function loadWorkspaces() {
  if (!activePort) return;

  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 800);
    const res = await fetch(`http://127.0.0.1:${activePort}/api/workspaces`, {
      method: 'GET',
      headers: {
        'X-MetroHub-Client': CLIENT_HEADER
      },
      signal: controller.signal
    });
    clearTimeout(timer);

    if (res.ok) {
      const workspaces = await res.json();
      if (!Array.isArray(workspaces) || workspaces.length === 0) return;

      renderWorkspaceMenu(workspaces);
    }
  } catch (err) {
    console.warn('[MetroHub] Error fetching workspaces:', err);
  }
}

function renderWorkspaceMenu(workspaces) {
  if (!workspaceMenu) return;
  workspaceMenu.innerHTML = '';
  let activeFound = false;

  workspaces.forEach((ws) => {
    const isCurrent = ws.isActive;
    if (isCurrent && !activeFound) {
      selectedWorkspaceId = ws.id;
      selectedWorkspaceName = ws.name;
      workspaceSelectedText.textContent = `${ws.name} (Active)`;
      activeFound = true;
    }

    const item = document.createElement('button');
    item.type = 'button';
    item.className = `dropdown-item ${selectedWorkspaceId === ws.id ? 'selected' : ''}`;
    item.setAttribute('role', 'option');
    item.setAttribute('aria-selected', String(selectedWorkspaceId === ws.id));
    item.setAttribute('data-id', ws.id);

    const span = document.createElement('span');
    span.textContent = ws.isActive ? `${ws.name} (Active)` : ws.name;
    item.appendChild(span);

    const checkSvg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    checkSvg.setAttribute('class', 'check-icon');
    checkSvg.setAttribute('viewBox', '0 0 24 24');
    checkSvg.setAttribute('fill', 'none');
    checkSvg.setAttribute('stroke', 'currentColor');
    checkSvg.setAttribute('stroke-width', '2.2');
    checkSvg.setAttribute('stroke-linecap', 'round');
    checkSvg.setAttribute('stroke-linejoin', 'round');
    const polyline = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
    polyline.setAttribute('points', '20 6 9 17 4 12');
    checkSvg.appendChild(polyline);
    item.appendChild(checkSvg);

    item.addEventListener('click', (e) => {
      e.stopPropagation();
      selectedWorkspaceId = ws.id;
      selectedWorkspaceName = ws.name;
      workspaceSelectedText.textContent = span.textContent;

      // Update selected styles
      workspaceMenu.querySelectorAll('.dropdown-item').forEach((el) => {
        const isMatch = el.getAttribute('data-id') === ws.id;
        el.classList.toggle('selected', isMatch);
        el.setAttribute('aria-selected', String(isMatch));
      });

      closeDropdown();
    });

    workspaceMenu.appendChild(item);
  });

  if (!activeFound && workspaces.length > 0) {
    selectedWorkspaceId = workspaces[0].id;
    selectedWorkspaceName = workspaces[0].name;
    workspaceSelectedText.textContent = workspaces[0].name;
    const firstItem = workspaceMenu.querySelector('.dropdown-item');
    if (firstItem) {
      firstItem.classList.add('selected');
      firstItem.setAttribute('aria-selected', 'true');
    }
  }
}

function setupPinButton() {
  pinBtn.addEventListener('click', async () => {
    if (!activePort || !currentTab || !currentTab.url) return;

    const title = tileTitleInput.value.trim();
    const workspaceId = selectedWorkspaceId || null;
    
    setButtonState('loading', 'Pinning to Canvas...');
    hideFeedback();

    const payload = {
      url: currentTab.url,
      title: title || null,
      workspaceId: workspaceId,
      spanX: selectedSpanX,
      spanY: selectedSpanY,
      thumbnailUrl: null // Clean, crisp 128x128 Favicon via WebFaviconService
    };

    // Smooth micro-interaction: keep spinner visible for at least 260ms so the user feels the action
    const minDelay = new Promise((resolve) => setTimeout(resolve, 260));

    try {
      const fetchPromise = fetch(`http://127.0.0.1:${activePort}/api/tiles`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-MetroHub-Client': CLIENT_HEADER
        },
        body: JSON.stringify(payload)
      });

      const [res] = await Promise.all([fetchPromise, minDelay]);
      const data = await res.json();

      setButtonState('default');

      if (res.ok && data.success) {
        const wsName = selectedWorkspaceName || (workspaceSelectedText?.textContent || 'Main').replace(/\s*\(Active\)\s*$/i, '').trim();
        if (data.duplicate) {
          showFeedback(
            'duplicate',
            'Already on Canvas',
            `${wsName} - Row ${data.row} - Col ${data.col}`
          );
        } else {
          showFeedback(
            'success',
            'Pinned Successfully',
            `${wsName} - Row ${data.row} - Col ${data.col}`
          );
        }
      } else {
        const errorMsg = data.error || data.message || 'Failed to place tile.';
        showFeedback('error', 'Unable to Pin Tile', errorMsg);
      }
    } catch (err) {
      setButtonState('default');
      showFeedback('error', 'Connection Lost', 'Is MetroHub desktop app running?');
    }
  });
}

function setButtonState(state, text = null) {
  if (!pinBtn) return;
  const btnText = pinBtn.querySelector('.btn-text');
  const spinner = pinBtn.querySelector('.btn-spinner');
  const pinSvg = pinBtn.querySelector('.pin-svg');
  const checkSvg = pinBtn.querySelector('.check-svg');

  if (state === 'loading') {
    pinBtn.disabled = true;
    pinSvg?.classList.add('hidden');
    checkSvg?.classList.add('hidden');
    spinner?.classList.remove('hidden');
    if (btnText) btnText.textContent = text || 'Pinning to Canvas...';
  } else {
    // default
    pinBtn.disabled = !activePort;
    pinSvg?.classList.remove('hidden');
    checkSvg?.classList.add('hidden');
    spinner?.classList.add('hidden');
    if (btnText) btnText.textContent = 'Pin to MetroHub';
  }
}

function setLoading(isLoading) {
  setButtonState(isLoading ? 'loading' : 'default');
}

function showFeedback(type, title, subtitle = '') {
  if (!feedbackBanner) return;
  feedbackBanner.className = `feedback-banner ${type}`;
  feedbackBanner.innerHTML = '';

  const titleEl = document.createElement('span');
  titleEl.className = 'feedback-text-title';
  titleEl.textContent = title;
  feedbackBanner.appendChild(titleEl);

  if (subtitle) {
    const subEl = document.createElement('span');
    subEl.className = 'feedback-text-sub';
    subEl.textContent = subtitle;
    feedbackBanner.appendChild(subEl);
  }

  feedbackBanner.classList.remove('hidden');
}

function hideFeedback() {
  if (!feedbackBanner) return;
  feedbackBanner.classList.add('hidden');
  feedbackBanner.innerHTML = '';
}

// =========================================================================
// Tactile Plug-and-Socket Connection Animation Engine
// =========================================================================
function setupPlugAnimation() {
  const cv = document.getElementById('plugCanvas');
  if (!cv) return;

  const W = 58;
  const H = Math.round(W * 65 / 140); // 27px
  const SS = Math.min(window.devicePixelRatio || 1, 2); // Capped at 2x for sharp retina rendering with low CPU/RAM
  const TAU = Math.PI * 2;
  const rnd = (a, b) => a + Math.random() * (b - a);
  const lerp = (a, b, t) => Math.round(a + (b - a) * t);
  const outC = t => 1 - Math.pow(1 - t, 3);
  const inC = t => t * t * t;
  const K = 0.35, Z0 = 13, SF = 86, SX1 = 112, PY = [29.5, 42.5], X_IN = 64, X_OUT = 30;
  const P = (x, y, z) => [x - K * z, y - K * z];

  cv.style.width = W + 'px';
  cv.style.height = H + 'px';
  cv.width = W * SS;
  cv.height = H * SS;

  const c = cv.getContext('2d');
  if (!c) return;
  c.imageSmoothingQuality = 'high';

  // Fallback for roundRect on older browser engines
  if (!CanvasRenderingContext2D.prototype.roundRect) {
    CanvasRenderingContext2D.prototype.roundRect = function (x, y, w, h, r) {
      const radius = typeof r === 'number' ? [r, r, r, r] : (r || [0, 0, 0, 0]);
      const [tl, tr, br, bl] = radius;
      this.moveTo(x + tl, y);
      this.lineTo(x + w - tr, y);
      this.quadraticCurveTo(x + w, y, x + w, y + tr);
      this.lineTo(x + w, y + h - br);
      this.quadraticCurveTo(x + w, y + h, x + w - br, y + h);
      this.lineTo(x + bl, y + h);
      this.quadraticCurveTo(x, y + h, x, y + h - bl);
      this.lineTo(x, y + tl);
      this.quadraticCurveTo(x, y, x + tl, y);
      return this;
    };
  }

  // Load remembered state for instantaneous 0ms first render
  let initialStatus = 'connected';
  try {
    const cached = localStorage.getItem('metrohub_companion_status');
    if (cached) initialStatus = cached;
  } catch { }

  const isInitiallyOn = initialStatus !== 'offline';
  const S = {
    state: isInitiallyOn ? 'on' : 'off',
    t: 0,
    plugX: isInitiallyOn ? X_IN : X_OUT,
    wob: 0,
    heat: 0,
    flame: 0,
    sep: !isInitiallyOn,
    arc: 0,
    amt: isInitiallyOn ? 1 : 0,
    surge: 0,
    fd: 0,
    dead: isInitiallyOn ? 0 : 1,
    crack: 0,
    crackT: 0.5,
    crackLane: 0,
    parts: [],
    clock: 0
  };

  const holes = () => PY.map(y => P(SF, y, Z0));

  function burst(x, y, n, cold) {
    for (let i = 0; i < n; i++) {
      const a = Math.PI + rnd(-1.4, 1.4);
      const sp = rnd(25, 95);
      const l = rnd(0.25, 0.7);
      S.parts.push({ k: 's', x, y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 12, l, m: l, cold });
    }
  }

  const go = s => {
    S.state = s;
    S.t = 0;
  };

  setPlugTargetState = function (target) {
    if (target === 'on') {
      if (S.state === 'off' || S.state === 'unplug') {
        go('plug');
      }
    } else if (target === 'off') {
      if (S.state === 'on' || S.state === 'plug') {
        S.sep = false;
        go('unplug');
      }
    }
  };

  function step(dt) {
    S.t += dt;
    S.clock += dt;
    S.wob *= Math.pow(0.02, dt);
    S.arc = 0;
    S.surge *= Math.pow(0.05, dt);
    S.fd += dt * 34 * (1 + S.surge * 2.2) * S.amt;
    const s = S.state;
    S.amt += ((s === 'on' ? 1 : 0) - S.amt) * Math.min(1, dt * (s === 'on' ? 10 : 7));
    S.dead += ((s === 'off' ? 1 : 0) - S.dead) * Math.min(1, dt * 3);

    if (s === 'on') {
      S.plugX = X_IN;
    } else if (s === 'unplug') {
      const k = Math.min(S.t / 0.6, 1);
      S.plugX = X_IN + (X_OUT - X_IN) * outC(k);
      if (!S.sep && S.plugX < 50) {
        S.sep = true;
        holes().forEach(h => burst(h[0], h[1], 10));
        S.wob = 1;
        S.flame = 1;
        S.heat = 1;
      }
      if (S.sep && S.plugX > 34) S.arc = 1;
      if (k >= 1) go('off');
    } else if (s === 'off') {
      S.plugX = X_OUT;
      S.crackT -= dt;
      if (S.crackT <= 0) {
        S.crack = 0.16;
        S.crackLane = Math.random() < 0.5 ? 0 : 1;
        S.crackT = rnd(0.5, 1.1);
      }
      S.crack = Math.max(0, S.crack - dt);
    } else if (s === 'plug') {
      const k = Math.min(S.t / 0.8, 1);
      S.plugX = X_OUT + (X_IN - X_OUT) * inC(k);
      if (S.plugX > 42 && k < 1) S.arc = 0.5;
      if (k >= 1) {
        S.sep = false;
        S.wob = 1.1;
        S.surge = 1;
        holes().forEach(h => burst(h[0], h[1], 6, true));
        go('on');
      }
    }

    S.heat = Math.max(0, S.heat - dt / 3);
    if (S.flame > 0) {
      S.flame = Math.max(0, S.flame - dt / 1.2);
      if (Math.random() < dt * 35 * S.flame) {
        const h = holes()[Math.random() < 0.5 ? 0 : 1];
        S.parts.push({ k: 'f', x: h[0] + rnd(-1, 1), y: h[1] + rnd(-1, 1), vx: rnd(-5, 2), vy: -rnd(10, 22), l: rnd(0.35, 0.7), m: 0.7, r: rnd(2, 3.6) });
      }
      if (Math.random() < dt * 8 * S.flame) {
        const h = holes()[0];
        S.parts.push({ k: 'e', x: h[0], y: h[1], vx: rnd(-6, 2), vy: -rnd(8, 18), l: rnd(0.8, 1.6), m: 1.6 });
      }
    }

    for (const p of S.parts) {
      p.l -= dt;
      p.x += p.vx * dt;
      p.y += p.vy * dt;
      if (p.k === 's') {
        p.vy += 240 * dt;
        p.vx *= 0.985;
      } else if (p.k === 'e') {
        p.vx += Math.sin(S.clock * 9 + p.m) * 14 * dt;
      }
    }
    S.parts = S.parts.filter(p => p.l > 0);
    if (S.parts.length > 25) S.parts = S.parts.slice(-25); // Strict memory cap
  }

  const lg = (x0, y0, x1, y1, st) => {
    const g = c.createLinearGradient(x0, y0, x1, y1);
    st.forEach((s, i) => g.addColorStop(i / (st.length - 1), s));
    return g;
  };

  const poly = (pts, fill) => {
    c.beginPath();
    pts.forEach((p, i) => (i ? c.lineTo(p[0], p[1]) : c.moveTo(p[0], p[1])));
    c.closePath();
    c.fillStyle = fill;
    c.fill();
  };

  const ellipse = (x, z, y0, r) => {
    const pts = [];
    for (let i = 0; i < 24; i++) {
      const a = (i / 24) * TAU;
      pts.push(P(x, y0 + r * Math.cos(a), z + r * Math.sin(a)));
    }
    return pts;
  };

  const bez = (p0, p1, p2, p3, u) => {
    const v = 1 - u, a = v * v * v, b = 3 * v * v * u, d = 3 * v * u * u, e = u * u * u;
    return [a * p0[0] + b * p1[0] + d * p2[0] + e * p3[0], a * p0[1] + b * p1[1] + d * p2[1] + e * p3[1]];
  };

  function arcLine(x0, y0, x1, y1, a) {
    c.save();
    c.globalCompositeOperation = 'lighter';
    c.lineJoin = 'round';
    for (const [w, col] of [[2.6, 'rgba(90,150,255,.22)'], [0.8, 'rgba(220,235,255,.9)']]) {
      c.strokeStyle = col;
      c.lineWidth = w;
      c.globalAlpha = a * (0.4 + Math.random() * 0.6);
      c.beginPath();
      c.moveTo(x0, y0);
      for (let i = 1; i < 5; i++) c.lineTo(x0 + (x1 - x0) * i / 5, y0 + (y1 - y0) * i / 5 + rnd(-2, 2));
      c.lineTo(x1, y1);
      c.stroke();
    }
    c.restore();
  }

  function draw() {
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.clearRect(0, 0, cv.width, cv.height);
    const sc = W * SS / 140;
    c.setTransform(sc, 0, 0, sc, 0, 0);
    const px = S.plugX, bob = S.state === 'off' ? Math.sin(S.clock * 3) * 0.7 : 0;
    const SX0 = SF, SY0 = 20, SY1 = 52, ZW = 26;
    const connected = S.state === 'on';

    // ---- socket ----
    poly([P(SX0, SY0, 0), P(SX0, SY1, 0), P(SX0, SY1, ZW), P(SX0, SY0, ZW)], lg(0, 0, 0, 60, ['#c9ccd4', '#a3a7b2']));
    poly([P(SX0, SY0, 0), P(SX1, SY0, 0), P(SX1, SY0, ZW), P(SX0, SY0, ZW)], lg(0, 5, 0, 20, ['#fbfbfd', '#e8e9ee']));
    poly([P(SX0, SY0, 0), P(SX1, SY0, 0), P(SX1, SY1, 0), P(SX0, SY1, 0)], lg(SX0, 0, SX1, 0, ['#eceef2', '#c5c8d1', '#9fa3af']));
    c.strokeStyle = 'rgba(255,255,255,.7)';
    c.lineWidth = 0.6;
    c.beginPath();
    c.moveTo(SX0, SY0 + 0.3);
    c.lineTo(SX1, SY0 + 0.3);
    c.stroke();
    c.strokeStyle = 'rgba(0,0,0,.25)';
    c.beginPath();
    c.moveTo(SX0, SY0);
    c.lineTo(SX0, SY1);
    c.stroke();

    // engraved channels on socket front
    const LY = PY.map(y => y - 1.75);
    c.lineCap = 'round';
    for (const ly of LY) {
      c.strokeStyle = 'rgba(20,22,28,.55)';
      c.lineWidth = 1.5;
      c.beginPath();
      c.moveTo(SX0 + 0.5, ly);
      c.lineTo(SX1 - 3, ly);
      c.stroke();
    }
    holes().forEach((hc, i) => {
      const y = PY[i];
      poly(ellipse(SF, Z0, y, 3.6), lg(0, 0, 0, 50, ['#8e929e', '#d9dbe2']));
      poly(ellipse(SF, Z0, y, 2.5), '#050607');
      const h = S.heat;
      c.save();
      if (h > 0.05) {
        c.shadowColor = '#ff7a1a';
        c.shadowBlur = 6 * h;
      }
      c.strokeStyle = `rgb(${lerp(150, 255, h)},${lerp(100, 110, h)},${lerp(45, 20, h)})`;
      c.lineWidth = 0.7;
      c.beginPath();
      ellipse(SF, Z0, y, 2.4).forEach((p, j) => (j ? c.lineTo(p[0], p[1]) : c.moveTo(p[0], p[1])));
      c.closePath();
      c.stroke();
      c.restore();

      // dead-socket warning: slow red breathing
      if (S.dead > 0.02) {
        const a = S.dead * (0.18 + 0.18 * Math.sin(S.clock * 2.6 + i * 1.6)), g = c.createRadialGradient(hc[0], hc[1], 0, hc[0], hc[1], 7);
        g.addColorStop(0, `rgba(255,60,50,${a})`);
        g.addColorStop(1, 'rgba(255,60,50,0)');
        c.save();
        c.globalCompositeOperation = 'lighter';
        c.fillStyle = g;
        c.fillRect(hc[0] - 8, hc[1] - 8, 16, 16);
        c.restore();
      }
    });

    // ---- cable (smoked tube) ----
    const Pc = P(px, 36, Z0), wy = S.wob * Math.sin(S.clock * 22) * 4, slack = (S.state === 'off' || S.state === 'unplug') ? 7 : 2;
    const p0 = [-14, Pc[1] + 4], p1 = [px * 0.35, Pc[1] + slack + wy], p2 = [px - 24, Pc[1] + slack * 1.3 - wy], p3 = [Pc[0] - 8, Pc[1] + bob];
    const cable = () => {
      c.beginPath();
      c.moveTo(p0[0], p0[1]);
      c.bezierCurveTo(p1[0], p1[1], p2[0], p2[1], p3[0], p3[1]);
    };
    c.save();
    c.translate(0, 1.4);
    cable();
    c.strokeStyle = 'rgba(0,0,0,.45)';
    c.lineWidth = 4.4;
    c.stroke();
    c.restore();
    cable();
    c.strokeStyle = '#9aa1b0';
    c.lineWidth = 4.2;
    c.stroke();
    cable();
    c.strokeStyle = '#232731';
    c.lineWidth = 3;
    c.stroke();
    c.save();
    c.translate(0, -0.9);
    cable();
    c.strokeStyle = 'rgba(255,255,255,.35)';
    c.lineWidth = 0.6;
    c.stroke();
    c.restore();

    // ---- plug body ----
    const PZ0 = 5, PZ1 = 21, Y0 = 24 + bob, Y1 = 48 + bob, X0 = px, X1 = px + 22;
    poly([P(X0, Y0, PZ0), P(X0, Y1, PZ0), P(X0, Y1, PZ1), P(X0, Y0, PZ1)], lg(0, Y0, 0, Y1, ['#c4c7cf', '#8e929e']));
    poly([P(X0, Y0, PZ0), P(X1, Y0, PZ0), P(X1, Y0, PZ1), P(X0, Y0, PZ1)], lg(0, Y0 - 8, 0, Y0, ['#ffffff', '#eceef2']));
    const f0 = P(X0, Y0, PZ0), f1 = P(X1, Y1, PZ0);
    c.save();
    c.beginPath();
    c.roundRect(f0[0], f0[1], f1[0] - f0[0], f1[1] - f0[1], 3);
    c.fillStyle = lg(0, f0[1], 0, f1[1], ['#f7f8fb', '#d3d6de', '#9ba0ac']);
    c.fill();
    c.clip();
    c.fillStyle = 'rgba(0,0,0,.18)';
    c.fillRect(f1[0] - 5, f0[1], 5, 24);
    c.fillStyle = 'rgba(255,255,255,.75)';
    c.fillRect(f0[0] + 2, f0[1] + 1, f1[0] - f0[0] - 4, 1.4);
    for (const ly of LY) {
      c.strokeStyle = 'rgba(20,22,28,.55)';
      c.lineWidth = 1.5;
      c.beginPath();
      c.moveTo(f0[0] + 1, ly + bob);
      c.lineTo(f1[0] - 1, ly + bob);
      c.stroke();
    }
    c.restore();
    c.beginPath();
    c.roundRect(Pc[0] - 8, Pc[1] + bob - 6, 9, 12, 3);
    c.fillStyle = lg(0, Pc[1] - 6, 0, Pc[1] + 6, ['#dfe1e7', '#8a8f9a']);
    c.fill();

    // ---- electrons: cable -> plug channel -> socket channel ----
    if (S.amt > 0.02) {
      c.save();
      c.globalCompositeOperation = 'lighter';
      for (let lane = 0; lane < 2; lane++) {
        const ly = LY[lane] + bob, pts = [];
        const sy = p3[1];
        for (let i = 0; i <= 30; i++) {
          const u = i / 30, b = bez(p0, p1, p2, p3, u);
          pts.push([b[0], b[1] + (ly - sy) * u * u * u + (lane ? 0.7 : -0.7) * (1 - u)]);
        }
        pts.push([f0[0] + 1, ly], [f1[0] - 1, ly]);
        if (connected || S.t < 0.01) {
          pts.push([SX0 + 0.5, LY[lane]], [SX1 - 3, LY[lane]]);
        }
        const cum = [0];
        for (let i = 1; i < pts.length; i++) cum.push(cum[i - 1] + Math.hypot(pts[i][0] - pts[i-1][0], pts[i][1] - pts[i-1][1]));
        const L = cum[cum.length - 1];
        for (let d = S.fd % 9; d < L; d += 9) {
          let j = 1;
          while (j < cum.length - 1 && cum[j] < d) j++;
          const t = (d - cum[j - 1]) / (cum[j] - cum[j - 1] || 1), x = pts[j - 1][0] + (pts[j][0] - pts[j - 1][0]) * t, y = pts[j - 1][1] + (pts[j][1] - pts[j - 1][1]) * t;
          const a = Math.min(1, d / 8, (L - d) / 8) * S.amt, tw = 0.75 + 0.25 * Math.sin(S.clock * 14 + d);
          const g = c.createRadialGradient(x, y, 0, x, y, 3);
          g.addColorStop(0, `rgba(150,235,255,${0.85 * a * tw})`);
          g.addColorStop(1, 'rgba(60,160,255,0)');
          c.fillStyle = g;
          c.fillRect(x - 3, y - 3, 6, 6);
          c.fillStyle = `rgba(255,255,255,${a})`;
          c.beginPath();
          c.arc(x, y, 0.7, 0, TAU);
          c.fill();
        }
      }
      c.restore();
    }

    // ---- pins (clipped at the socket face so they vanish into the holes) ----
    c.save();
    c.beginPath();
    c.rect(-50, -50, P(SF, 0, Z0)[0] + 50, 200);
    c.clip();
    for (const y of PY) {
      const a = P(X1, y + bob, Z0);
      c.fillStyle = lg(0, a[1] - 1.8, 0, a[1] + 1.8, ['#6e4e17', '#f8e3a0', '#c99a3a', '#7b5819']);
      c.beginPath();
      c.roundRect(a[0], a[1] - 1.8, 14, 3.6, 1.6);
      c.fill();
      c.fillStyle = '#2f3138';
      c.fillRect(a[0], a[1] - 1.9, 4, 3.8);
    }
    c.restore();

    // ---- arcs ----
    if (S.arc > 0) PY.forEach(y => {
      const a = P(px + 36, y + bob, Z0), h = P(SF, y, Z0);
      arcLine(a[0], a[1], h[0], h[1], S.arc);
    });
    // disconnected: current reaches out from a pin tip but never lands
    if (S.crack > 0) {
      const y = PY[S.crackLane], a = P(px + 36, y + bob, Z0), h = P(SF, y, Z0);
      arcLine(a[0], a[1], a[0] + (h[0] - a[0]) * 0.55, a[1] + (h[1] - a[1]) * 0.55, 0.9);
      const g = c.createRadialGradient(a[0], a[1], 0, a[0], a[1], 4);
      g.addColorStop(0, 'rgba(190,225,255,.8)');
      g.addColorStop(1, 'rgba(90,150,255,0)');
      c.save();
      c.globalCompositeOperation = 'lighter';
      c.fillStyle = g;
      c.fillRect(a[0] - 4, a[1] - 4, 8, 8);
      c.restore();
    }

    // ---- connected seam glow ----
    if (S.amt > 0.02) {
      const g = c.createRadialGradient(81, 37, 0, 81, 37, 16);
      g.addColorStop(0, `rgba(70,190,255,${0.14 * S.amt})`);
      g.addColorStop(1, 'rgba(70,190,255,0)');
      c.save();
      c.globalCompositeOperation = 'lighter';
      c.fillStyle = g;
      c.fillRect(60, 15, 40, 45);
      c.restore();
    }

    // ---- sparks, flame, embers ----
    c.save();
    c.globalCompositeOperation = 'lighter';
    for (const p of S.parts) {
      const f = Math.max(0, p.l / p.m);
      if (p.k === 's') {
        c.strokeStyle = p.cold ? `rgba(${lerp(90, 200, f)},${lerp(150, 230, f)},255,${f})` : f > 0.6 ? `rgba(255,246,200,${f})` : f > 0.3 ? `rgba(255,178,60,${f + 0.2})` : `rgba(255,90,31,${f + 0.2})`;
        c.lineWidth = 0.9;
        c.beginPath();
        c.moveTo(p.x, p.y);
        c.lineTo(p.x - p.vx * 0.035, p.y - p.vy * 0.035);
        c.stroke();
      } else if (p.k === 'f') {
        const r = p.r * (0.4 + f * 0.8), g = c.createRadialGradient(p.x, p.y, 0, p.x, p.y, r);
        g.addColorStop(0, `rgba(255,226,140,${f})`);
        g.addColorStop(0.5, `rgba(255,110,20,${f * 0.55})`);
        g.addColorStop(1, 'rgba(255,40,0,0)');
        c.fillStyle = g;
        c.beginPath();
        c.arc(p.x, p.y, r, 0, TAU);
        c.fill();
      } else {
        c.fillStyle = `rgba(255,${lerp(90, 190, f)},40,${f})`;
        c.fillRect(p.x - 0.5, p.y - 0.5, 1, 1);
      }
    }
    c.restore();
  }

  // Draw initial frame immediately (0ms delay)
  draw();

  // Motion preference & Loop execution
  const prefersReduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  if (prefersReduced) return;

  let animId = null;
  let last = performance.now();
  function loop(now) {
    const dt = Math.min((now - last) / 1000, 0.05);
    last = now;
    step(dt);
    draw();
    animId = requestAnimationFrame(loop);
  }
  animId = requestAnimationFrame(loop);

  function stopLoop() {
    if (animId) {
      cancelAnimationFrame(animId);
      animId = null;
    }
  }

  function resumeLoop() {
    if (!animId && !prefersReduced) {
      last = performance.now();
      animId = requestAnimationFrame(loop);
    }
  }

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) stopLoop();
    else resumeLoop();
  });
  window.addEventListener('pagehide', stopLoop);
  window.addEventListener('unload', stopLoop);
}
