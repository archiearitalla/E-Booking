// Lets the phone offer "Install app". It keeps nothing offline on purpose: every page always comes fresh from the server,
// so bookings and records are never shown out of date.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', () => {});
