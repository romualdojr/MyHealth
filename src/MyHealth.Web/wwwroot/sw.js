// MyHealth service worker: makes the dashboard installable and shows a friendly page when there is no connection.
// It never caches the app itself (it is live, server-rendered and works with the clients' data), only the offline page.
const CACHE = 'myhealth-offline-v2';
const OFFLINE = '/offline.html';
const STYLES = '/_content/MyHealth.Web.Common/css/app.css';

self.addEventListener('install', event => {
    event.waitUntil(caches.open(CACHE).then(cache => cache.addAll([OFFLINE, '/icons/icon-192.png', STYLES])));
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)))));
    self.clients.claim();
});

self.addEventListener('fetch', event => {
    // The offline page's stylesheet, when the network is down.
    if (new URL(event.request.url).pathname === STYLES) {
        event.respondWith(fetch(event.request).catch(() => caches.match(STYLES)));
        return;
    }

    if (event.request.mode !== 'navigate') {
        return;
    }

    event.respondWith(fetch(event.request).catch(() => caches.match(OFFLINE)));
});
