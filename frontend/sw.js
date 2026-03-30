const CACHE = 'chatapp-v5';
const SHELL = ['/', '/styles.css', '/app.js', '/icon.svg', '/manifest.json'];

// Pre-cache app shell on install
self.addEventListener('install', e => {
  e.waitUntil(
    caches.open(CACHE).then(c => c.addAll(SHELL))
  );
  self.skipWaiting();
});

// Remove old caches on activate
self.addEventListener('activate', e => {
  e.waitUntil(
    caches.keys().then(keys =>
      Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)))
    )
  );
  self.clients.claim();
});

// Fetch strategy:
//   API / SignalR     → network only (never cache)
//   App shell assets  → network-first, fall back to cache (always gets fresh files)
//   Everything else   → cache-first, fall back to network
self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);

  // Never intercept API or SignalR calls
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/chathub')) {
    return;
  }

  // Shell assets (HTML, CSS, JS): try network first so updates arrive immediately,
  // fall back to cache for offline use.
  const isShell = SHELL.includes(url.pathname);
  if (isShell) {
    e.respondWith(
      fetch(e.request)
        .then(response => {
          if (response.ok) {
            const clone = response.clone();
            caches.open(CACHE).then(c => c.put(e.request, clone));
          }
          return response;
        })
        .catch(() => caches.match(e.request))
    );
    return;
  }

  // Other assets (images, fonts, etc.): cache-first
  e.respondWith(
    caches.match(e.request).then(cached => {
      if (cached) return cached;
      return fetch(e.request).then(response => {
        if (response.ok && e.request.method === 'GET') {
          const clone = response.clone();
          caches.open(CACHE).then(c => c.put(e.request, clone));
        }
        return response;
      });
    })
  );
});

// ── Push notification handler ─────────────────────────────────────────────────
self.addEventListener('push', e => {
  let data = { title: 'ChatApp', body: 'New message' };
  try { if (e.data) data = e.data.json(); } catch { /* use defaults */ }

  e.waitUntil(
    self.registration.showNotification(data.title || 'ChatApp', {
      body:    data.body || '',
      icon:    '/icon.svg',
      badge:   '/icon.svg',
      tag:     data.tag || 'chatapp',
      renotify: true,
      data:    { roomId: data.roomId }
    })
  );
});

// Click on notification — focus the app or open it
self.addEventListener('notificationclick', e => {
  e.notification.close();
  e.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clients => {
      if (clients.length > 0) {
        const c = clients[0];
        c.focus();
        c.postMessage({ type: 'notification-click', roomId: e.notification.data?.roomId });
      } else {
        self.clients.openWindow('/');
      }
    })
  );
});
