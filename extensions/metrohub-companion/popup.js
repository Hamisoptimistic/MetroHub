// MetroHub Companion WebExtension Popup Controller
const PORTS = [48842, 48843, 48844, 48845, 48846];
const CLIENT_HEADER = 'BrowserExtension';

let activePort = null;
let currentTab = null;
let selectedSpanX = 2;
let selectedSpanY = 2;
let selectedWorkspaceId = null;
let selectedWorkspaceName = 'Main';

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

  try {
    chrome.storage.local.get('metrohub_theme', (res) => {
      const theme = res && res.metrohub_theme ? res.metrohub_theme : 'light';
      applyTheme(theme);
      requestAnimationFrame(() => {
        document.body.classList.add('ready');
      });
    });
  } catch {
    applyTheme('light');
    document.body.classList.add('ready');
  }

  themeToggleBtn.addEventListener('click', () => {
    const current = document.documentElement.getAttribute('data-theme') || 'light';
    const next = current === 'light' ? 'dark' : 'light';

    // Trigger smooth window-wide theme transition (0.35s ease-in-out)
    document.documentElement.classList.add('theme-transitioning');
    applyTheme(next);

    setTimeout(() => {
      document.documentElement.classList.remove('theme-transitioning');
    }, 400);

    try {
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
  statusText.textContent = label;
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
