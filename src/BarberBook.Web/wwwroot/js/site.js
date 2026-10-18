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
