// Account look-up helper: <input data-account-lookup="#targetId"> shows title / balance / status for an account number.
document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('[data-account-lookup]').forEach(function (inp) {
        var out = document.querySelector(inp.getAttribute('data-account-lookup'));
        var timer;
        inp.addEventListener('input', function () {
            clearTimeout(timer);
            var v = inp.value.trim();
            if (v.length < 6) { if (out) out.textContent = ''; return; }
            timer = setTimeout(function () {
                fetch('/Accounts/Lookup?no=' + encodeURIComponent(v), { credentials: 'same-origin' })
                    .then(function (r) { return r.json(); })
                    .then(function (d) {
                        if (!out) return;
                        out.className = 'form-text ' + (d.found ? 'text-success' : 'text-danger');
                        out.textContent = d.found ? (d.title + ' | ' + d.product + ' | ' + d.status + ' | Balance ' + d.balance + ' | Branch ' + d.branch) : 'Account not found';
                    }).catch(function () { });
            }, 300);
        });
    });
    // Show / hide FDR / DPS fields by product category on the account opening form
    var prod = document.getElementById('ProductId');
    if (prod && prod.dataset.cats) {
        var cats = JSON.parse(prod.dataset.cats);
        var apply = function () {
            var c = cats[prod.value] || '';
            document.querySelectorAll('[data-cat]').forEach(function (el) {
                el.style.display = el.dataset.cat.split(',').indexOf(c) >= 0 ? '' : 'none';
            });
        };
        prod.addEventListener('change', apply); apply();
    }
});
