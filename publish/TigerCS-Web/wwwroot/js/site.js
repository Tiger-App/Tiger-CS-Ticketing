(function () {
  "use strict";

  // ---------- Progressive enhancement only: every feature below has a
  // working non-JS fallback (native <details>, a real <form> submit). ----------

  // Close open <details> disclosures when clicking outside them.
  document.addEventListener("click", function (event) {
    document.querySelectorAll("details.disclosure[open]").forEach(function (details) {
      if (!details.contains(event.target)) {
        details.removeAttribute("open");
      }
    });
  });

  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      document.querySelectorAll("details.disclosure[open]").forEach(function (d) {
        d.removeAttribute("open");
      });
    }
  });

  // Password show/hide. Without JS the field just stays type="password".
  document.querySelectorAll(".password-toggle").forEach(function (toggle) {
    toggle.addEventListener("click", function () {
      var input = document.getElementById(toggle.getAttribute("data-target"));
      if (!input) return;
      var showing = input.type === "text";
      input.type = showing ? "password" : "text";
      toggle.setAttribute("aria-pressed", showing ? "false" : "true");
      toggle.setAttribute("aria-label", showing ? "Show password" : "Hide password");
      toggle.innerHTML = showing ? toggle.dataset.iconShow : toggle.dataset.iconHide;
    });
  });

  // Filter forms: auto-submit on select/checkbox change, so a mouse/keyboard
  // user doesn't need to also press the Apply button. The button still works
  // (and is the only way to apply filters) without this script. Applies to
  // any select or checkbox marked data-autosubmit (the ticket queue's
  // filters, the customer workspace's unit filter, the admin list toolbars).
  document.querySelectorAll("select[data-autosubmit], input[type=checkbox][data-autosubmit]").forEach(function (el) {
    el.addEventListener("change", function () {
      // Never start a second navigation over one already in flight — the
      // browser would abort the first, and the server would be left holding
      // a cancelled query. The in-flight submit already carries this
      // control's new value if it changed before the request left.
      if (el.form && !el.form.hasAttribute("data-submitting")) {
        el.form.requestSubmit();
      }
    });
  });

  // Clickable table rows (Customers directory, customer ticket lists): the
  // first cell carries a real link; with JS the whole row follows it, except
  // when the click was on another link, button or form control in the row.
  document.querySelectorAll("tr.is-link[data-href]").forEach(function (row) {
    row.addEventListener("click", function (event) {
      if (event.target.closest("a, button, input, select, label, form, details")) return;
      window.location.assign(row.getAttribute("data-href"));
    });
  });

  // Confirmation before a deactivation/archive/delete/publish action:
  // forms marked data-confirm ask first. Without JS the form submits
  // directly — the Api still enforces every rule server-side.
  document.addEventListener("submit", function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    var message = form.getAttribute("data-confirm");
    if (message && !window.confirm(message)) {
      event.preventDefault();
      event.stopPropagation();
    }
  }, true);

  // New Ticket Step 1: the phone field's "required" state follows the
  // selected channel's own configuration (each option carries the
  // channel's RequiresPhone flag from Administration → Channels). Without
  // JS the server-rendered state stands, and the PageModel and Api enforce
  // the same rule regardless of what the browser did.
  document.querySelectorAll("form[data-channel-form]").forEach(function (form) {
    var select = form.querySelector("[data-channel-select]");
    var phone = form.querySelector("[data-phone-input]");
    var hint = form.querySelector("[data-phone-optional-hint]");
    if (!select || !phone) return;
    var apply = function () {
      var option = select.options[select.selectedIndex];
      var requiresPhone = !option || option.getAttribute("data-requires-phone") !== "false";
      phone.required = requiresPhone;
      phone.setAttribute("aria-required", requiresPhone ? "true" : "false");
      if (hint) hint.hidden = requiresPhone;
    };
    select.addEventListener("change", apply);
    apply();
  });

  // Customer Profile · Payment tab: its content is fetched the first time
  // the tab is opened, so the profile itself never waits on the finance
  // source, and fetched again when the account changes. Only the response
  // to the LATEST request is applied: a slow answer for a previously
  // selected account is discarded. Without JS every link and the account
  // form load the page with the tab rendered server-side (?tab=payment).
  // A payment account selector rendered outside the profile's fetched tab
  // (the New Ticket wizard's Payments & Fines panel) reloads the page with
  // its carried state on change; the <noscript> button covers the rest.
  document.querySelectorAll("[data-payment-selector][data-autosubmit='true'] select").forEach(function (select) {
    select.addEventListener("change", function () { select.form.submit(); });
  });

  document.querySelectorAll("[data-payment-src]").forEach(function (panel) {
    var radio = document.getElementById("tab-payment");
    if (!radio) return;
    var latest = 0;

    var load = function (account) {
      var request = ++latest;
      var url = panel.getAttribute("data-payment-src") + (account ? "&account=" + encodeURIComponent(account) : "");
      var current = panel.querySelector(".payment-tab");
      if (current) current.setAttribute("aria-busy", "true");
      fetch(url, { credentials: "same-origin", headers: { "Accept": "text/html" } })
        .then(function (response) {
          if (!response.ok) throw new Error(String(response.status));
          return response.text();
        })
        .then(function (html) {
          if (request !== latest) return; // a newer selection superseded this one
          var tab = panel.querySelector(".payment-tab");
          if (tab) tab.outerHTML = html;
        })
        .catch(function () {
          if (request !== latest) return;
          var tab = panel.querySelector(".payment-tab");
          if (tab) {
            tab.removeAttribute("aria-busy");
            tab.insertAdjacentHTML("afterbegin", '<div class="alert alert-error" role="alert"><span>Payment details could not be loaded. Use Retry or reload the page.</span></div>');
          }
        });
    };

    panel.addEventListener("change", function (event) {
      if (event.target && event.target.id === "paymentAccount") load(event.target.value);
    });
    panel.addEventListener("click", function (event) {
      var link = event.target.closest && event.target.closest("[data-payment-account-link], [data-payment-retry]");
      if (!link || !panel.contains(link)) return;
      event.preventDefault();
      var tab = panel.querySelector(".payment-tab");
      load(link.getAttribute("data-payment-account-link") || (tab && tab.getAttribute("data-payment-account")) || "");
    });

    var deferred = function () { return panel.querySelector('[data-payment-state="Deferred"]'); };
    radio.addEventListener("change", function () { if (radio.checked && deferred()) load(""); });
    if (radio.checked && deferred()) load("");
  });

  // Customer Profile · Documents tab: fetched the first time the tab is opened,
  // and again whenever the unit changes. On a unit change the previous unit's
  // documents are removed from the page BEFORE the request is made (only the
  // selector stays), the previous request is aborted, and only the answer to the
  // LATEST request is applied - and only if it is for the unit that was asked
  // for - so a slow answer for another unit can never show under this one.
  // Without JS the selector form and every link load the page with the tab
  // rendered server-side (?tab=documents&unit=...).
  document.querySelectorAll("[data-documents-src]").forEach(function (panel) {
    var radio = document.getElementById("tab-documents");
    if (!radio) return;
    var latest = 0;
    var controller = null;

    var loadingNode = function () {
      var wrap = document.createElement("div");
      wrap.className = "payment-loading";
      wrap.setAttribute("role", "status");
      wrap.setAttribute("aria-live", "polite");
      wrap.setAttribute("data-documents-loading", "");
      wrap.innerHTML = '<span class="payment-spinner" aria-hidden="true"></span><span>Loading documents\u2026</span>';
      return wrap;
    };

    var clearToLoading = function () {
      var tab = panel.querySelector(".documents-tab");
      if (!tab) return null;
      Array.prototype.slice.call(tab.children).forEach(function (child) {
        if (!child.hasAttribute("data-documents-selector")) tab.removeChild(child);
      });
      tab.setAttribute("aria-busy", "true");
      tab.setAttribute("data-documents-state", "Deferred");
      tab.appendChild(loadingNode());
      return tab;
    };

    var load = function (unit) {
      var request = ++latest;
      if (controller) controller.abort();
      controller = typeof AbortController === "function" ? new AbortController() : null;
      clearToLoading();
      var url = panel.getAttribute("data-documents-src") + (unit ? "&unit=" + encodeURIComponent(unit) : "");
      fetch(url, { credentials: "same-origin", headers: { "Accept": "text/html" }, signal: controller ? controller.signal : undefined })
        .then(function (response) {
          if (!response.ok) throw new Error(String(response.status));
          return response.text();
        })
        .then(function (html) {
          if (request !== latest) return; // a newer selection superseded this one
          var holder = document.createElement("div");
          holder.innerHTML = html;
          var fresh = holder.querySelector(".documents-tab");
          var answered = fresh && fresh.getAttribute("data-documents-unit");
          if (!fresh || (unit && answered && answered !== String(unit))) throw new Error("wrong unit");
          var tab = panel.querySelector(".documents-tab");
          if (tab) tab.outerHTML = fresh.outerHTML;
        })
        .catch(function (error) {
          if (request !== latest || (error && error.name === "AbortError")) return;
          var tab = panel.querySelector(".documents-tab");
          if (!tab) return;
          tab.removeAttribute("aria-busy");
          var loading = tab.querySelector("[data-documents-loading]");
          if (loading) tab.removeChild(loading);
          tab.setAttribute("data-documents-state", "Error");
          tab.insertAdjacentHTML("beforeend", '<div class="alert alert-error payment-alert-action" role="alert" data-documents-error><span><strong>Documents could not be retrieved.</strong> No documents are shown.</span><a class="btn btn-sm" href="#" data-documents-retry>Retry</a></div>');
        });
    };

    var currentUnit = function () {
      var select = panel.querySelector("#documentsUnit");
      if (select && select.value) return select.value;
      var tab = panel.querySelector(".documents-tab");
      return (tab && tab.getAttribute("data-documents-unit")) || "";
    };

    panel.addEventListener("change", function (event) {
      if (event.target && event.target.id === "documentsUnit") {
        // The "Select a unit" placeholder exists only while nothing is shown, so an empty value has nothing to clear.
        if (event.target.value) load(event.target.value);
      }
    });
    panel.addEventListener("click", function (event) {
      var link = event.target.closest && event.target.closest("[data-documents-retry], [data-documents-fallback]");
      if (!link || !panel.contains(link)) return;
      event.preventDefault();
      load(currentUnit());
    });

    var deferred = function () { return panel.querySelector('[data-documents-state="Deferred"]'); };
    radio.addEventListener("change", function () { if (radio.checked && deferred()) load(currentUnit()); });
    if (radio.checked && deferred()) load(currentUnit());
  });

  // Prevent duplicate submission: the FIRST submit of a form wins, and every
  // further submit while that one is still in flight is dropped. This covers
  // the double-click, the second Enter in a search box, and a filter control
  // changing while the page is already navigating — on the Customers
  // directory's search/filter bar (#customerFilters) as much as on the
  // ticket forms.
  //
  // It matters beyond the duplicate itself: a second navigation makes the
  // browser abort the first one, and an aborted request cancels
  // HttpContext.RequestAborted all the way down to the Customers query in
  // TigerCS.Api, which then throws TaskCanceledException out of
  // CustomerDirectoryRepository. The server treats that as the expected
  // client cancellation it is (ClientDisconnectMiddleware), but not starting
  // the pointless second request is better than cancelling the first.
  //
  // Without JS every click still submits normally, and the server is
  // unchanged either way — nothing here is a correctness guarantee, the
  // Api enforces its own rules regardless.
  document.addEventListener("submit", function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) return;

    if (form.hasAttribute("data-submitting")) {
      event.preventDefault();
      event.stopImmediatePropagation();
      return;
    }

    form.setAttribute("data-submitting", "");
    // Deferred by one turn so the submit button is still enabled while the
    // browser builds the form data: a disabled control is barred from
    // submission, and some forms (TicketDetails' Approve/Reject) submit
    // their decision AS the button's own name/value. The attribute above,
    // not the disabled state, is what actually blocks the second submit.
    window.setTimeout(function () {
      form.querySelectorAll('button[type="submit"]').forEach(function (btn) {
        btn.disabled = true;
      });
    }, 0);
  });

  // Ticket Details "What needs your attention?": the recommended step opens the matching header
  // action (or scrolls to the banner that holds it). Without JS the link still jumps to the page top
  // where the actions are; stopPropagation keeps the outside-click handler above from closing it.
  document.querySelectorAll("[data-guidance-target]").forEach(function (link) {
    link.addEventListener("click", function (event) {
      var target = document.querySelector('[data-action="' + link.getAttribute("data-guidance-target") + '"]');
      if (!target) return;
      event.preventDefault();
      event.stopPropagation();
      if (target.tagName === "DETAILS") {
        target.setAttribute("open", "");
      }
      target.scrollIntoView({ behavior: "smooth", block: "center" });
      var focusable = target.querySelector("summary, select, textarea, input:not([type=hidden]), button");
      if (focusable) focusable.focus({ preventScroll: true });
    });
  });

  // Ticket Details "Classify": the request type list follows the chosen category. Each request type option carries the
  // category it belongs under; only those are shown. A category with none classifies on its own (no request type).
  document.querySelectorAll("[data-classify-form]").forEach(function (form) {
    var category = form.querySelector("[data-classify-category]");
    var requestType = form.querySelector("[data-classify-request-type]");
    if (!category || !requestType) return;
    var hint = form.querySelector("[data-classify-request-type-hint]");
    var options = Array.prototype.slice.call(requestType.options);
    function apply() {
      var categoryId = category.value;
      var visible = 0;
      options.forEach(function (option) {
        var belongs = option.value === "0" ? false : option.getAttribute("data-category-ids") === categoryId;
        option.hidden = option.value === "0" ? true : !belongs;
        option.disabled = option.value === "0" ? true : !belongs;
        if (belongs) visible++;
      });
      var none = requestType.querySelector('option[value="0"]');
      if (visible === 0) {
        none.hidden = false;
        none.disabled = false;
        requestType.value = "0";
        requestType.disabled = true;
        if (hint) hint.textContent = "This category has no request types; it is classified on its own.";
      } else {
        requestType.disabled = false;
        var current = requestType.options[requestType.selectedIndex];
        if (!current || current.disabled) {
          var first = options.filter(function (o) { return !o.disabled; })[0];
          if (first) requestType.value = first.value;
        }
        if (hint) hint.textContent = "Choose the request type that belongs under the selected category.";
      }
    }
    category.addEventListener("change", apply);
    apply();
    // A disabled select is not posted; the server reads a missing request type as "none".
  });

  // Ticket Details "Conversation History": a chat reads from the newest message, so an open conversation starts scrolled to
  // its end (a long one stays in its own scroll box); a group opened later scrolls once when it first shows.
  function scrollChatToEnd(group) {
    var box = group.querySelector(".chat-scroll");
    if (box) box.scrollTop = box.scrollHeight;
  }
  document.querySelectorAll(".convo-group").forEach(function (group) {
    if (group.open) scrollChatToEnd(group);
    group.addEventListener("toggle", function () {
      if (group.open) scrollChatToEnd(group);
    });
  });

  // Back/forward can restore this page from the browser's cache exactly as
  // it was left — mid-submit, with the guard set and the buttons disabled.
  // Release it, or the agent returns to a filter bar that refuses to apply.
  window.addEventListener("pageshow", function () {
    document.querySelectorAll("form[data-submitting]").forEach(function (form) {
      form.removeAttribute("data-submitting");
      form.querySelectorAll('button[type="submit"]').forEach(function (btn) {
        btn.disabled = false;
      });
    });
  });
})();
