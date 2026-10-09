// MetroHub Companion Background Service Worker
const PORTS = [48842, 48843, 48844, 48845, 48846];
const CLIENT_HEADER = 'BrowserExtension';

chrome.runtime.onInstalled.addListener(() => {
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
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'metrohub-pin-page') {
    if (tab && tab.url) {
      await pinUrl(tab.url, tab.title);
    }
  } else if (info.menuItemId === 'metrohub-pin-link') {
    if (info.linkUrl) {
      await pinUrl(info.linkUrl, info.linkText || null);
    }
  }
});

chrome.commands.onCommand.addListener(async (command) => {
  if (command === 'pin_tab') {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (tab && tab.url) {
      await pinUrl(tab.url, tab.title);
    }
  }
});

async function pinUrl(url, title) {
  const port = await getActivePort();
  if (!port) {
    flashBadge('ERR', '#E81123');
    return;
  }

  try {
    const res = await fetch(`http://127.0.0.1:${port}/api/tiles`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-MetroHub-Client': CLIENT_HEADER
      },
      body: JSON.stringify({
        url: url,
        title: title || null,
        spanX: 2,
        spanY: 2,
        thumbnailUrl: null
      })
    });

    const data = await res.json();
    if (res.ok && data.success) {
      flashBadge('OK', '#2ECC71');
    } else {
      flashBadge('DUP', '#F39C12');
    }
  } catch (err) {
    flashBadge('ERR', '#E81123');
  }
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
