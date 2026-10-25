// Prevents double submission: the first submit disables the button. The server also treats a repeated
// submit as the same booking (idempotency key), so this is a courtesy, not the guarantee.
document.addEventListener('submit', function (event) {
  var button = event.target.querySelector('button[type="submit"].once');
  if (!button) return;
  // Disable after the browser has captured the form data for this submit.
  setTimeout(function () {
    button.disabled = true;
    button.setAttribute('aria-busy', 'true');
  }, 0);
});

// Restore the button when the page is shown again from the back/forward cache.
window.addEventListener('pageshow', function (event) {
  if (!event.persisted) return;
  document.querySelectorAll('button.once[disabled]').forEach(function (button) {
    button.disabled = false;
    button.removeAttribute('aria-busy');
  });
});

// Close the notification bell when the visitor taps elsewhere or presses Escape.
document.addEventListener('click', function (event) {
  document.querySelectorAll('details.bell[open]').forEach(function (d) {
    if (!d.contains(event.target)) d.removeAttribute('open');
  });
});
document.addEventListener('keydown', function (event) {
  if (event.key !== 'Escape') return;
  document.querySelectorAll('details.bell[open]').forEach(function (d) { d.removeAttribute('open'); });
});

// "Copy message" button on the Follow-ups page: copies the ready-made message next to it.
document.addEventListener('click', function (event) {
  var button = event.target.closest('[data-copy]');
  if (!button) return;
  var source = document.getElementById(button.getAttribute('data-copy'));
  if (!source) return;
  var done = function () {
    var label = button.getAttribute('data-copied');
    if (label) { var old = button.textContent; button.textContent = label; setTimeout(function () { button.textContent = old; }, 2000); }
  };
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(source.value).then(done, function () { source.select(); });
  } else {
    source.select();
    try { document.execCommand('copy'); done(); } catch (e) { /* the text is selected; the person can copy it by hand */ }
  }
});

// "Share to another app": only phones and browsers that support sharing show the button; the share sheet lists
// Messenger, Viber, WhatsApp, email and so on.
document.querySelectorAll('[data-share]').forEach(function (button) {
  if (navigator.share) button.hidden = false;
});
document.addEventListener('click', function (event) {
  var button = event.target.closest('[data-share]');
  if (!button) return;
  var source = document.getElementById(button.getAttribute('data-share'));
  if (source && navigator.share) navigator.share({ text: source.value }).catch(function () { /* cancelled */ });
});

// "Print" button on the Settings page (the print stylesheet shows only the QR code).
document.addEventListener('click', function (event) {
  if (event.target.closest('[data-print]')) window.print();
});
