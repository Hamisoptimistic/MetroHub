// MetroHub Companion WebExtension Popup Controller
const PORTS = [48842, 48843, 48844, 48845, 48846];
const CLIENT_HEADER = 'BrowserExtension';

let activePort = null;
let currentTab = null;
let selectedSpanX = 2;
let selectedSpanY = 2;

// DOM Elements
const statusBadge = document.getElementById('statusBadge');
const statusText = document.getElementById('statusText');
const tileTitleInput = document.getElementById('tileTitle');
const workspaceSelect = document.getElementById('workspaceSelect');
const size2x2Btn = document.getElementById('size2x2');
const size4x2Btn = document.getElementById('size4x2');
const faviconPreview = document.getElementById('faviconPreview');
const faviconDomain = document.getElementById('faviconDomain');
const pinBtn = document.getElementById('pinBtn');
const feedbackBanner = document.getElementById('feedbackBanner');

document.addEventListener('DOMContentLoaded', async () => {
  setupSizeButtons();
  setupPinButton();

  // 1. Inspect Active Tab & Extract Favicon Context
  await initializeTabContext();

  // 2. Discover Running MetroHub Desktop Companion Port
  await discoverCompanionPort();
});

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

  // Check cached session port first
  try {
    const cached = await chrome.storage.session.get('metrohub_active_port');
    if (cached && cached.metrohub_active_port) {
      if (await probePort(cached.metrohub_active_port)) {
        activePort = cached.metrohub_active_port;
        onConnected();
        return;
      }
    }
  } catch { }

  // Sequential port probe
  for (const port of PORTS) {
    if (await probePort(port)) {
      activePort = port;
      try {
        await chrome.storage.session.set({ metrohub_active_port: port });
      } catch { }
      onConnected();
      return;
    }
  }

  // Not found
  onDisconnected();
}

async function probePort(port) {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/api/health`, {
      method: 'GET',
      headers: {
        'X-MetroHub-Client': CLIENT_HEADER
      }
    });

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
  pinBtn.disabled = false;
  loadWorkspaces();
}

function onDisconnected() {
  setConnectionStatus('offline', 'Offline');
  pinBtn.disabled = true;
  showFeedback('error', 'MetroHub is not running. Please start MetroHub desktop app.');
}

function setConnectionStatus(type, label) {
  statusBadge.className = `status-indicator ${type}`;
  statusText.textContent = label;
}

async function loadWorkspaces() {
  if (!activePort) return;

  try {
    const res = await fetch(`http://127.0.0.1:${activePort}/api/workspaces`, {
      method: 'GET',
      headers: {
        'X-MetroHub-Client': CLIENT_HEADER
      }
    });

    if (res.ok) {
      const workspaces = await res.json();
      workspaceSelect.innerHTML = '';

      let hasActive = false;
      for (const ws of workspaces) {
        const opt = document.createElement('option');
        opt.value = ws.id;
        opt.textContent = ws.isActive ? `${ws.name} (Active)` : ws.name;
        if (ws.isActive) {
          opt.selected = true;
          hasActive = true;
        }
        workspaceSelect.appendChild(opt);
      }

      if (!hasActive && workspaces.length > 0) {
        workspaceSelect.selectedIndex = 0;
      }
    }
  } catch (err) {
    console.warn('[MetroHub] Error fetching workspaces:', err);
  }
}

function setupPinButton() {
  pinBtn.addEventListener('click', async () => {
    if (!activePort || !currentTab || !currentTab.url) return;

    const title = tileTitleInput.value.trim();
    const workspaceId = workspaceSelect.value || null;
    setLoading(true);
    hideFeedback();

    const payload = {
      url: currentTab.url,
      title: title || null,
      workspaceId: workspaceId,
      spanX: selectedSpanX,
      spanY: selectedSpanY,
      thumbnailUrl: null // Clean, crisp 128x128 Favicon via WebFaviconService
    };

    try {
      const res = await fetch(`http://127.0.0.1:${activePort}/api/tiles`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-MetroHub-Client': CLIENT_HEADER
        },
        body: JSON.stringify(payload)
      });

      const data = await res.json();

      if (res.ok && data.success) {
        if (data.duplicate) {
          showFeedback('duplicate', `Tile already exists on canvas (Col ${data.col}, Row ${data.row})`);
        } else {
          showFeedback('success', `✓ Pinned to MetroHub! (Col ${data.col}, Row ${data.row})`);
          setTimeout(() => {
            window.close();
          }, 1400);
        }
      } else {
        const errorMsg = data.error || data.message || 'Failed to place tile.';
        showFeedback('error', errorMsg);
      }
    } catch (err) {
      showFeedback('error', 'Connection lost. Is MetroHub running?');
    } finally {
      setLoading(false);
    }
  });
}

function setLoading(isLoading) {
  pinBtn.disabled = isLoading;
  const btnText = pinBtn.querySelector('.btn-text');
  const spinner = pinBtn.querySelector('.btn-spinner');

  if (isLoading) {
    btnText.textContent = 'Pinning...';
    spinner.classList.remove('hidden');
  } else {
    btnText.textContent = 'Pin to MetroHub';
    spinner.classList.add('hidden');
  }
}

function showFeedback(type, message) {
  feedbackBanner.className = `feedback-banner ${type}`;
  feedbackBanner.textContent = message;
  feedbackBanner.classList.remove('hidden');
}

function hideFeedback() {
  feedbackBanner.classList.add('hidden');
}
