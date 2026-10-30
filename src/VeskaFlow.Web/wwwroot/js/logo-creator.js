// The Logo Creator on the Settings page. The logo is drawn here in the browser (letters, a shape, colours, an icon), shown as a
// preview as the owner chooses, and on "Use this logo" turned into a PNG and sent through the ordinary logo upload form, so the
// server checks it exactly like a picture the owner chose from their phone. Nothing else is sent anywhere.
(function () {
  var box = document.getElementById('logo-creator');
  var form = document.getElementById('logo-form');
  var fileInput = document.getElementById('logo-file');
  if (!box || !form || !fileInput) return;
  box.hidden = false;

  var text = document.getElementById('logo-creator-text');
  var preview = document.getElementById('logo-creator-preview');
  var use = document.getElementById('logo-creator-use');
  var message = document.getElementById('logo-creator-message');
  var defaultText = box.getAttribute('data-default-text') || '';
  var useLabel = use.textContent;
  text.value = defaultText;

  function chosen(name) { return box.querySelector('input[name="' + name + '"]:checked'); }
  function escape(value) {
    return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  function drawing() {
    var shape = chosen('lc-shape').value;
    var color = chosen('lc-color');
    var icon = chosen('lc-icon');
    var serif = chosen('lc-font').value === 'serif';
    var background = color.getAttribute('data-bg');
    var foreground = color.getAttribute('data-fg');
    var iconSvg = icon.value === 'none' ? null : icon.parentElement.querySelector('svg');
    var word = (text.value.trim() || defaultText).slice(0, 12);
    var size = word.length <= 2 ? 150 : word.length === 3 ? 118 : word.length <= 5 ? 88 : word.length <= 8 ? 64 : 48;
    var baseline = iconSvg ? 400 : 256 + size * 0.35;
    var family = serif ? "Georgia, 'Times New Roman', serif" : "'Helvetica Neue', Arial, sans-serif";

    var back = shape === 'circle'
      ? '<circle cx="256" cy="256" r="256" fill="' + background + '"/>'
      : '<rect width="512" height="512" rx="' + (shape === 'rounded' ? 96 : 0) + '" fill="' + background + '"/>';
    var mark = iconSvg
      ? '<g transform="translate(176 78) scale(6.667)" fill="none" stroke="' + foreground + '" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">' + iconSvg.innerHTML + '</g>'
      : '';
    return '<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512">' + back + mark +
      '<text x="256" y="' + baseline + '" text-anchor="middle" font-family="' + escape(family) + '" font-weight="800" font-size="' + size +
      '" fill="' + foreground + '">' + escape(word) + '</text></svg>';
  }

  function address() { return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(drawing()); }
  function update() { preview.src = address(); }

  function fail() {
    use.disabled = false;
    use.textContent = useLabel;
    message.textContent = box.getAttribute('data-failed');
    message.hidden = false;
  }

  use.addEventListener('click', function () {
    message.hidden = true;
    use.disabled = true;
    use.textContent = box.getAttribute('data-busy');
    var image = new Image();
    image.onerror = fail;
    image.onload = function () {
      try {
        var canvas = document.createElement('canvas');
        canvas.width = 512;
        canvas.height = 512;
        canvas.getContext('2d').drawImage(image, 0, 0, 512, 512);
        canvas.toBlob(function (blob) {
          if (!blob) { fail(); return; }
          try {
            var transfer = new DataTransfer();
            transfer.items.add(new File([blob], 'logo.png', { type: 'image/png' }));
            fileInput.files = transfer.files;
            if (form.requestSubmit) form.requestSubmit(); else form.submit();
          } catch (e) { fail(); }
        }, 'image/png');
      } catch (e) { fail(); }
    };
    image.src = address();
  });

  box.addEventListener('input', update);
  box.addEventListener('change', update);
  update();
})();
