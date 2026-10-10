// MetroHub Companion Background Service Worker
const PORTS = [48842, 48843, 48844, 48845, 48846];
const CLIENT_HEADER = 'BrowserExtension';

function setupContextMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: 'metrohub-pin-page',
      title: 'Send Page to MetroHub',
      contexts: ['page']
    });

    chrome.contextMenus.create({
      id: 'metrohub-pin-link',
      title: 'Send Link to MetroHub',
      contexts: ['link']
    });

    chrome.contextMenus.create({
      id: 'metrohub-pin-note',
      title: 'Send Note to MetroHub',
      contexts: ['selection']
    });

    chrome.contextMenus.create({
      id: 'metrohub-pin-image',
      title: 'Pin Image to MetroHub',
      contexts: ['image']
    });
  });
}

chrome.runtime.onInstalled.addListener(() => {
  setupContextMenus();
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  console.log('[MetroHub Background] Context menu action triggered:', info.menuItemId, { url: tab?.url, linkUrl: info.linkUrl, hasSelection: Boolean(info.selectionText), hasImage: Boolean(info.srcUrl) });
  if (info.menuItemId === 'metrohub-pin-page') {
    if (tab && tab.url) {
      await pinTile({ url: tab.url, title: tab.title });
    }
  } else if (info.menuItemId === 'metrohub-pin-link') {
    if (info.linkUrl) {
      await pinTile({ url: info.linkUrl, title: info.linkText || null });
    }
  } else if (info.menuItemId === 'metrohub-pin-note') {
    const selection = (info.selectionText || '').trim();
    if (selection) {
      await pinTile({
        note: selection.slice(0, 4000),
        url: tab?.url || info.pageUrl || null,
        title: tab?.title || null
      });
    }
  } else if (info.menuItemId === 'metrohub-pin-image') {
    if (info.srcUrl) {
      await pinTile({
        thumbnailUrl: info.srcUrl,
        url: tab?.url || info.pageUrl || null,
        title: tab?.title || 'Pinned Image'
      });
    }
  }
});

chrome.commands.onCommand.addListener(async (command) => {
  console.log('[MetroHub Background] Keyboard command triggered:', command);
  if (command === 'pin_tab') {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (tab && tab.url) {
      await pinTile({ url: tab.url, title: tab.title });
    }
  }
});

async function pinTile({ url = null, title = null, note = null, thumbnailUrl = null, spanX = 2, spanY = 2 } = {}) {
  const port = await getActivePort();
  if (!port) {
    console.error('[MetroHub Background] Pin failed: No active MetroHub port found (ports 48842-48846 offline).');
    flashBadge('ERR', '#E81123');
    return;
  }

  const payload = {
    url: url,
    title: title,
    note: note,
    thumbnailUrl: thumbnailUrl,
    spanX: spanX,
    spanY: spanY
  };

  console.log(`[MetroHub Background] Sending pin request to http://127.0.0.1:${port}/api/tiles:`, payload);

  try {
    const t0 = performance.now();
    const res = await fetch(`http://127.0.0.1:${port}/api/tiles`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-MetroHub-Client': CLIENT_HEADER
      },
      body: JSON.stringify(payload)
    });

    const elapsed = Math.round(performance.now() - t0);
    const data = await res.json().catch(() => ({}));

    console.log(`[MetroHub Background] Response received (${elapsed}ms, HTTP ${res.status}):`, data);

    if (res.ok && data.success) {
      if (data.duplicate) {
        console.warn('[MetroHub Background] Tile already exists in workspace:', data);
        flashBadge('DUP', '#F39C12');
      } else {
        console.log(`[MetroHub Background] Pin successful! Placed at Col ${data.col}, Row ${data.row}, TileId: ${data.tileId}`);
        flashBadge('OK', '#2ECC71');
      }
    } else {
      console.error(`[MetroHub Background] Pin rejected by desktop app (HTTP ${res.status}):`, data.error || data.message || 'Unknown error');
      flashBadge('ERR', '#E81123');
    }
  } catch (err) {
    console.error('[MetroHub Background] Network error dispatching pin request:', err);
    flashBadge('ERR', '#E81123');
  }
}

async function pinUrl(url, title) {
  return pinTile({ url, title });
}

async function getActivePort() {
  try {
    const cached = await chrome.storage.local.get('metrohub_active_port');
    if (cached && cached.metrohub_active_port) {
      if (await probePort(cached.metrohub_active_port, 250)) {
        return cached.metrohub_active_port;
      }
    }
  } catch { }

  const probePromises = PORTS.map(async (port) => {
    const ok = await probePort(port, 350);
    if (ok) return port;
    throw new Error('offline');
  });

  try {
    const foundPort = await Promise.any(probePromises);
    try {
      await chrome.storage.local.set({ metrohub_active_port: foundPort });
    } catch { }
    return foundPort;
  } catch {
    return null;
  }
}

async function probePort(port, timeoutMs = 350) {
  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    const res = await fetch(`http://127.0.0.1:${port}/api/health`, {
      method: 'GET',
      headers: { 'X-MetroHub-Client': CLIENT_HEADER },
      signal: controller.signal
    });
    clearTimeout(timer);
    if (res.ok) {
      const data = await res.json();
      return data.app === 'MetroHub';
    }
  } catch { }
  return false;
}

function flashBadge(text, color) {
  chrome.action.setBadgeText({ text });
  chrome.action.setBadgeBackgroundColor({ color });
  setTimeout(() => {
    chrome.action.setBadgeText({ text: '' });
  }, 2500);
}
