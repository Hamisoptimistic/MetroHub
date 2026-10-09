// MetroHub Companion WebExtension Popup Controller
const PORTS = [48842, 48843, 48844, 48845, 48846];
const CLIENT_HEADER = 'BrowserExtension';

let activePort = null;
let currentTab = null;
let currentMode = 'single'; // 'single' | 'all'
let windowTabs = [];
let cachedSingleTitle = '';
let cachedGroupName = '';
let defaultTileSize = '2x2';
let selectedSpanX = 2;
let selectedSpanY = 2;
let selectedWorkspaceId = null;
let selectedWorkspaceName = 'Main';

// DOM Elements
const statusBadge = document.getElementById('statusBadge');
const statusText = document.getElementById('statusText');
const menuToggleBtn = document.getElementById('menuToggleBtn');
const settingsMenu = document.getElementById('settingsMenu');
const menuSize2x2 = document.getElementById('menuSize2x2');
const menuSize4x2 = document.getElementById('menuSize4x2');
const openGitHubBtn = document.getElementById('openGitHubBtn');
const modeSwitcher = document.getElementById('modeSwitcher');
const modeSingleTabBtn = document.getElementById('modeSingleTab');
const modeAllTabsBtn = document.getElementById('modeAllTabs');
const allTabsLabelText = document.getElementById('allTabsLabelText');
const tileTitleLabel = document.getElementById('tileTitleLabel');
const tileTitleInput = document.getElementById('tileTitle');
const workspaceDropdown = document.getElementById('workspaceDropdown');
const workspaceTrigger = document.getElementById('workspaceTrigger');
const workspaceSelectedText = document.getElementById('workspaceSelectedText');
const workspaceMenu = document.getElementById('workspaceMenu');
const faviconCard = document.getElementById('faviconCard');
const faviconPreview = document.getElementById('faviconPreview');
const faviconDomain = document.getElementById('faviconDomain');
const sessionCard = document.getElementById('sessionCard');
const sessionSummaryText = document.getElementById('sessionSummaryText');
const sessionFaviconStack = document.getElementById('sessionFaviconStack');
const pinBtn = document.getElementById('pinBtn');
const feedbackBanner = document.getElementById('feedbackBanner');
const themeToggleBtn = document.getElementById('themeToggleBtn');

document.addEventListener('DOMContentLoaded', () => {
  setupTheme();
  setupSettingsMenu();
  setupModeSwitcher();
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

function setupModeSwitcher() {
  if (!modeSingleTabBtn || !modeAllTabsBtn) return;

  modeSingleTabBtn.addEventListener('click', () => {
    switchMode('single');
  });

  modeAllTabsBtn.addEventListener('click', () => {
    switchMode('all');
  });
}

function getDefaultGroupName() {
  const now = new Date();
  const monthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  const month = monthNames[now.getMonth()];
  const day = now.getDate();
  let hours = now.getHours();
  const minutes = now.getMinutes().toString().padStart(2, '0');
  const ampm = hours >= 12 ? 'PM' : 'AM';
  hours = hours % 12 || 12;
  return `Session • ${month} ${day}, ${hours}:${minutes} ${ampm}`;
}

function switchMode(mode) {
  if (currentMode === mode) return;
  currentMode = mode;

  hideFeedback();
  modeSwitcher?.setAttribute('data-active', mode);

  if (mode === 'all') {
    modeSingleTabBtn?.classList.remove('active');
    modeSingleTabBtn?.setAttribute('aria-selected', 'false');
    modeAllTabsBtn?.classList.add('active');
    modeAllTabsBtn?.setAttribute('aria-selected', 'true');

    // Morph title into group name
    cachedSingleTitle = tileTitleInput ? tileTitleInput.value : '';
    if (tileTitleLabel) tileTitleLabel.textContent = 'Group Name';
    if (tileTitleInput) {
      tileTitleInput.placeholder = 'e.g. Work Session';
      tileTitleInput.value = cachedGroupName || getDefaultGroupName();
    }

    // Switch preview card to group summary card
    faviconCard?.classList.add('hidden');
    sessionCard?.classList.remove('hidden');

    updateAllTabsSummary();
    setButtonState('default');
  } else {
    modeAllTabsBtn?.classList.remove('active');
    modeAllTabsBtn?.setAttribute('aria-selected', 'false');
    modeSingleTabBtn?.classList.add('active');
    modeSingleTabBtn?.setAttribute('aria-selected', 'true');

    // Morph group name back to single tab title
    cachedGroupName = tileTitleInput ? tileTitleInput.value : '';
    if (tileTitleLabel) tileTitleLabel.textContent = 'Title';
    if (tileTitleInput) {
      tileTitleInput.placeholder = 'Page title';
      tileTitleInput.value = cachedSingleTitle || (currentTab ? currentTab.title : '');
    }

    // Switch preview card back to single favicon preview
    sessionCard?.classList.add('hidden');
    faviconCard?.classList.remove('hidden');

    setButtonState('default');
  }
}

function updateAllTabsSummary() {
  const count = windowTabs.length;
  if (allTabsLabelText) {
    allTabsLabelText.textContent = count > 0 ? `All Tabs (${count})` : 'All Tabs';
  }
  if (sessionSummaryText) {
    sessionSummaryText.textContent = count === 1 ? '1 tab will be grouped' : `${count} tabs will be grouped`;
  }
  renderFaviconStack();
}

function renderFaviconStack() {
  if (!sessionFaviconStack) return;
  sessionFaviconStack.innerHTML = '';

  const total = windowTabs.length;
  if (total === 0) return;

  const maxVisible = 12;
  const renderCount = total > maxVisible ? maxVisible - 1 : total;
  const remaining = total - renderCount;

  for (let i = 0; i < renderCount; i++) {
    const tab = windowTabs[i];
    let domain = '';
    try {
      domain = new URL(tab.url).hostname.replace(/^www\./, '');
    } catch { }

    const item = document.createElement('div');
    item.className = 'session-favicon-item dealing';
    item.style.animationDelay = `${i * 36}ms`;
    // Facing left: leftmost tab has highest z-index so subsequent tabs tuck underneath it to the right
    item.style.zIndex = String(total - i);
    item.setAttribute('title', tab.title ? `${tab.title} (${domain || tab.url})` : (domain || 'Tab'));

    const img = document.createElement('img');
    img.alt = domain || 'Tab';
    const fallbackUrl = domain ? `https://www.google.com/s2/favicons?domain=${domain}&sz=64` : '';
    img.onerror = () => {
      if (fallbackUrl && img.src !== fallbackUrl) {
        img.src = fallbackUrl;
      } else {
        img.style.display = 'none';
        const fallbackSvg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        fallbackSvg.setAttribute('width', '18');
        fallbackSvg.setAttribute('height', '18');
        fallbackSvg.setAttribute('viewBox', '0 0 24 24');
        fallbackSvg.setAttribute('fill', 'none');
        fallbackSvg.setAttribute('stroke', 'currentColor');
        fallbackSvg.setAttribute('stroke-width', '2');
        fallbackSvg.innerHTML = '<circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>';
        fallbackSvg.style.color = '#4b5563';
        item.appendChild(fallbackSvg);
      }
    };
    img.src = tab.favIconUrl || fallbackUrl;

    item.addEventListener('animationend', () => item.classList.remove('dealing'), { once: true });

    item.appendChild(img);
    sessionFaviconStack.appendChild(item);
  }

  if (remaining > 0) {
    const moreItem = document.createElement('div');
    moreItem.className = 'session-favicon-more dealing';
    moreItem.style.animationDelay = `${renderCount * 36}ms`;
    moreItem.style.zIndex = '1';
    moreItem.textContent = `+${remaining}`;
    moreItem.setAttribute('title', `${remaining} more tabs`);
    moreItem.addEventListener('animationend', () => moreItem.classList.remove('dealing'), { once: true });
    sessionFaviconStack.appendChild(moreItem);
  }
}

function setupSettingsMenu() {
  if (!menuToggleBtn || !settingsMenu) return;

  function applyDefaultSize(size) {
    defaultTileSize = size;
    if (size === '4x2') {
      selectedSpanX = 4;
      selectedSpanY = 2;
      menuSize4x2?.classList.add('active');
      menuSize4x2?.setAttribute('aria-checked', 'true');
      menuSize4x2?.querySelector('.check-icon')?.classList.remove('hidden');
      menuSize2x2?.classList.remove('active');
      menuSize2x2?.setAttribute('aria-checked', 'false');
      menuSize2x2?.querySelector('.check-icon')?.classList.add('hidden');
    } else {
      selectedSpanX = 2;
      selectedSpanY = 2;
      menuSize2x2?.classList.add('active');
      menuSize2x2?.setAttribute('aria-checked', 'true');
      menuSize2x2?.querySelector('.check-icon')?.classList.remove('hidden');
      menuSize4x2?.classList.remove('active');
      menuSize4x2?.setAttribute('aria-checked', 'false');
      menuSize4x2?.querySelector('.check-icon')?.classList.add('hidden');
    }
  }

  // 1. Initial size from localStorage
  let savedSize = '2x2';
  try {
    const val = localStorage.getItem('metrohub_default_tile_size');
    if (val === '2x2' || val === '4x2') savedSize = val;
  } catch { }
  applyDefaultSize(savedSize);

  // 2. Storage sync
  try {
    chrome.storage.local.get('metrohub_default_tile_size', (res) => {
      if (res && res.metrohub_default_tile_size && res.metrohub_default_tile_size !== savedSize) {
        applyDefaultSize(res.metrohub_default_tile_size);
        try { localStorage.setItem('metrohub_default_tile_size', res.metrohub_default_tile_size); } catch { }
      }
    });
  } catch { }

  // 3. Option clicks
  menuSize2x2?.addEventListener('click', (e) => {
    e.stopPropagation();
    applyDefaultSize('2x2');
    try {
      localStorage.setItem('metrohub_default_tile_size', '2x2');
      chrome.storage.local.set({ metrohub_default_tile_size: '2x2' });
    } catch { }
  });

  menuSize4x2?.addEventListener('click', (e) => {
    e.stopPropagation();
    applyDefaultSize('4x2');
    try {
      localStorage.setItem('metrohub_default_tile_size', '4x2');
      chrome.storage.local.set({ metrohub_default_tile_size: '4x2' });
    } catch { }
  });

  // 4. Menu toggle
  menuToggleBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    const isClosed = settingsMenu.classList.contains('hidden');
    settingsMenu.classList.toggle('hidden', !isClosed);
    menuToggleBtn.classList.toggle('active', isClosed);
    menuToggleBtn.setAttribute('aria-expanded', String(isClosed));
  });

  // 5. Click outside to close
  document.addEventListener('click', (e) => {
    if (!settingsMenu.classList.contains('hidden')) {
      if (!settingsMenu.contains(e.target) && !menuToggleBtn.contains(e.target)) {
        settingsMenu.classList.add('hidden');
        menuToggleBtn.classList.remove('active');
        menuToggleBtn.setAttribute('aria-expanded', 'false');
      }
    }
  });

  // 6. Open MetroHub GitHub Repository
  openGitHubBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    settingsMenu.classList.add('hidden');
    menuToggleBtn.classList.remove('active');
    menuToggleBtn.setAttribute('aria-expanded', 'false');

    const GITHUB_REPO_URL = 'https://github.com/Hamisoptimistic/MetroHub';
    const tabsApi = (typeof browser !== 'undefined' && browser.tabs) ? browser.tabs : chrome.tabs;
    if (tabsApi?.create) {
      tabsApi.create({ url: GITHUB_REPO_URL });
    } else {
      window.open(GITHUB_REPO_URL, '_blank', 'noopener,noreferrer');
    }
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
    // 1. Current active tab context
    const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
    if (tabs && tabs.length > 0) {
      currentTab = tabs[0];
      cachedSingleTitle = currentTab.title || '';
      if (currentMode === 'single' && tileTitleInput) {
        tileTitleInput.value = cachedSingleTitle;
      }

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
    }

    // 2. Query all tabs in the active window for session stashing
    const allTabs = await chrome.tabs.query({ currentWindow: true });
    if (Array.isArray(allTabs)) {
      windowTabs = allTabs.filter(t => t.url && (t.url.startsWith('http://') || t.url.startsWith('https://')));
      updateAllTabsSummary();
      setButtonState('default');
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
  if (statusBadge) {
    statusBadge.className = `status-badge ${type}`;
    statusBadge.setAttribute('title', `MetroHub: ${label}`);
    statusBadge.setAttribute('aria-label', `MetroHub: ${label}`);
  }
  if (statusText) {
    statusText.textContent = label;
  }

  try {
    localStorage.setItem('metrohub_companion_status', type);
    chrome.storage.local.set({ metrohub_companion_status: type });
  } catch { }
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
    if (!activePort) return;

    // Mode A: Stash All Tabs into Desktop Tile Group
    if (currentMode === 'all') {
      if (windowTabs.length === 0) return;

      const groupName = tileTitleInput.value.trim() || getDefaultGroupName();
      const workspaceId = selectedWorkspaceId || null;

      setButtonState('loading', `Stashing ${windowTabs.length} Tabs...`);
      hideFeedback();

      const payload = {
        groupName: groupName,
        workspaceId: workspaceId,
        tiles: windowTabs.map((t) => ({
          url: t.url,
          title: t.title || null
        }))
      };

      // Trigger Vault Stash absorption animation on all discs
      const items = Array.from(sessionFaviconStack.querySelectorAll('.session-favicon-item, .session-favicon-more'));
      items.forEach((it, i) => {
        it.classList.remove('dealing');
        it.style.animationDelay = `${i * 36}ms`;
        it.classList.add('absorbing');
      });

      const cascadeTime = Math.max(300, (items.length * 36) + 240);
      const minDelay = new Promise((resolve) => setTimeout(resolve, cascadeTime));

      try {
        const fetchPromise = fetch(`http://127.0.0.1:${activePort}/api/tile-groups`, {
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
          pinBtn.classList.add('pulse-absorbed');
          setTimeout(() => pinBtn.classList.remove('pulse-absorbed'), 500);

          const wsName = selectedWorkspaceName || (workspaceSelectedText?.textContent || 'Main').replace(/\s*\(Active\)\s*$/i, '').trim();
          showFeedback(
            'success',
            `Stashed ${data.tilesAdded} Tabs`,
            `"${data.groupTitle}" • ${wsName}`
          );
        } else {
          items.forEach(it => it.classList.remove('absorbing'));
          const errorMsg = data.error || data.message || 'Failed to stash tabs into group.';
          showFeedback('error', 'Unable to Stash Tabs', errorMsg);
        }
      } catch (err) {
        items.forEach(it => it.classList.remove('absorbing'));
        setButtonState('default');
        showFeedback('error', 'Connection Lost', 'Is MetroHub desktop app running?');
      }
      return;
    }

    // Mode B: Single Current Tab Pin
    if (!currentTab || !currentTab.url) return;

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
  const stashSvg = pinBtn.querySelector('.stash-svg');
  const checkSvg = pinBtn.querySelector('.check-svg');

  if (state === 'loading') {
    pinBtn.disabled = true;
    pinSvg?.classList.add('hidden');
    stashSvg?.classList.add('hidden');
    checkSvg?.classList.add('hidden');
    spinner?.classList.remove('hidden');
    if (btnText) {
      btnText.textContent = text || (currentMode === 'all' ? 'Stashing Tabs...' : 'Pinning to Canvas...');
    }
  } else {
    // default
    pinBtn.disabled = !activePort || (currentMode === 'all' && windowTabs.length === 0);
    checkSvg?.classList.add('hidden');
    spinner?.classList.add('hidden');

    if (currentMode === 'all') {
      pinSvg?.classList.add('hidden');
      stashSvg?.classList.remove('hidden');
      const count = windowTabs.length;
      if (btnText) {
        btnText.textContent = count > 0 ? `Stash ${count} Tabs to MetroHub` : 'Stash Tabs to MetroHub';
      }
    } else {
      stashSvg?.classList.add('hidden');
      pinSvg?.classList.remove('hidden');
      if (btnText) {
        btnText.textContent = 'Pin to MetroHub';
      }
    }
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


