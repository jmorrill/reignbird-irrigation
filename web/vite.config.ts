import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { VitePWA } from 'vite-plugin-pwa';

/** Where the ASP.NET server is listening during development. */
const API_TARGET = process.env.RAINBIRD_API ?? 'http://127.0.0.1:5056';

export default defineConfig({
  plugins: [
    react(),

    VitePWA({
      // "prompt" rather than "autoUpdate": a silent reload is fine for a blog and
      // wrong for an app with valves attached to it. A reload that lands while
      // someone is halfway through setting a run time loses that input, so the new
      // version waits behind a banner and activates when they say so.
      registerType: 'prompt',

      includeAssets: ['favicon.svg', 'favicon.ico', 'apple-touch-icon-180x180.png'],

      manifest: {
        // Pinned explicitly so the identity survives ever being served from a
        // subpath — the browser derives it from start_url otherwise, and a changed
        // id orphans the installed copy instead of updating it.
        id: '/',
        name: 'Reignbird — irrigation control',
        short_name: 'Reignbird',
        description: 'Local control for Rain Bird irrigation controllers.',
        start_url: '/',
        scope: '/',
        display: 'standalone',
        orientation: 'any',
        // Both colours are the dark surface rather than the light one. The splash
        // shows for a few hundred milliseconds before the app picks up the system
        // theme, and a dark flash on a light device is far gentler than a white
        // flash on a dark one. The adaptive <meta name="theme-color"> tags in
        // index.html still drive the live title bar wherever they are supported.
        background_color: '#0a1017',
        theme_color: '#0a1017',
        categories: ['utilities', 'lifestyle'],
        icons: [
          { src: 'pwa-64x64.png', sizes: '64x64', type: 'image/png' },
          { src: 'pwa-192x192.png', sizes: '192x192', type: 'image/png' },
          { src: 'pwa-512x512.png', sizes: '512x512', type: 'image/png' },
          // Full-bleed, so a launcher can crop it to whatever shape it likes.
          { src: 'maskable-icon-512x512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
        shortcuts: [
          { name: 'Zones', short_name: 'Zones', url: '/?tab=zones', icons: [{ src: 'pwa-192x192.png', sizes: '192x192' }] },
          { name: 'Schedules', short_name: 'Schedules', url: '/?tab=schedules', icons: [{ src: 'pwa-192x192.png', sizes: '192x192' }] },
        ],
      },

      workbox: {
        globPatterns: ['**/*.{js,css,html,woff2,png,svg,ico}'],

        // The shell is precached, so a cold start offline still renders the app
        // rather than the browser's dinosaur.
        navigateFallback: '/index.html',
        // ...but only for navigations. These three are the server's, and answering
        // them with index.html would turn a network error into a parse error.
        navigateFallbackDenylist: [/^\/api\//, /^\/media\//, /^\/hubs\//],

        runtimeCaching: [
          {
            // Never cache irrigation state. A cached /api response could show a zone
            // as idle while it is watering, or report a run as finished while the
            // valve is still open — and the whole point of the screen is to be
            // trusted. Offline, these fail, and the app already has a path for that.
            urlPattern: ({ url }) => url.pathname.startsWith('/api/'),
            handler: 'NetworkOnly',
          },
          {
            // SignalR negotiates over HTTP before upgrading. Caching that hands the
            // client a stale connection token.
            urlPattern: ({ url }) => url.pathname.startsWith('/hubs/'),
            handler: 'NetworkOnly',
          },
          {
            // Zone photos are large, rarely change, and are the one thing worth
            // having offline. Revalidating in the background means replacing a photo
            // still shows up, one load later.
            urlPattern: ({ url }) => url.pathname.startsWith('/media/'),
            handler: 'StaleWhileRevalidate',
            options: {
              cacheName: 'rainbird-zone-photos',
              expiration: { maxEntries: 60, maxAgeSeconds: 60 * 60 * 24 * 30 },
            },
          },
        ],

        cleanupOutdatedCaches: true,
      },

      // The service worker is a production concern. Enabling it in dev means every
      // change is served from a cache that has to be manually cleared.
      devOptions: { enabled: false },
    }),
  ],

  server: {
    // 5173 is Vite's default and tends to be taken by other projects; this app
    // claims its own port and fails loudly rather than silently drifting to
    // another one, which would break the proxy assumptions below.
    port: 5273,
    strictPort: true,
    // Proxying rather than talking cross-origin means development behaves exactly
    // like production, where the SPA is served by the same host as the API. It also
    // removes any dependence on the server's CORS policy.
    proxy: {
      '/api': { target: API_TARGET, changeOrigin: true },
      '/media': { target: API_TARGET, changeOrigin: true },
      '/hubs': { target: API_TARGET, changeOrigin: true, ws: true },
    },
  },

  build: {
    // The built SPA is served by the ASP.NET project.
    outDir: '../src/RainBird.Server/wwwroot',
    emptyOutDir: true,
  },
});
