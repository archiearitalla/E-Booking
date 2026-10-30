// The Promo Creator's live preview: the card on the page changes as the owner types, picks a mode or chooses a picture.
// It only changes how the preview looks; nothing is saved until the form is sent, and the server checks everything again.
(function () {
  var form = document.getElementById('promo-form');
  var holder = document.getElementById('promo-preview');
  if (!form || !holder) return;
  var card = holder.querySelector('.promo');
  var icons = document.getElementById('promo-icons');
  function part(selector) { return card.querySelector(selector); }
  function field(name) { return form.elements[name]; }

  function show(element, text) {
    element.textContent = text;
    element.hidden = !text;
  }

  // "2026-12-24" -> "December 24", without letting the time zone shift the day.
  function dayText(value) {
    var bits = value.split('-');
    if (bits.length !== 3) return '';
    var date = new Date(Number(bits[0]), Number(bits[1]) - 1, Number(bits[2]));
    return isNaN(date.getTime()) ? '' : date.toLocaleDateString('en-US', { month: 'long', day: 'numeric' });
  }

  function update() {
    var chosen = form.querySelector('input[name="Input.Theme"]:checked');
    if (chosen) {
      card.className = 'promo promo-t-' + chosen.value;
      part('.promo-kicker').textContent = chosen.getAttribute('data-kicker');
      var icon = icons && icons.querySelector('[data-promo-icon="' + chosen.value + '"]');
      if (icon) part('.promo-deco').innerHTML = icon.innerHTML;
    }
    var headline = field('Input.Caption').value.trim();
    part('.promo-title').textContent = headline || holder.getAttribute('data-placeholder');
    show(part('.promo-details'), field('Input.Details').value.trim());
    show(part('.promo-badge'), field('Input.Badge').value.trim());
    var day = field('Input.EndsOn').value ? dayText(field('Input.EndsOn').value) : '';
    show(part('.promo-until'), day ? holder.getAttribute('data-until').replace('{date}', day) : '');
    var removing = field('Input.RemovePicture') && field('Input.RemovePicture').checked;
    var image = part('.promo-img');
    if (image) image.hidden = removing || !image.getAttribute('src');
  }

  var picture = field('picture');
  if (picture) {
    picture.addEventListener('change', function () {
      var file = picture.files && picture.files[0];
      var image = part('.promo-img');
      if (!file || !image || !/^image\//.test(file.type)) return;
      var reader = new FileReader();
      reader.onload = function () { image.src = reader.result; update(); }; // a data: address, which the page is allowed to show
      reader.readAsDataURL(file);
    });
  }
  form.addEventListener('input', update);
  form.addEventListener('change', update);
  update();
})();
