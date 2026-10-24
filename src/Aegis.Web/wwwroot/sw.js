// Minimal service worker: makes Aegis installable and lets the app shell open offline.
// It never touches /api, /share, or anything that is not a plain GET, so downloads and logins always hit the server.
const CACHE = 'aegis-shell-v1';
const SHELL = ['/', '/app.css', '/app.js', '/icons/icon-192.png', '/manifest.webmanifest'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== location.origin) return;
  if (url.pathname.startsWith('/api/') || url.pathname === '/share' || url.pathname === '/shortcut' || url.pathname === '/stats' || url.pathname === '/sw.js') return;

  if (req.mode === 'navigate') {
    // Network first; fall back to the cached shell when offline.
    event.respondWith(fetch(req).catch(() => caches.match('/')));
    return;
  }

  // Static assets: network first, refresh the cache, fall back to cache.
  event.respondWith(
    fetch(req).then((res) => {
      if (res.ok && !res.redirected) {
        const copy = res.clone();
        caches.open(CACHE).then((c) => c.put(req, copy));
      }
      return res;
    }).catch(() => caches.match(req)));
});
