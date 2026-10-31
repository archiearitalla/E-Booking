// The AI receptionist chat on the booking page. The server decides everything (free times, bookings, limits); this only
// sends what the customer typed and shows what comes back. Replies are shown as text, never as HTML, and only addresses on
// this site become links (the manage link of a new booking).
(function () {
  var box = document.getElementById('chat');
  if (!box) return;
  var form = box.querySelector('form');
  var log = document.getElementById('chat-log');
  var input = document.getElementById('chat-input');
  var button = form.querySelector('button[type="submit"]');
  box.hidden = false;

  function render(p, text) {
    text.split(/(https?:\/\/\S+)/).forEach(function (part) {
      var link = null;
      if (/^https?:\/\//.test(part)) {
        try {
          var url = new URL(part.replace(/[.,;:!?)]+$/, ''));
          if (url.origin === window.location.origin) link = url;
        } catch (e) { /* not a web address: shown as text */ }
      }
      if (link) {
        var a = document.createElement('a');
        a.href = link.href;
        a.textContent = link.href;
        a.rel = 'noopener';
        p.appendChild(a);
        p.appendChild(document.createTextNode(part.slice(link.href.length)));
      } else {
        p.appendChild(document.createTextNode(part));
      }
    });
  }

  function add(kind, text) {
    var p = document.createElement('p');
    p.className = 'chat-msg ' + kind;
    render(p, text);
    log.appendChild(p);
    log.scrollTop = log.scrollHeight;
    return p;
  }

  form.addEventListener('submit', function (event) {
    event.preventDefault();
    var text = input.value.trim();
    if (!text || button.disabled) return;
    // The first message of a conversation carries the "are you a person" answer (when the check is on): wait until it is ready.
    var check = form.elements['cf-turnstile-response'];
    if (check && !check.value && !form.elements['chatId'].value) {
      add('bot', box.getAttribute('data-captcha-wait') || '...');
      return;
    }
    var body = new URLSearchParams(new FormData(form));
    add('me', text);
    input.value = '';
    button.disabled = true;
    var waiting = add('bot wait', box.getAttribute('data-working') || '...');

    fetch(form.action, { method: 'POST', body: body, headers: { Accept: 'application/json' }, credentials: 'same-origin' })
      .then(function (response) {
        return response.json().then(function (data) { return { ok: response.ok, data: data }; }, function () { return { ok: false, data: {} }; });
      })
      .then(function (result) {
        waiting.remove();
        if (result.ok && result.data.reply) {
          form.elements['chatId'].value = result.data.chatId;
          add('bot', result.data.reply);
        } else {
          add('bot', result.data.error || box.getAttribute('data-unavailable'));
        }
        // The answer is single-use. Once a conversation has started the check is done; until then ask for a fresh one.
        var captcha = document.getElementById('chat-captcha');
        if (captcha) {
          if (form.elements['chatId'].value) captcha.hidden = true;
          else if (window.turnstile) window.turnstile.reset();
        }
      })
      .catch(function () {
        waiting.remove();
        add('bot', box.getAttribute('data-network'));
      })
      .then(function () {
        button.disabled = false;
        input.focus();
      });
  });
})();
