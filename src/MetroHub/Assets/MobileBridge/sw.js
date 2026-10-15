/* MetroHub Phone Bridge Service Worker
 *
 * Handles PWA caching and Web Share Target Level 2 for photos, links, and text.
 */
const CACHE = "metrohub-shell-v4";
const SHELL = [
  "./",
  "app.css",
  "app.js",
  "manifest.webmanifest",
  "icon-192.png",
  "icon-512.png"
];

const DB_NAME = "metrohub-share-db";
const STORE_NAME = "shares";

function saveSharedData(data) {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = (e) => {
      const db = e.target.result;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        db.createObjectStore(STORE_NAME, { keyPath: "id" });
      }
    };
    req.onsuccess = (e) => {
      const db = e.target.result;
      const tx = db.transaction(STORE_NAME, "readwrite");
      tx.objectStore(STORE_NAME).put(data);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    };
    req.onerror = () => reject(req.error);
  });
}

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(CACHE)
      .then((cache) => cache.addAll(SHELL))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((key) => key !== CACHE).map((key) => caches.delete(key))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  const url = new URL(request.url);

  // Intercept Web Share Target Level 2 POST
  if (request.method === "POST" && (url.pathname.endsWith("/share-target") || url.pathname.endsWith("share-target"))) {
    event.respondWith((async () => {
      try {
        const formData = await request.formData();
        const files = formData.getAll("photos");
        const title = formData.get("title") || "";
        const text = formData.get("text") || "";
        const shareUrl = formData.get("url") || "";

        await saveSharedData({
          id: "pending",
          title,
          text,
          url: shareUrl,
          files: files && files.length > 0 ? files : null,
          timestamp: Date.now()
        });
      } catch (err) {
        console.error("[MetroHub SW] Failed to process share target:", err);
      }

      return Response.redirect("./?shared=1", 303);
    })());
    return;
  }

  if (request.method !== "GET") {
    return;
  }

  // Never touch local LAN API calls
  if (url.pathname.includes("/api/")) {
    return;
  }

  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request).catch(() => caches.match("./") || caches.match("index.html"))
    );
    return;
  }

  event.respondWith(
    caches.match(request).then((hit) => hit || fetch(request))
  );
});
