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

  // Toolbar popovers (Tickets Date): one open at a time, closed by
  // Escape (focus returns to the toggle) or a click outside.
  var popovers = Array.prototype.slice.call(document.querySelectorAll("details[data-popover]"));
  popovers.forEach(function (pop) {
    pop.addEventListener("toggle", function () {
      if (!pop.open) return;
      popovers.forEach(function (other) { if (other !== pop) other.open = false; });
    });
  });
  document.addEventListener("click", function (event) {
    popovers.forEach(function (pop) { if (pop.open && !pop.contains(event.target)) pop.open = false; });
  });
  document.addEventListener("keydown", function (event) {
    if (event.key !== "Escape") return;
    popovers.forEach(function (pop) {
      if (!pop.open) return;
      pop.open = false;
      var summary = pop.querySelector("summary");
      if (summary) summary.focus();
    });
  });

  // Drawers (Tickets Filters): any [data-drawer] element. Opened by
  // [data-drawer-open="<id>"], closed by [data-drawer-close], the backdrop
  // ([data-drawer-backdrop="<id>"]) or Escape. Focus moves into the drawer and
  // is trapped there, then returns to the opener; the page behind does not
  // scroll. [data-drawer-reset] clears every field in the drawer and submits
  // the drawer's form. Values are never cleared on close, so reopening shows
  // what is applied.
  var DRAWER_FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type=hidden]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
  document.querySelectorAll("[data-drawer]").forEach(function (drawer) {
    var backdrop = document.querySelector('[data-drawer-backdrop="' + drawer.id + '"]');
    var openers = Array.prototype.slice.call(document.querySelectorAll('[data-drawer-open="' + drawer.id + '"]'));
    var lastOpener = null, closeTimer = null;

    function open(opener) {
      if (drawer.classList.contains("is-open")) return;
      clearTimeout(closeTimer);
      lastOpener = opener || document.activeElement;
      var gutter = window.innerWidth - document.documentElement.clientWidth;
      drawer.hidden = false; if (backdrop) backdrop.hidden = false;
      drawer.setAttribute("aria-hidden", "false");
      void drawer.offsetWidth; // commit the closed state so the slide animates
      drawer.classList.add("is-open"); if (backdrop) backdrop.classList.add("is-open");
      document.documentElement.classList.add("drawer-open");
      if (gutter > 0) document.documentElement.style.paddingInlineEnd = gutter + "px";
      openers.forEach(function (o) { o.setAttribute("aria-expanded", "true"); });
      var first = drawer.querySelector(".drawer__body " + DRAWER_FOCUSABLE) || drawer.querySelector("[data-drawer-close]");
      if (first) first.focus();
    }
    function close(restoreFocus) {
      if (!drawer.classList.contains("is-open")) return;
      drawer.classList.remove("is-open"); if (backdrop) backdrop.classList.remove("is-open");
      drawer.setAttribute("aria-hidden", "true");
      openers.forEach(function (o) { o.setAttribute("aria-expanded", "false"); });
      document.documentElement.classList.remove("drawer-open");
      document.documentElement.style.paddingInlineEnd = "";
      closeTimer = setTimeout(function () { drawer.hidden = true; if (backdrop) backdrop.hidden = true; }, 300);
      if (restoreFocus !== false && lastOpener && lastOpener.focus) lastOpener.focus();
    }

    openers.forEach(function (o) { o.addEventListener("click", function () { open(o); }); });
    drawer.querySelectorAll("[data-drawer-close]").forEach(function (b) { b.addEventListener("click", function () { close(); }); });
    if (backdrop) backdrop.addEventListener("click", function () { close(); });
    document.addEventListener("keydown", function (event) {
      if (!drawer.classList.contains("is-open")) return;
      if (event.key === "Escape") { event.preventDefault(); close(); return; }
      if (event.key !== "Tab") return;
      var items = Array.prototype.filter.call(drawer.querySelectorAll(DRAWER_FOCUSABLE), function (el) { return el.offsetParent !== null; });
      if (!items.length) return;
      var firstEl = items[0], lastEl = items[items.length - 1];
      if (!drawer.contains(document.activeElement)) { event.preventDefault(); firstEl.focus(); }
      else if (event.shiftKey && document.activeElement === firstEl) { event.preventDefault(); lastEl.focus(); }
      else if (!event.shiftKey && document.activeElement === lastEl) { event.preventDefault(); firstEl.focus(); }
    });
    drawer.querySelectorAll("[data-drawer-reset]").forEach(function (btn) {
      btn.addEventListener("click", function () {
        drawer.querySelectorAll("select").forEach(function (el) { el.value = ""; });
        drawer.querySelectorAll("input:not([type=hidden])").forEach(function (el) {
          if (el.type === "checkbox" || el.type === "radio") el.checked = false; else el.value = "";
        });
        var form = btn.form || drawer.closest("form");
        if (form) form.requestSubmit();
      });
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

  // Unlock a locked-out user in place (Admin > Users): post with fetch, then
  // drop the "Sign-in locked" note and the button and show the outcome. The
  // data-confirm prompt below still runs first. Without JS the form posts and
  // redirects back with a status message.
  document.querySelectorAll("form[data-unlock-form]").forEach(function (form) {
    form.addEventListener("submit", function (event) {
      if (event.defaultPrevented) return;
      event.preventDefault();
      var flash = document.getElementById("users-flash");
      var show = function (ok, message) {
        if (!flash) return;
        flash.className = "alert " + (ok ? "alert-success" : "alert-error");
        flash.textContent = message;
        flash.hidden = false;
      };
      var button = form.querySelector("button");
      if (button) button.disabled = true;
      fetch(form.action, {
        method: "POST", body: new FormData(form), credentials: "same-origin",
        headers: { "X-Requested-With": "fetch", "Accept": "application/json" }
      }).then(function (r) { return r.json(); }).then(function (data) {
        show(data.success, data.message);
        if (data.success) {
          var row = form.closest("tr");
          var note = row && row.querySelector("[data-lock-note]");
          if (note) note.remove();
          form.remove();
        } else if (button) {
          button.disabled = false;
        }
      }).catch(function () {
        show(false, "The user could not be unlocked.");
        if (button) button.disabled = false;
      });
    });
  });

  // Live password-rule feedback (Admin > Reset password). The rules listed come
  // from Identity's configured policy (rendered server-side); this only shows
  // which are met as the admin types. It never blocks the submit — the Api
  // still validates and its Identity errors are shown after the post.
  document.querySelectorAll("form[data-password-form]").forEach(function (form) {
    var input = form.querySelector("[data-password-new]");
    var confirm = form.querySelector("[data-password-confirm]");
    var rules = form.querySelectorAll("[data-password-rules] [data-rule]");
    var match = form.querySelector("[data-password-match]");
    if (!input) return;
    var tests = {
      length: function (v, el) { return Array.from(v).length >= +el.dataset.min; },
      upper: function (v) { return /\p{Lu}/u.test(v); },
      lower: function (v) { return /\p{Ll}/u.test(v); },
      digit: function (v) { return /\p{Nd}/u.test(v); },
      symbol: function (v) { return /[^\p{L}\p{N}]/u.test(v); },
      unique: function (v, el) { return new Set(Array.from(v)).size >= +el.dataset.min; }
    };
    function update() {
      var v = input.value;
      rules.forEach(function (el) {
        var test = tests[el.dataset.rule];
        el.classList.remove("is-met", "is-unmet");
        if (!test || v === "") return;
        el.classList.add(test(v, el) ? "is-met" : "is-unmet");
      });
      if (match && confirm) {
        match.classList.remove("is-ok", "is-bad");
        if (confirm.value === "") {
          match.textContent = "Both entries must match.";
        } else if (confirm.value === v) {
          match.textContent = "Passwords match.";
          match.classList.add("is-ok");
        } else {
          match.textContent = "Passwords do not match.";
          match.classList.add("is-bad");
        }
      }
    }
    input.addEventListener("input", update);
    if (confirm) confirm.addEventListener("input", update);
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

    // A form's own handler (a validation message, or a fetch-based save) runs first and may already have cancelled this
    // submit. Nothing is being sent then, so the form must stay usable: marking it "submitting" here would disable its
    // button for good and make every later attempt - after the user fixed what the message asked for - silently do nothing.
    if (event.defaultPrevented) return;

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
      // The action may sit inside the header's Actions menu: open every enclosing menu so the form is actually visible.
      var enclosing = target.parentElement ? target.parentElement.closest("details") : null;
      while (enclosing) {
        enclosing.setAttribute("open", "");
        enclosing = enclosing.parentElement ? enclosing.parentElement.closest("details") : null;
      }
      target.scrollIntoView({ behavior: "smooth", block: "nearest" });
      var focusable = target.querySelector("summary, select, textarea, input:not([type=hidden]), button");
      if (focusable) focusable.focus({ preventScroll: true });
    });
  });

  // Ticket Details "Actions" menu: each action's form opens as a modal dialog (CSS positions it; the <details> keeps it working
  // without script). Here: one modal at a time, focus moves into it and is kept there, the close button works, and focus returns
  // to the action's row when it closes.
  var MODAL_FOCUSABLE = 'a[href], button:not([disabled]), select:not([disabled]), textarea:not([disabled]), input:not([disabled]):not([type=hidden]), summary';
  document.addEventListener("toggle", function (event) {
    var details = event.target;
    if (!(details instanceof HTMLDetailsElement) || !details.matches(".actions-menu__panel > details[data-action]")) return;
    var summary = details.querySelector(":scope > summary");
    if (details.open) {
      details.parentElement.querySelectorAll(":scope > details[data-action][open]").forEach(function (other) {
        if (other !== details) other.removeAttribute("open");
      });
      var first = details.querySelector(".action-modal__body select, .action-modal__body textarea, .action-modal__body input:not([type=hidden]), .action-modal__body button, .action-modal__body a[href]");
      if (first) first.focus({ preventScroll: true });
    } else if (!document.activeElement || document.activeElement === document.body || details.contains(document.activeElement)) {
      // Back to the control that opened it: the action's row, or the Actions button when the whole menu closed (Escape, outside click).
      var menu = details.closest("details.actions-menu");
      var back = menu && !menu.open ? menu.querySelector(":scope > summary") : summary;
      if (back) back.focus({ preventScroll: true });
    }
  }, true);

  document.addEventListener("click", function (event) {
    var close = event.target instanceof Element ? event.target.closest("[data-modal-close]") : null;
    if (!close) return;
    var details = close.closest("details");
    if (details) details.removeAttribute("open");
  });

  document.addEventListener("keydown", function (event) {
    if (event.key !== "Tab") return;
    var modal = document.querySelector(".actions-menu__panel > details[open] > .action-modal");
    if (!modal) return;
    var items = Array.prototype.filter.call(modal.querySelectorAll(MODAL_FOCUSABLE), function (el) { return el.offsetParent !== null; });
    if (items.length === 0) return;
    var firstItem = items[0], lastItem = items[items.length - 1];
    if (!modal.contains(document.activeElement)) { event.preventDefault(); firstItem.focus(); }
    else if (event.shiftKey && document.activeElement === firstItem) { event.preventDefault(); lastItem.focus(); }
    else if (!event.shiftKey && document.activeElement === lastItem) { event.preventDefault(); firstItem.focus(); }
  });

  // Keyboard access for tab-jump labels (they target a tab's radio input): focusable, announced as buttons, Enter/Space activate.
  document.querySelectorAll("label.tab-jump").forEach(function (label) {
    if (!label.hasAttribute("tabindex")) label.setAttribute("tabindex", "0");
    label.setAttribute("role", "button");
    label.addEventListener("keydown", function (event) {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        label.click();
      }
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

  // New Ticket Step 2: when the Project changes, refill the Unit list with the units known for that project (the Web handler proxies
  // the read-only Api call). The Unit stays free text, so a unit that is not in the system can still be typed in.
  (function () {
    var project = document.querySelector("[data-project-input]");
    var unitList = document.querySelector("[data-unit-options]");
    if (!project || !unitList) return;
    var timer = null;
    function load() {
      var name = project.value.trim();
      if (!name) { unitList.innerHTML = ""; return; }
      fetch(project.getAttribute("data-units-url") + "&project=" + encodeURIComponent(name), { credentials: "same-origin", headers: { "Accept": "application/json" } })
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(function (units) {
          if (project.value.trim() !== name) return;
          unitList.innerHTML = "";
          units.forEach(function (u) { var o = document.createElement("option"); o.value = u; unitList.appendChild(o); });
        })
        .catch(function () { /* the Unit field stays free text */ });
    }
    project.addEventListener("input", function () { clearTimeout(timer); timer = setTimeout(load, 250); });
    project.addEventListener("change", load);
  })();

  // Customer Details: inline edit of the customer's name/e-mail. The pencil swaps the heading for a small form; Save posts to the
  // EditContact handler with fetch and, once the server confirms, updates every place the name is shown on the page without a
  // reload. Without JavaScript the pencil is a link to ?editContact=1 and the form posts normally.
  (function () {
    var form = document.querySelector("[data-contact-edit-form]");
    var openLink = document.querySelector("[data-contact-edit-open]");
    if (!form || !openLink) return;
    var title = openLink.closest(".customer-header__title");
    var nameInput = form.querySelector("[data-contact-input-name]");
    var emailInput = form.querySelector("[data-contact-input-email]");
    var errorBox = form.querySelector("[data-contact-error]");
    var saveButton = form.querySelector("[data-contact-save]");
    var cancelLink = form.querySelector("[data-contact-edit-cancel]");
    var initial = { name: nameInput.value, email: emailInput.value };

    function show(editing) {
      form.hidden = !editing;
      if (title) title.hidden = editing;
      if (editing) { nameInput.focus(); nameInput.select(); }
    }
    function say(text) { errorBox.textContent = text || ""; }
    function initials(name) {
      var parts = name.trim().split(/\s+/).filter(Boolean);
      return ((parts[0] || "")[0] || "").concat(parts.length > 1 ? parts[parts.length - 1][0] : "").toUpperCase();
    }

    if (!form.hidden) show(true);
    openLink.addEventListener("click", function (e) { e.preventDefault(); say(""); show(true); });
    cancelLink.addEventListener("click", function (e) {
      e.preventDefault();
      nameInput.value = initial.name; emailInput.value = initial.email; say(""); show(false);
    });
    form.addEventListener("keydown", function (e) { if (e.key === "Escape") cancelLink.click(); });

    form.addEventListener("submit", function (e) {
      e.preventDefault();
      if (!nameInput.value.trim()) { say("Customer name is required."); nameInput.focus(); return; }
      saveButton.disabled = true; say("");
      fetch(form.action, { method: "POST", body: new FormData(form), credentials: "same-origin", headers: { "X-Requested-With": "fetch", "Accept": "application/json" } })
        .then(function (response) { return response.json().catch(function () { return { ok: false, message: "The change could not be saved." }; }); })
        .then(function (result) {
          if (!result.ok) { say(result.message || "The change could not be saved."); return; }
          document.querySelectorAll("[data-contact-name]").forEach(function (el) { el.textContent = result.name; });
          document.querySelectorAll("[data-contact-email]").forEach(function (el) { el.textContent = result.email || "\u2014"; });
          var avatar = document.querySelector(".customer-header__avatar");
          if (avatar) avatar.textContent = initials(result.name);
          document.title = result.name + " \u00b7 Customer";
          initial = { name: result.name, email: result.email || "" };
          nameInput.value = initial.name; emailInput.value = initial.email;
          show(false);
        })
        .catch(function () { say("Tiger Ticketing System could not reach the ticketing service."); })
        .then(function () { saveButton.disabled = false; });
    });
  })();

  // Ticket Details Assign / Transfer: state the destination in words before the agent confirms. The text follows the select
  // beside it (the option's label up to " — ", i.e. just the person's or department's name); nothing is posted from here.
  document.querySelectorAll("[data-destination-for]").forEach(function (note) {
    var select = document.getElementById(note.getAttribute("data-destination-for"));
    if (!select) { note.hidden = true; return; }
    var template = note.getAttribute("data-destination-template") || "{0}";
    var fallback = note.getAttribute("data-destination-fallback") || "";
    function update() {
      var option = select.options[select.selectedIndex];
      var label = option && option.value ? option.text.split(" \u2014 ")[0].trim() : "";
      note.textContent = label ? template.replace("{0}", label) : fallback;
      note.hidden = note.textContent === "";
    }
    select.addEventListener("change", update);
    update();
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
