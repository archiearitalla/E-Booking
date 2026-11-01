// Plan and billing: shows the total to pay as the owner picks what to pay for and, for extra staff, types how many. The server works out
// the real amount again when "I paid" is sent; this only shows it. Without JavaScript the form works the same, just without the total.
(function () {
  var select = document.getElementById('option');
  var quantity = document.getElementById('quantity');
  var field = document.getElementById('quantity-field');
  var total = document.getElementById('pay-total');
  if (!select || !total) return;

  function peso(n) {
    return '₱' + n.toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 2 });
  }

  function update() {
    var option = select.options[select.selectedIndex];
    if (!option) return;
    var price = parseFloat(option.getAttribute('data-price'));
    var forStaff = option.getAttribute('data-kind') === 'STAFF';
    if (field) field.hidden = !forStaff;
    var count = 1;
    if (forStaff) {
      count = parseInt(quantity.value, 10);
      if (!(count >= 1)) { total.textContent = ''; return; }
    }
    var label = total.getAttribute('data-label') || '';
    var each = forStaff ? ' (' + count + ' × ' + peso(price) + ')' : '';
    total.textContent = label + ' ' + peso(price * count) + each;
  }

  select.addEventListener('change', update);
  if (quantity) quantity.addEventListener('input', update);
  update();
})();
