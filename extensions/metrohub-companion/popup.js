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
let wire = null;

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
  setupWire();
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
    if (wire) setTimeout(() => wire.refreshTheme(), 10);
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

  modeSingleTabBtn.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
      e.preventDefault();
      switchMode('all');
      modeAllTabsBtn.focus();
    }
  });

  modeAllTabsBtn.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
      e.preventDefault();
      switchMode('single');
      modeSingleTabBtn.focus();
    }
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

let dropdownFocusedIndex = -1;

function updateWorkspaceTrigger(name) {
  if (!workspaceSelectedText) return;
  workspaceSelectedText.innerHTML = '';

  const nameSpan = document.createElement('span');
  nameSpan.className = 'ws-name';
  nameSpan.textContent = name;
  workspaceSelectedText.appendChild(nameSpan);
}

function openDropdown() {
  if (!workspaceTrigger || !workspaceMenu) return;
  workspaceTrigger.classList.add('open');
  workspaceMenu.classList.remove('hidden');
  workspaceTrigger.setAttribute('aria-expanded', 'true');

  const items = Array.from(workspaceMenu.querySelectorAll('.dropdown-item'));
  if (items.length === 0) return;

  const selectedIdx = items.findIndex((it) => it.getAttribute('aria-selected') === 'true');
  dropdownFocusedIndex = selectedIdx >= 0 ? selectedIdx : 0;
  highlightDropdownItem(dropdownFocusedIndex);
}

function closeDropdown() {
  if (!workspaceTrigger || !workspaceMenu) return;
  workspaceTrigger.classList.remove('open');
  workspaceMenu.classList.add('hidden');
  workspaceTrigger.setAttribute('aria-expanded', 'false');
  dropdownFocusedIndex = -1;
  workspaceMenu.querySelectorAll('.dropdown-item').forEach((it) => it.classList.remove('focused'));
}

function highlightDropdownItem(index) {
  if (!workspaceMenu) return;
  const items = Array.from(workspaceMenu.querySelectorAll('.dropdown-item'));
  if (items.length === 0) return;

  dropdownFocusedIndex = Math.max(0, Math.min(index, items.length - 1));
  items.forEach((it, i) => {
    it.classList.toggle('focused', i === dropdownFocusedIndex);
  });

  const focusedItem = items[dropdownFocusedIndex];
  if (focusedItem) {
    focusedItem.scrollIntoView({ block: 'nearest' });
  }
}

function setupDropdownListeners() {
  if (!workspaceTrigger || !workspaceMenu) return;

  workspaceTrigger.addEventListener('click', (e) => {
    e.stopPropagation();
    if (workspaceTrigger.classList.contains('open')) {
      closeDropdown();
    } else {
      openDropdown();
    }
  });

  workspaceTrigger.addEventListener('keydown', (e) => {
    const isOpen = workspaceTrigger.classList.contains('open');

    if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (!isOpen) {
        openDropdown();
      } else {
        const items = Array.from(workspaceMenu.querySelectorAll('.dropdown-item'));
        if (items.length > 0) {
          highlightDropdownItem((dropdownFocusedIndex + 1) % items.length);
        }
      }
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (!isOpen) {
        openDropdown();
      } else {
        const items = Array.from(workspaceMenu.querySelectorAll('.dropdown-item'));
        if (items.length > 0) {
          highlightDropdownItem((dropdownFocusedIndex - 1 + items.length) % items.length);
        }
      }
    } else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      if (isOpen) {
        const items = Array.from(workspaceMenu.querySelectorAll('.dropdown-item'));
        if (dropdownFocusedIndex >= 0 && dropdownFocusedIndex < items.length) {
          items[dropdownFocusedIndex].click();
        } else {
          closeDropdown();
        }
      } else {
        openDropdown();
      }
    } else if (e.key === 'Escape') {
      if (isOpen) {
        e.preventDefault();
        closeDropdown();
      }
    } else if (e.key === 'Tab') {
      if (isOpen) {
        closeDropdown();
      }
    } else if (e.key === 'Home' && isOpen) {
      e.preventDefault();
      highlightDropdownItem(0);
    } else if (e.key === 'End' && isOpen) {
      e.preventDefault();
      const items = workspaceMenu.querySelectorAll('.dropdown-item');
      if (items.length > 0) {
        highlightDropdownItem(items.length - 1);
      }
    }
  });

  document.addEventListener('click', (e) => {
    if (workspaceDropdown && !workspaceDropdown.contains(e.target)) {
      closeDropdown();
    }
  });
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
  setButtonState('offline');
}

function setConnectionStatus(type, label) {
  if (wire) {
    wire.setConnected(type === 'connected');
  }
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

  workspaces.forEach((ws, index) => {
    const isCurrentActive = Boolean(ws.isActive);
    if (isCurrentActive && !activeFound) {
      selectedWorkspaceId = ws.id;
      selectedWorkspaceName = ws.name;
      updateWorkspaceTrigger(ws.name);
      activeFound = true;
    }

    const isSelected = selectedWorkspaceId === ws.id;

    const item = document.createElement('button');
    item.type = 'button';
    item.className = `dropdown-item ${isSelected ? 'selected' : ''}`;
    item.setAttribute('role', 'option');
    item.setAttribute('aria-selected', String(isSelected));
    item.setAttribute('data-id', ws.id);
    item.setAttribute('data-index', String(index));
    item.tabIndex = -1;

    const nameSpan = document.createElement('span');
    nameSpan.className = 'ws-name';
    nameSpan.textContent = ws.name;
    item.appendChild(nameSpan);

    if (isCurrentActive) {
      const liveSpan = document.createElement('span');
      liveSpan.className = 'live';
      liveSpan.textContent = 'Active';
      item.appendChild(liveSpan);
    }

    item.addEventListener('mouseenter', () => {
      highlightDropdownItem(index);
    });

    item.addEventListener('click', (e) => {
      e.stopPropagation();
      selectedWorkspaceId = ws.id;
      selectedWorkspaceName = ws.name;
      updateWorkspaceTrigger(ws.name);

      workspaceMenu.querySelectorAll('.dropdown-item').forEach((el) => {
        const isMatch = el.getAttribute('data-id') === ws.id;
        el.classList.toggle('selected', isMatch);
        el.setAttribute('aria-selected', String(isMatch));
        el.classList.remove('focused');
      });

      closeDropdown();
      workspaceTrigger?.focus();
      setButtonState('default');
    });

    workspaceMenu.appendChild(item);
  });

  if (!activeFound && workspaces.length > 0) {
    selectedWorkspaceId = workspaces[0].id;
    selectedWorkspaceName = workspaces[0].name;
    updateWorkspaceTrigger(workspaces[0].name);
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

      const minDelay = new Promise((resolve) => setTimeout(resolve, 260));

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

        if (res.ok && data.success) {
          const wsName = selectedWorkspaceName || (workspaceSelectedText?.textContent || 'Main').replace(/\s*\(Active\)\s*$/i, '').trim();
          setButtonState(
            'done',
            `Stashed ${data.tilesAdded} Tabs`,
            `"${data.groupTitle}" • ${wsName}`
          );
        } else {
          const errorMsg = data.error || data.message || 'Failed to stash tabs into group.';
          setButtonState('error', 'Unable to Stash Tabs', errorMsg);
        }
      } catch (err) {
        setButtonState('error', 'Connection Lost', 'Is MetroHub desktop app running?');
      }
      return;
    }

    // Mode B: Single Current Tab Pin
    if (!currentTab || !currentTab.url) return;

    const title = tileTitleInput.value.trim();
    const workspaceId = selectedWorkspaceId || null;
    
    setButtonState('loading', 'Pinning to Canvas...', 'Connecting to MetroHub');
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

      if (res.ok && data.success) {
        const wsName = selectedWorkspaceName || (workspaceSelectedText?.textContent || 'Main').replace(/\s*\(Active\)\s*$/i, '').trim();
        if (data.duplicate) {
          setButtonState(
            'done',
            'Already on Canvas',
            `${wsName} • Row ${data.row}, Col ${data.col}`
          );
        } else {
          setButtonState(
            'done',
            'Pinned Successfully',
            `${wsName} • Row ${data.row}, Col ${data.col}`
          );
        }
      } else {
        const errorMsg = data.error || data.message || 'Failed to place tile.';
        setButtonState('error', 'Unable to Pin Tile', errorMsg);
      }
    } catch (err) {
      setButtonState('error', 'Connection Lost', 'Is MetroHub desktop app running?');
    }
  });
}

let buttonStateTimer = null;

function setButtonState(state, customBig = null, customSub = null) {
  if (!pinBtn) return;

  if (buttonStateTimer) {
    clearTimeout(buttonStateTimer);
    buttonStateTimer = null;
  }

  const bigEl = document.getElementById('pinBtnBig') || pinBtn.querySelector('.btn-big');
  const subEl = document.getElementById('pinBtnSub') || pinBtn.querySelector('.btn-sub');
  const icoPin = pinBtn.querySelector('.ico-pin');
  const icoStash = pinBtn.querySelector('.ico-stash');
  const icoOff = pinBtn.querySelector('.ico-off');
  const icoCheck = pinBtn.querySelector('.ico-check');
  const spinner = pinBtn.querySelector('.btn-spinner');

  function showIcon(target) {
    [icoPin, icoStash, icoOff, icoCheck, spinner].forEach((el) => {
      if (el) el.classList.add('hidden');
    });
    if (target) target.classList.remove('hidden');
  }

  const wsName = selectedWorkspaceName || (workspaceSelectedText?.querySelector('.ws-name')?.textContent || workspaceSelectedText?.textContent || 'Main').replace(/\s*\(Active\)\s*$/i, '').trim();

  // Reset state classes
  pinBtn.classList.remove('done', 'loading', 'error');

  if (state === 'loading') {
    pinBtn.disabled = true;
    pinBtn.classList.add('loading');
    showIcon(spinner);
    if (bigEl) {
      bigEl.textContent = customBig || (currentMode === 'all' ? `Stashing ${windowTabs.length} Tabs...` : 'Pinning to Canvas...');
    }
    if (subEl) {
      subEl.textContent = customSub || 'Connecting to MetroHub';
    }
  } else if (state === 'done' || state === 'success') {
    pinBtn.disabled = false;
    pinBtn.classList.add('done');
    showIcon(icoCheck);

    // Retrigger checkmark draw animation
    if (icoCheck) {
      const ck = icoCheck.querySelector('.ck');
      if (ck) {
        ck.style.animation = 'none';
        ck.offsetHeight; // trigger reflow
        ck.style.animation = '';
      }
    }

    if (bigEl) {
      bigEl.textContent = customBig || (currentMode === 'all' ? `Stashed ${windowTabs.length} Tabs` : 'Pinned Successfully');
    }
    if (subEl) {
      subEl.textContent = customSub || `to ${wsName}`;
    }

    // Auto-revert back to default state after 2600ms (matching widget_animation.html)
    buttonStateTimer = setTimeout(() => {
      setButtonState('default');
    }, 2600);
  } else if (state === 'error') {
    pinBtn.disabled = false;
    pinBtn.classList.add('error');
    showIcon(icoOff);

    if (bigEl) {
      bigEl.textContent = customBig || (currentMode === 'all' ? 'Unable to Stash Tabs' : 'Unable to Pin Tile');
    }
    if (subEl) {
      subEl.textContent = customSub || 'Failed to place tile';
    }

    // Auto-revert back to default (or offline) after 3000ms
    buttonStateTimer = setTimeout(() => {
      setButtonState(activePort ? 'default' : 'offline');
    }, 3000);
  } else if (state === 'offline' || !activePort) {
    pinBtn.disabled = true;
    showIcon(icoOff);

    if (bigEl) {
      bigEl.textContent = customBig || (currentMode === 'all' ? "Can't stash right now" : "Can't pin right now");
    }
    if (subEl) {
      subEl.textContent = customSub || 'Not connected to MetroHub';
    }
  } else {
    // Default (ready / connected)
    const isAllMode = currentMode === 'all';
    const noTabs = isAllMode && windowTabs.length === 0;

    if (noTabs) {
      pinBtn.disabled = true;
      showIcon(icoStash);
      if (bigEl) bigEl.textContent = customBig || 'No Tabs to Stash';
      if (subEl) subEl.textContent = customSub || 'Open tabs in current window';
      return;
    }

    pinBtn.disabled = false;

    if (isAllMode) {
      showIcon(icoStash);
      const count = windowTabs.length;
      if (bigEl) {
        bigEl.textContent = customBig || (count > 0 ? `Stash ${count} ${count === 1 ? 'Tab' : 'Tabs'}` : 'Stash Tabs to MetroHub');
      }
      if (subEl) {
        subEl.textContent = customSub || `to ${wsName}`;
      }
    } else {
      showIcon(icoPin);
      if (bigEl) {
        bigEl.textContent = customBig || 'Pin to MetroHub';
      }
      if (subEl) {
        subEl.textContent = customSub || `to ${wsName}`;
      }
    }
  }
}

function setLoading(isLoading) {
  setButtonState(isLoading ? 'loading' : 'default');
}

function showFeedback(type, title, subtitle = '') {
  // AIO Button handles all feedback directly on the tile - no extra banner box below
  if (type === 'success' || type === 'duplicate') {
    setButtonState('done', title, subtitle);
  } else if (type === 'error') {
    setButtonState('error', title, subtitle);
  } else {
    setButtonState('default');
  }
}

function hideFeedback() {
  if (buttonStateTimer) {
    clearTimeout(buttonStateTimer);
    buttonStateTimer = null;
  }
}

function setupWire() {
  const wireEl = document.getElementById('wire');
  if (!wireEl || typeof PlugWire === 'undefined') return;

  let initialConnected = false;
  try {
    const saved = localStorage.getItem('metrohub_companion_status');
    if (saved === 'connected') initialConnected = true;
  } catch { }

  wire = new PlugWire(wireEl, { connected: initialConnected });
}

/* ───────── PlugWire Component (Physics & Particles Rope) ───────── */
(() => {
  const REAR = 28, PRONG = 10, YP = 4.6;      // plug geometry (px)
  const N = 28, PAD = 16, H = 50;            // rope points, canvas overflow, stage height
  const CY = 24, FLOOR = CY + 14, GAP = 24;  // center Y, floor, disconnected gap
  const G = 1100;                            // rope gravity
  const rand  = (a, b) => a + Math.random() * (b - a);
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

  class PlugWire {
    constructor(root, opts = {}) {
      this.root = root;
      this.on = opts.connected !== false;
      this.t = 0; this.acc = 0; this.last = 0; this.W = 0;
      this.plug = { x: 0, v: 0 }; this.sock = { x: 0, v: 0 };
      this.kP = 240; this.zP = .8; this.kS = 190; this.zS = .55;
      this.angP = 0; this.angS = 0;
      this.sparks = []; this.arcs = []; this.pulses = [];
      this.flash = 0; this.energy = this.on ? 1 : 0; this.tension = this.on ? 1 : 0;
      this.apart = !this.on; this.sepDone = !this.on;
      this.popAt = 0; this.trail = 0; this.trailT = 0; this.zapAt = 0; this.arcDue = 0;
      this.flow = { busy: false, wait: .6 };
      this.mq = matchMedia('(prefers-reduced-motion: reduce)');
      this.build();
      this.refreshTheme();
      this.resize();
      this.label();
      this.ro = new ResizeObserver(() => this.resize());
      this.ro.observe(root);
      this.raf = requestAnimationFrame(t => this.loop(t));
    }

    /* ───── DOM ───── */
    build() {
      this.root.innerHTML = `
      <svg class="pw-svg" aria-hidden="true">
        <path class="pw-wire" data-w="L"/><path class="pw-wire" data-w="R"/>
        <g class="pw-sock">
          <rect class="pw-shape" x="21" y="-3" width="7" height="6" rx="2.2"/>
          <path class="pw-shape" d="M0 -10H10C19 -10 24 -6 24 0S19 10 10 10H0Z"/>
          <path class="pw-thin" d="M8 -6.6C14 -6.6 17.5 -3.6 17.5 0"/>
          <circle class="pw-dot" cx="12" cy="4.4" r="1"/>
          <path class="pw-hole" d="M0 -6.8H9.2a2.2 2.2 0 0 1 0 4.4H0"/>
          <path class="pw-hole" d="M0 2.4H9.2a2.2 2.2 0 0 1 0 4.4H0"/>
        </g>
        <g class="pw-plug">
          <rect class="pw-shape" x="-28" y="-3" width="7" height="6" rx="2.2"/>
          <path class="pw-shape" d="M0 -10H-10C-19 -10 -24 -6 -24 0S-19 10 -10 10H0Z"/>
          <path class="pw-thin" d="M-8 -6.6C-14 -6.6 -17.5 -3.6 -17.5 0"/>
          <circle class="pw-dot" cx="-12" cy="4.4" r="1"/>
          <rect class="pw-prong" x="-1" y="-6" width="11" height="2.8" rx="1.3"/>
          <rect class="pw-prong" x="-1" y="3.2" width="11" height="2.8" rx="1.3"/>
        </g>
      </svg>
      <canvas class="pw-fx" aria-hidden="true"></canvas>
      <span class="pw-sr" role="status"></span>`;
      const q = s => this.root.querySelector(s);
      this.root.style.height = H + 'px';
      this.svg = q('svg'); this.wL = q('[data-w=L]'); this.wR = q('[data-w=R]');
      this.gP = q('.pw-plug'); this.gS = q('.pw-sock');
      this.cv = q('canvas'); this.ctx = this.cv.getContext('2d');
      this.sr = q('.pw-sr');
    }
    label() { this.sr.textContent = this.on ? 'Connected' : 'Disconnected'; }

    refreshTheme() {
      const raw = getComputedStyle(this.root).getPropertyValue('--ink').trim();
      const ink = raw ? raw.split(/\s+/) : ['236', '236', '240'];
      this.rgb = ink.join(',');
      this.dark = (+ink[0]) > 128;
      this.cOk   = this.dark ? '74,222,128' : '22,163,74';     // plugged in
      this.cBad  = this.dark ? '255,92,80'  : '220,38,38';     // came apart
      this.cBad2 = this.dark ? '255,165,80' : '234,120,20';    // hotter edge of the sparks
    }

    /* ───── layout ───── */
    resize() {
      const w = Math.max(240, Math.round(this.root.clientWidth || 350));
      if (w === this.W) return;
      this.W = w; this.CX = w / 2;
      this.svg.setAttribute('width', w); this.svg.setAttribute('height', H);
      this.svg.setAttribute('viewBox', `0 0 ${w} ${H}`);
      this.dpr = Math.min(2, window.devicePixelRatio || 1);
      this.cv.width = w * this.dpr; this.cv.height = (H + PAD * 2) * this.dpr;
      this.cv.style.width = w + 'px'; this.cv.style.height = (H + PAD * 2) + 'px'; this.cv.style.top = -PAD + 'px';
      this.rL = this.makeRope(this.CX - REAR);
      this.rR = this.makeRope(w - (this.CX + REAR));
      this.snap();
    }
    makeRope(len) {
      const p = Array.from({ length: N }, () => ({ x: 0, y: CY, px: 0, py: CY }));
      return { p, total: len, seg: len / (N - 1), cum: new Float32Array(N), len };
    }
    targets() { const c = this.CX; return this.on ? [c, c] : [c - GAP, c + GAP]; }
    snap() {
      const [tp, ts] = this.targets();
      this.plug.x = tp; this.sock.x = ts; this.plug.v = this.sock.v = 0; this.tension = this.on ? 1 : 0;
      this.layout(this.rL, 0, tp - REAR); this.layout(this.rR, ts + REAR, this.W);
      for (let i = 0; i < 240; i++) this.stepRopes(1 / 120);
      this.measure(this.rL); this.measure(this.rR);
      this.angP = this.angS = 0;
    }
    layout(r, ax, bx) {
      const slack = Math.max(0, r.total - (bx - ax));
      const sag = Math.min(FLOOR - CY, Math.sqrt(3 * (bx - ax) * slack / 8));
      r.p.forEach((p, i) => {
        const u = i / (N - 1);
        p.x = p.px = ax + (bx - ax) * u; p.y = p.py = CY + sag * Math.sin(Math.PI * u);
      });
    }

    /* ───── state changes ───── */
    setConnected(on) {
      if (on === this.on) return;
      this.on = on; this.label();
      this.popAt = 0; this.trail = 0; this.arcDue = 0; this.arcs.length = 0;
      this.pulses.length = 0; this.flow.busy = false;
      if (this.mq.matches) { this.apart = !on; this.sepDone = !on; this.snap(); return; }
      if (on) {
        if (this.apart) { this.kP = 300; this.zP = .75; }
      } else {
        this.kP = 900; this.zP = 1; this.popAt = this.t + .11;
      }
    }

    /* ───── physics ───── */
    spring(o, tgt, k, z, h) {
      const c = 2 * z * Math.sqrt(k);
      o.v += (k * (tgt - o.x) - c * o.v) * h; o.x += o.v * h;
    }
    stepRope(r, ax, bx, h) {
      const P = r.p, n = P.length, seg = r.seg;
      for (let i = 1; i < n - 1; i++) {
        const p = P[i], vx = (p.x - p.px) * .992, vy = (p.y - p.py) * .992;
        p.px = p.x; p.py = p.y; p.x += vx; p.y += vy + G * (1 - .97 * this.tension) * h * h;
      }
      const pin = () => {
        P[0].x = P[0].px = ax; P[0].y = P[0].py = CY;
        P[n - 1].x = P[n - 1].px = bx; P[n - 1].y = P[n - 1].py = CY;
      };
      pin();
      for (let it = 0; it < 14; it++) {
        for (let i = 0; i < n - 1; i++) {
          const a = P[i], b = P[i + 1];
          const dx = b.x - a.x, dy = b.y - a.y, d = Math.hypot(dx, dy) || 1e-6, k = (d - seg) / d;
          const wa = i === 0 ? 0 : i === n - 2 ? 1 : .5, wb = i === 0 ? 1 : i === n - 2 ? 0 : .5;
          a.x += dx * k * wa; a.y += dy * k * wa; b.x -= dx * k * wb; b.y -= dy * k * wb;
        }
        for (let i = 0; i < n - 2; i++) {
          const a = P[i], c = P[i + 2];
          const dx = c.x - a.x, dy = c.y - a.y, d = Math.hypot(dx, dy) || 1e-6, min = 2 * seg * .95;
          if (d < min) {
            const k = (min - d) / d;
            let wa = i === 0 ? 0 : .5, wc = i + 2 === n - 1 ? 0 : .5;
            if (!wa) wc = 1; if (!wc) wa = 1;
            a.x -= dx * k * wa; a.y -= dy * k * wa; c.x += dx * k * wc; c.y += dy * k * wc;
          }
        }
        for (let i = 1; i < n - 1; i++) {
          const p = P[i];
          if (p.y > FLOOR) { p.y = FLOOR; p.px = p.x - (p.x - p.px) * .7; }
        }
        pin();
      }
    }
    stepRopes(h) {
      this.stepRope(this.rL, 0, this.plug.x - REAR, h);
      this.stepRope(this.rR, this.sock.x + REAR, this.W, h);
    }
    measure(r) {
      let s = 0; r.cum[0] = 0;
      for (let i = 1; i < N; i++) { s += Math.hypot(r.p[i].x - r.p[i - 1].x, r.p[i].y - r.p[i - 1].y); r.cum[i] = s; }
      r.len = s;
    }
    pointAt(r, s) {
      s = clamp(s, 0, r.len); let i = 1; const n = r.p.length;
      while (i < n - 1 && r.cum[i] < s) i++;
      const a = r.p[i - 1], b = r.p[i], span = r.cum[i] - r.cum[i - 1] || 1, u = (s - r.cum[i - 1]) / span;
      return [a.x + (b.x - a.x) * u, a.y + (b.y - a.y) * u];
    }
    shudder(amp) {
      for (const r of [this.rL, this.rR])
        r.p.forEach((p, i) => { p.py -= amp / 120 * Math.sin(Math.PI * i / (N - 1)); });
    }

    step(h) {
      this.t += h;
      const [tp, ts] = this.targets(), c = this.CX;
      let target = tp;
      if (this.popAt) target = c - 2.2;
      else if (this.on && this.apart) target = c + 18;
      if (this.popAt && this.t >= this.popAt) {
        this.popAt = 0; this.apart = true; this.sepDone = false;
        this.kP = 240; this.zP = .34; target = tp;
        this.plug.v = -640; this.sock.v = 110;
        this.zapAt = this.t + 3.4;
      }
      const tgoal = this.on && !this.apart && !this.popAt ? 1 : 0;
      this.tension += (tgoal - this.tension) * Math.min(1, h * (tgoal ? 7 : 30));
      this.spring(this.plug, target, this.kP, this.zP, h);
      this.spring(this.sock, ts, this.kS, this.on ? .9 : .42, h);

      const g = this.sock.x - this.plug.x;
      if (g < 0) {
        const vi = Math.max(0, this.plug.v - this.sock.v);
        this.plug.x = this.sock.x; this.plug.v = Math.min(this.plug.v, this.sock.v) * .1;
        if (this.on && this.apart) { this.apart = false; this.sock.v += vi * .22; this.contact(vi); this.kP = 240; this.zP = .8; }
      }
      if (!this.on && this.apart && !this.sepDone && g >= 9) { this.sepDone = true; this.separation(); }
      this.stepRopes(h);
    }

    /* ───── effects ───── */
    spark(x, y, ang, spd, life, w = 1.2, ember = false, col = this.rgb) {
      if (this.sparks.length > 140) return;
      this.sparks.push({ x, y, vx: Math.cos(ang) * spd, vy: Math.sin(ang) * spd, life, max: life, w, ember, col });
    }
    separation() {
      if (this.mq.matches) return;
      const x = this.sock.x;
      for (const y of [CY - YP, CY + YP]) {
        for (let i = 0; i < 7; i++) this.spark(x, y, Math.PI + rand(-.95, .95), rand(90, 300), rand(.25, .6), 1.2, false, Math.random() < .7 ? this.cBad : this.cBad2);
        for (let i = 0; i < 2; i++) this.spark(x, y, Math.PI + rand(-1.3, 1.3), rand(20, 70), rand(.8, 1.2), 1.6, true, this.cBad);
      }
      this.trail = .22; this.flash = .0; this.shudder(60);
    }
    contact(vi) {
      if (this.mq.matches) { this.startFlow(); return; }
      const n = Math.round(clamp(4 + vi / 70, 4, 10));
      for (const y of [CY - YP, CY + YP])
        for (let i = 0; i < n / 2; i++) this.spark(this.sock.x, y, rand(0, Math.PI * 2), rand(50, 210), rand(.18, .45), 1.1, false, Math.random() < .65 ? this.cOk : this.rgb);
      this.flash = 1; this.shudder(clamp(vi * .12, 20, 90));
      let left = 2; const done = () => { if (--left === 0) { this.flow.busy = false; this.flow.wait = .5; } };
      this.flow.busy = true;
      this.addPulse({ r: this.rL, dir: -1, dur: .62, tail: 90, power: 1, out: true, done });
      this.addPulse({ r: this.rR, dir: +1, dur: .62, tail: 90, power: 1, out: true, done });
    }
    startFlow() {
      this.flow.busy = true;
      this.addPulse({ r: this.rL, dir: 1, dur: 1.15, tail: 62, power: .95, next: () =>
        this.addPulse({ kind: 'pill', dir: 1, dur: .7, tail: 34, power: 1, next: () =>
          this.addPulse({ r: this.rR, dir: 1, dur: 1.15, tail: 62, power: .95,
            done: () => { this.flow.busy = false; this.flow.wait = 1.1; } }) }) });
    }
    addPulse(o) { this.pulses.push(Object.assign({ t: 0, head: 0, fired: false, rs: o.kind === 'pill' ? this.routes() : [o.r] }, o)); }
    routes() {
      const f = this.plug.x, s = this.sock.x, x0 = f - REAR, x1 = s + REAR;
      return [-1, 1].map(sg => {
        const y = CY + sg * YP;
        const p = [[x0, CY], [f - 11, CY], [f - 6, y], [s + PRONG + 1, y], [s + PRONG + 6, CY], [x1, CY]].map(([x, yy]) => ({ x, y: yy }));
        const cum = new Float32Array(p.length); let L = 0;
        for (let i = 1; i < p.length; i++) { L += Math.hypot(p[i].x - p[i - 1].x, p[i].y - p[i - 1].y); cum[i] = L; }
        return { p, cum, len: L };
      });
    }
    zap() {
      this.plug.v += 330;
      this.arcDue = this.t + .085;
    }

    fx(dt) {
      const goal = this.on && !this.popAt ? 1 : 0;
      this.energy += (goal - this.energy) * Math.min(1, dt * (goal ? 3 : 9));
      this.flash = Math.max(0, this.flash - dt * 2.2);
      if (this.mq.matches) return;

      if (this.on && !this.apart && !this.flow.busy) { this.flow.wait -= dt; if (this.flow.wait <= 0) this.startFlow(); }
      for (let i = this.pulses.length - 1; i >= 0; i--) {
        const p = this.pulses[i]; p.t += dt;
        p.rs = p.kind === 'pill' ? this.routes() : [p.r];
        const len = p.rs[0].len, q = clamp(p.t / p.dur, 0, 1);
        p.head = (p.out ? 1 - (1 - q) * (1 - q) : p.t / p.dur) * len;
        if (p.kind === 'pill' && p.head > len * .25 && p.head < len * .8) this.flash = Math.max(this.flash, .55);
        if (p.next && !p.fired && p.head >= len) { p.fired = true; p.next(); }
        if (p.next ? p.head >= len + p.tail : p.t >= p.dur) { this.pulses.splice(i, 1); p.done && p.done(); }
      }
      if (this.trail > 0) {
        this.trail -= dt; this.trailT -= dt;
        if (this.trailT <= 0) {
          this.trailT = .03;
          const y = CY + (Math.random() < .5 ? -YP : YP);
          this.spark(this.plug.x + PRONG, y, Math.PI + rand(-1, 1), rand(40, 140), rand(.15, .3), .9, false, this.cBad2);
        }
      }
      if (!this.on && this.apart && this.sepDone && this.t >= this.zapAt) { this.zapAt = this.t + rand(2.8, 5.5); this.zap(); }
      if (this.arcDue && this.t >= this.arcDue) {
        this.arcDue = 0;
        const y = Math.random() < .5 ? -YP : YP;
        this.arcs.push({ y, t: 0, dur: .17 });
        for (let i = 0; i < 4; i++) this.spark(this.sock.x, CY + y, Math.PI + rand(-1.3, 1.3), rand(50, 150), rand(.2, .4), 1, false, this.cBad);
        this.shudder(26);
      }
      for (let i = this.arcs.length - 1; i >= 0; i--) { this.arcs[i].t += dt; if (this.arcs[i].t > this.arcs[i].dur) this.arcs.splice(i, 1); }
      for (let i = this.sparks.length - 1; i >= 0; i--) {
        const s = this.sparks[i];
        s.life -= dt; if (s.life <= 0) { this.sparks.splice(i, 1); continue; }
        s.vy += (s.ember ? 160 : 560) * dt; s.vx *= 1 - 1.4 * dt;
        s.x += s.vx * dt; s.y += s.vy * dt;
        if (s.y > FLOOR + 3 && s.vy > 0) { s.y = FLOOR + 3; s.vy *= -.3; s.vx *= .6; }
      }
    }

    /* ───── drawing ───── */
    pathD(P) {
      let d = `M${P[0].x.toFixed(2)} ${P[0].y.toFixed(2)}`;
      for (let i = 1; i < P.length - 1; i++) {
        const mx = (P[i].x + P[i + 1].x) / 2, my = (P[i].y + P[i + 1].y) / 2;
        d += `Q${P[i].x.toFixed(2)} ${P[i].y.toFixed(2)} ${mx.toFixed(2)} ${my.toFixed(2)}`;
      }
      const l = P[P.length - 1];
      return d + `L${l.x.toFixed(2)} ${l.y.toFixed(2)}`;
    }
    edge(x) { return clamp(Math.min(x, this.W - x) / 24, 0, 1); }

    render(dt) {
      this.measure(this.rL); this.measure(this.rR);
      this.wL.setAttribute('d', this.pathD(this.rL.p)); this.wR.setAttribute('d', this.pathD(this.rR.p));
      const op = .3 + .3 * this.energy;
      this.wL.style.opacity = this.wR.style.opacity = op;

      const g = this.sock.x - this.plug.x, align = clamp(g / 22, 0, 1);
      const pl = this.rL.p, pr = this.rR.p;
      const aP = clamp(Math.atan2(pl[N - 1].y - pl[N - 2].y, pl[N - 1].x - pl[N - 2].x), -.20, .20) * align;
      const aS = clamp(Math.atan2(pr[1].y - pr[0].y, pr[1].x - pr[0].x), -.20, .20) * align;
      const k = Math.min(1, dt * 16);
      this.angP += (aP - this.angP) * k; this.angS += (aS - this.angS) * k;
      this.gP.setAttribute('transform', `translate(${(this.plug.x - REAR).toFixed(2)} ${CY}) rotate(${(this.angP * 57.3).toFixed(2)}) translate(${REAR} 0)`);
      this.gS.setAttribute('transform', `translate(${(this.sock.x + REAR).toFixed(2)} ${CY}) rotate(${(this.angS * 57.3).toFixed(2)}) translate(${-REAR} 0)`);

      this.draw();
    }
    draw() {
      const c = this.ctx, rgb = this.rgb;
      c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
      c.clearRect(0, 0, this.W, H + PAD * 2);
      c.translate(0, PAD); c.lineCap = 'round'; c.lineJoin = 'round';

      if (this.flash > .01) {
        const gr = c.createRadialGradient(this.CX, CY, 0, this.CX, CY, 38);
        gr.addColorStop(0, `rgba(${this.cOk},${(this.dark ? .3 : .16) * this.flash})`);
        gr.addColorStop(1, `rgba(${this.cOk},0)`);
        c.fillStyle = gr; c.fillRect(this.CX - 40, CY - 40, 80, 80);
      }
      c.shadowColor = `rgba(${this.cOk},.9)`; c.shadowBlur = 9;
      for (const p of this.pulses) this.drawPulse(c, p);

      for (const a of this.arcs) {
        const life = 1 - a.t / a.dur, bucket = Math.floor(this.t * 45);
        const x1 = this.plug.x + PRONG + 1, y1 = CY + a.y, x2 = this.sock.x + 1, y2 = y1;
        const trace = () => {
          c.beginPath(); c.moveTo(x1, y1);
          for (let i = 1; i < 5; i++) {
            const u = i / 5, r = Math.sin((bucket * 13.37 + i * 7.1)) * 43758.5453;
            c.lineTo(x1 + (x2 - x1) * u, y1 + ((r - Math.floor(r)) - .5) * 8 * Math.sin(Math.PI * u));
          }
          c.lineTo(x2, y2);
        };
        c.shadowColor = `rgba(${this.cBad},.9)`; c.shadowBlur = 10;
        trace(); c.strokeStyle = `rgba(${this.cBad},${.9 * life})`; c.lineWidth = 1.7; c.stroke();
        c.shadowBlur = 0;
        trace(); c.strokeStyle = `rgba(${this.dark ? '255,235,225' : this.cBad2},${.9 * life})`; c.lineWidth = .7; c.stroke();
      }
      for (const s of this.sparks) {
        const al = Math.pow(s.life / s.max, 1.2) * (s.ember ? .8 + .2 * Math.sin(this.t * 50 + s.x) : 1);
        c.shadowColor = `rgba(${s.col},.9)`; c.shadowBlur = 6;
        c.strokeStyle = c.fillStyle = `rgba(${s.col},${al})`;
        if (s.ember) { c.beginPath(); c.arc(s.x, s.y, s.w * .7, 0, 6.283); c.fill(); }
        else { c.lineWidth = s.w; c.beginPath(); c.moveTo(s.x - s.vx * .024, s.y - s.vy * .024); c.lineTo(s.x, s.y); c.stroke(); }
      }
      c.shadowBlur = 0;
    }
    drawPulse(c, p) {
      const K = 14, pill = p.kind === 'pill';
      for (const r of p.rs) {
        for (let k = 0; k < K; k++) {
          let s0 = p.head - p.tail * k / K, s1 = p.head - p.tail * (k + 1) / K;
          if (s0 < 0) continue; s1 = Math.max(0, s1); if (s1 > r.len) continue; s0 = Math.min(s0, r.len);
          const a0 = p.dir > 0 ? s0 : r.len - s0, a1 = p.dir > 0 ? s1 : r.len - s1;
          const A = this.pointAt(r, a0), B = this.pointAt(r, a1);
          const fade = pill ? .8 : this.edge((A[0] + B[0]) / 2);
          const al = p.power * this.energy * Math.pow(1 - k / K, 1.7) * fade;
          if (al < .01) continue;
          c.strokeStyle = `rgba(${this.cOk},${al})`; c.lineWidth = (pill ? 2 : 2.3) - .9 * k / K;
          c.beginPath(); c.moveTo(A[0], A[1]); c.lineTo(B[0], B[1]); c.stroke();
          if (k < 3 && this.dark) {
            c.strokeStyle = `rgba(235,255,242,${al * .75})`; c.lineWidth = .9;
            c.beginPath(); c.moveTo(A[0], A[1]); c.lineTo(B[0], B[1]); c.stroke();
          }
        }
      }
    }

    loop(now) {
      const dt = Math.min(.033, (now - (this.last || now)) / 1000); this.last = now;
      if (this.mq.matches) {
        this.snap(); this.fx(dt); this.render(dt);
      } else {
        this.acc += dt;
        while (this.acc >= 1 / 120) { this.step(1 / 120); this.acc -= 1 / 120; }
        this.fx(dt); this.render(dt);
      }
      this.raf = requestAnimationFrame(t => this.loop(t));
    }
    destroy() { cancelAnimationFrame(this.raf); this.ro.disconnect(); this.root.innerHTML = ''; }
  }

  window.PlugWire = PlugWire;
})();



