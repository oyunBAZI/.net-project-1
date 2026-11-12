(function () {
    "use strict";

    // Sidebar toggle (mobile)
    document.addEventListener("click", function (event) {
        var toggle = event.target.closest("[data-toggle-sidebar]");
        if (toggle) {
            document.body.classList.toggle("sidebar-open");
            return;
        }
        if (document.body.classList.contains("sidebar-open") &&
            !event.target.closest("#sidebar") && !event.target.closest("[data-toggle-sidebar]")) {
            document.body.classList.remove("sidebar-open");
        }
    });

    // Company switcher posts immediately
    document.querySelectorAll("[data-company-switch]").forEach(function (select) {
        select.addEventListener("change", function () {
            select.form.submit();
        });
    });

    // Auto-dismiss toasts
    document.querySelectorAll(".toast").forEach(function (toast) {
        setTimeout(function () { toast.style.display = "none"; }, 7000);
    });

    // Confirm destructive actions
    document.addEventListener("submit", function (event) {
        var form = event.target;
        var message = form.getAttribute("data-confirm");
        if (message && !window.confirm(message)) {
            event.preventDefault();
            return;
        }
    });

    // ---------- line editors ----------
    function money(value) {
        var number = parseFloat(String(value).replace(/[^0-9.\-]/g, ""));
        return isNaN(number) ? 0 : number;
    }

    function format(value) {
        return (Math.round(value * 100) / 100).toLocaleString(undefined, {
            minimumFractionDigits: 2, maximumFractionDigits: 2
        });
    }

    function reindex(editor) {
        editor.querySelectorAll(".line-row").forEach(function (row, index) {
            row.querySelectorAll("[name]").forEach(function (input) {
                input.name = input.name.replace(/Lines\[\d+\]/g, "Lines[" + index + "]");
                input.id = input.id ? input.id.replace(/Lines_\d+__/g, "Lines_" + index + "__") : input.id;
            });
        });
    }

    function recalc(editor) {
        var subtotal = 0, taxTotal = 0;
        editor.querySelectorAll(".line-row").forEach(function (row) {
            var qtyInput = row.querySelector("[data-qty]");
            var priceInput = row.querySelector("[data-price]");
            var discountInput = row.querySelector("[data-discount]");
            var taxInput = row.querySelector("[data-tax]");
            if (!qtyInput || !priceInput) return;

            var qty = money(qtyInput.value);
            var price = money(priceInput.value);
            var discount = discountInput ? money(discountInput.value) : 0;
            var rate = taxInput ? money(taxInput.value) : 0;
            var net = qty * price * (1 - discount / 100);
            var tax = net * rate / 100;

            var lineTotal = row.querySelector("[data-line-total]");
            if (lineTotal) lineTotal.textContent = format(net + tax);

            subtotal += net;
            taxTotal += tax;
        });

        var subElement = document.querySelector("[data-subtotal]");
        var taxElement = document.querySelector("[data-total-tax]");
        var grandElement = document.querySelector("[data-grand-total]");
        if (subElement) subElement.textContent = format(subtotal);
        if (taxElement) taxElement.textContent = format(taxTotal);
        if (grandElement) grandElement.textContent = format(subtotal + taxTotal);
    }

    function journalTotals(editor) {
        var debit = 0, credit = 0;
        editor.querySelectorAll(".line-row").forEach(function (row) {
            var debitInput = row.querySelector("[data-debit]");
            var creditInput = row.querySelector("[data-credit]");
            debit += debitInput ? money(debitInput.value) : 0;
            credit += creditInput ? money(creditInput.value) : 0;
        });
        var debitEl = document.querySelector("[data-total-debit]");
        var creditEl = document.querySelector("[data-total-credit]");
        var diffEl = document.querySelector("[data-difference]");
        if (debitEl) debitEl.textContent = format(debit);
        if (creditEl) creditEl.textContent = format(credit);
        if (diffEl) {
            diffEl.textContent = format(debit - credit);
            diffEl.classList.toggle("negative", Math.abs(debit - credit) > 0.004);
        }
    }

    function update(editor) {
        reindex(editor);
        recalc(editor);
        journalTotals(editor);
    }

    document.querySelectorAll("[data-lines]").forEach(function (editor) {
        editor.addEventListener("input", function () { update(editor); });

        editor.addEventListener("click", function (event) {
            if (event.target.closest("[data-remove-line]")) {
                var row = event.target.closest(".line-row");
                var rows = editor.querySelectorAll(".line-row");
                if (rows.length > 1) {
                    row.remove();
                    update(editor);
                }
            }
        });

        var addButton = document.querySelector("[data-add-line]");
        if (addButton) {
            addButton.addEventListener("click", function () {
                var template = document.getElementById("line-template");
                if (!template) return;
                var body = editor.querySelector("tbody");
                var index = editor.querySelectorAll(".line-row").length;
                var html = template.innerHTML.replace(/__index__/g, index);
                var wrapper = document.createElement("tbody");
                wrapper.innerHTML = html.trim();
                var row = wrapper.querySelector(".line-row");
                if (row) body.appendChild(row);
                update(editor);
            });
        }

        update(editor);
    });

    // Auto-fill tax rate when a tax code is chosen
    document.addEventListener("change", function (event) {
        var select = event.target.closest("[data-tax-code]");
        if (!select) return;
        var rate = select.options[select.selectedIndex]?.getAttribute("data-rate");
        var row = select.closest(".line-row");
        if (!row || rate === null) return;
        var input = row.querySelector("[data-tax]");
        if (input && !input.disabled) input.value = rate;
        var editor = select.closest("[data-lines]");
        if (editor) update(editor);
    });

    // Bank import: show the chosen file name
    document.querySelectorAll("[data-file-input]").forEach(function (input) {
        input.addEventListener("change", function () {
            var label = document.querySelector(input.getAttribute("data-file-input"));
            if (label) label.textContent = input.files.length ? input.files[0].name : "Choose CSV file";
        });
    });

    // Print buttons
    document.querySelectorAll("[data-print]").forEach(function (button) {
        button.addEventListener("click", function () { window.print(); });
    });
})();
