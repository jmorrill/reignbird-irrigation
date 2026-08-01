/*
 * Push notifications, imported into the generated service worker.
 *
 * Kept as a separate file so the Workbox precaching stays generated rather than
 * hand-written: vite-plugin-pwa pulls this in with importScripts, and everything
 * else about the worker is left alone.
 */

self.addEventListener('push', (event) => {
  // A push with no readable body still means something happened. Showing a generic
  // notification beats showing nothing — on most browsers a push that displays no
  // notification at all counts against the site and can cost the permission.
  let payload = {
    title: 'Reignbird',
    body: 'Something needs your attention.',
    severity: 'Info',
  };

  try {
    if (event.data) payload = { ...payload, ...event.data.json() };
  } catch {
    /* Not JSON. The default above stands. */
  }

  const options = {
    body: payload.body,
    icon: '/pwa-192x192.png',
    badge: '/pwa-64x64.png',
    // Notifications of the same kind replace each other rather than stacking, so a
    // controller that has been offline all weekend is one line, not a hundred.
    tag: payload.kind || 'reignbird',
    renotify: payload.severity === 'Problem',
    requireInteraction: payload.severity === 'Problem',
    data: { url: '/', kind: payload.kind },
  };

  event.waitUntil(self.registration.showNotification(payload.title, options));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();

  // Focus the app if it is already open rather than opening a second copy of it.
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clients) => {
      for (const client of clients) {
        if ('focus' in client) return client.focus();
      }
      return self.clients.openWindow(event.notification.data?.url || '/');
    }),
  );
});
