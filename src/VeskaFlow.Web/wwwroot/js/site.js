// Asks before an action that cannot be undone (cancel, no-show, delete, booking): a button or form with data-confirm shows
// that question in a small dialog first, and nothing is sent unless the person says yes. The dialog is the browser's own
// <dialog> element styled like the cards (no extra library); very old browsers fall back to the plain confirm box.
function vfAskConfirm(message, danger, onYes) {
  var dlg = document.getElementById('vf-confirm');
  if (!dlg) {
    dlg = document.createElement('dialog');
    dlg.id = 'vf-confirm';
    dlg.className = 'confirm-dialog';
    dlg.setAttribute('aria-labelledby', 'vf-confirm-text');
    var form = document.createElement('form');
    form.method = 'dialog';
    var text = document.createElement('p');
    text.id = 'vf-confirm-text';
    var actions = document.createElement('div');
    actions.className = 'confirm-actions';
    var no = document.createElement('button');
    no.type = 'button'; no.value = 'no'; no.className = 'secondary'; no.id = 'vf-confirm-no';
    no.textContent = document.body.getAttribute('data-confirm-no') || 'Cancel';
    var yes = document.createElement('button');
    yes.type = 'button'; yes.value = 'yes'; yes.id = 'vf-confirm-yes';
    yes.textContent = document.body.getAttribute('data-confirm-yes') || 'Yes, continue';
    actions.appendChild(no); actions.appendChild(yes);
    form.appendChild(text); form.appendChild(actions);
    dlg.appendChild(form);
    document.body.appendChild(dlg);
  }
  if (typeof dlg.showModal !== 'function') { if (window.confirm(message)) onYes(); return; }
  dlg.querySelector('#vf-confirm-text').textContent = message;
  dlg.querySelector('#vf-confirm-yes').className = danger ? 'danger' : 'primary';
  // Both answers act at once (not through the dialog's async close event), then the dialog closes.
  dlg.querySelector('#vf-confirm-yes').onclick = function () { dlg.close('yes'); onYes(); };
  dlg.querySelector('#vf-confirm-no').onclick = function () { dlg.close('no'); };
  dlg.showModal();
  dlg.querySelector('#vf-confirm-no').focus(); // the safe answer is the one under the finger
}

document.addEventListener('submit', function (event) {
  var form = event.target;
  var button = event.submitter;
  var message = (button && button.getAttribute('data-confirm')) || form.getAttribute('data-confirm');
  if (!message) return;
  if (form.vfConfirmed) { form.vfConfirmed = false; return; } // the person already said yes: let it go through
  event.preventDefault();
  var danger = !!(button && button.classList.contains('danger'));
  vfAskConfirm(message, danger, function () {
    form.vfConfirmed = true;
    if (form.requestSubmit) form.requestSubmit(button || undefined); else form.submit();
  });
});

// A Save button (marked data-save) shows a check for a few seconds once the save went through. A save reloads the page with a
// green notice, so the pressed button is remembered across the reload and checked when the notice is there.
document.addEventListener('submit', function (event) {
  var button = event.submitter;
  if (event.defaultPrevented || !button || !button.hasAttribute('data-save')) return; // not a save, or still waiting for "yes"
  try { sessionStorage.setItem('vf-saved', [button.name || '', button.value || '', button.textContent.trim()].join('|')); } catch (e) { /* private mode: no check, the notice still shows */ }
});
(function () {
  var key = null;
  try { key = sessionStorage.getItem('vf-saved'); sessionStorage.removeItem('vf-saved'); } catch (e) { return; }
  if (!key || !document.querySelector('.notice[role="status"]')) return; // nothing was saved (an error page has no notice)
  var parts = key.split('|');
  var done = Array.prototype.filter.call(document.querySelectorAll('button[data-save]'), function (b) {
    return (b.name || '') === parts[0] && (b.value || '') === parts[1] && b.textContent.trim() === parts[2];
  })[0];
  if (!done) return;
  done.classList.add('saved');
  setTimeout(function () { done.classList.remove('saved'); }, 4000);
})();

// Prevents double submission: the first submit disables the button. The server also treats a repeated
// submit as the same booking (idempotency key), so this is a courtesy, not the guarantee.
document.addEventListener('submit', function (event) {
  if (event.defaultPrevented) return; // the person answered "no" to a confirmation
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

// "What service do you need?" appears only while "Other" is chosen (online booking and walk-in). Without JavaScript the box
// simply stays visible, and the server still asks for it when Other is chosen.
document.querySelectorAll('[data-other-field]').forEach(function (field) {
  var form = field.closest('form');
  if (!form) return;
  var input = field.querySelector('input');
  var sync = function () {
    var chosen = form.querySelector('select[name="serviceId"] option:checked, input[name="serviceId"]:checked');
    var isOther = !!(chosen && chosen.getAttribute('data-other') === 'true');
    field.hidden = !isOther;
    if (!isOther && input) input.value = ''; // forget what was typed for a service that is no longer Other
  };
  form.addEventListener('change', sync);
  sync();
});

// "Check your email" page: ask the server every few seconds whether the link was opened, then go to sign-in by itself.
(function () {
  var marker = document.querySelector('[data-poll-confirmed]');
  if (!marker) return;
  var tries = 0;
  var timer = setInterval(function () {
    if (++tries > 150) { clearInterval(timer); return; } // stop after about 10 minutes
    fetch(marker.getAttribute('data-poll-confirmed'), { cache: 'no-store', credentials: 'same-origin' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) { if (j && j.confirmed) { clearInterval(timer); location.href = marker.getAttribute('data-poll-to'); } })
      .catch(function () { /* a network blip: try again on the next tick */ });
  }, 4000);
})();

// "Print" button on the Settings page (the print stylesheet shows only the QR code).
document.addEventListener('click', function (event) {
  if (event.target.closest('[data-print]')) window.print();
});

// Back office only: register the tiny service worker so phones can install it as an app.
if ('serviceWorker' in navigator && location.pathname.toLowerCase().startsWith('/admin')) {
  navigator.serviceWorker.register('/sw.js', { scope: '/' }).catch(function () {});
}

// GCash QR on a phone: tapping the QR (or Save QR) saves the picture and, on Android, opens the GCash app a moment later so the
// client can use Pay QR, Upload QR. An "Open GCash" button also shows on Android. Other devices only get the download.
(function () {
  var android = /Android/i.test(navigator.userAgent);
  if (!android) return;
  var gcash = 'intent:#Intent;action=android.intent.action.MAIN;category=android.intent.category.LAUNCHER;package=com.globe.gcash.android;' +
    'S.browser_fallback_url=' + encodeURIComponent('https://play.google.com/store/apps/details?id=com.globe.gcash.android') + ';end';
  document.querySelectorAll('[data-gcash-open]').forEach(function (el) {
    el.hidden = false;
    el.setAttribute('href', gcash);
  });
  document.querySelectorAll('[data-gcash-save]').forEach(function (el) {
    el.addEventListener('click', function () {
      setTimeout(function () { window.location.href = gcash; }, 1500);
    });
  });
})();

// Day view: after a card of the day at a glance is clicked, bring its list into view and put focus on it.
(function () {
  var panel = document.getElementById('kpi-details');
  if (!panel) return;
  panel.scrollIntoView({ block: 'start' });
  panel.focus({ preventScroll: true });
})();

// A search box with a list of suggestions (a <datalist>): choosing one runs the search at once.
document.querySelectorAll('form[data-search-submit]').forEach(function (form) {
  var input = form.querySelector('input[list]');
  if (!input) return;
  input.addEventListener('change', function () { if (typeof form.requestSubmit === 'function') form.requestSubmit(); else form.submit(); });
});
