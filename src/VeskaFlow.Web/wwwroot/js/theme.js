// Bright / dark theme switch. The site is dark by default; a person who picks the bright theme keeps it on this device (localStorage).
// This file is loaded in the head without "defer" so the saved choice is applied before the page is drawn (no flash of the other theme).
// The click is handled by one listener on the document, so it works for every switch button on any page. Storage can be blocked
// (private window, site data off): then the choice only lasts for this page and nothing breaks.
(function () {
  var KEY = 'vf-theme';
  var root = document.documentElement;

  function saved() {
    try { return localStorage.getItem(KEY) === 'light' ? 'light' : 'dark'; } catch (e) { return 'dark'; }
  }
  function apply(mode) {
    if (mode === 'light') root.setAttribute('data-mode', 'light'); else root.removeAttribute('data-mode');
    var buttons = document.querySelectorAll('[data-theme-toggle]');
    for (var i = 0; i < buttons.length; i++) {
      var b = buttons[i];
      var toLight = mode !== 'light';
      b.setAttribute('aria-pressed', mode === 'light' ? 'true' : 'false');
      b.setAttribute('aria-label', toLight ? (b.getAttribute('data-to-light') || 'Switch to bright theme') : (b.getAttribute('data-to-dark') || 'Switch to dark theme'));
      b.setAttribute('title', b.getAttribute('aria-label'));
    }
  }

  apply(saved());

  document.addEventListener('DOMContentLoaded', function () { apply(saved()); });
  document.addEventListener('click', function (e) {
    var target = e.target;
    while (target && target !== document && !(target.hasAttribute && target.hasAttribute('data-theme-toggle'))) target = target.parentNode;
    if (!target || target === document) return;
    var next = root.getAttribute('data-mode') === 'light' ? 'dark' : 'light';
    try { localStorage.setItem(KEY, next); } catch (err) { /* the choice lasts for this page only */ }
    apply(next);
  });
})();
