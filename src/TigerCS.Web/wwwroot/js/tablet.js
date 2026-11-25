/* Customer review tablet. Same-origin script (the Web app's CSP forbids inline script).
 *
 * A review is never lost: it is written to the tablet's own storage BEFORE the first send, kept there until the server confirms it, and
 * re-sent (with the same submissionId, so a repeat can never be stored twice) on a timer, when the connection returns and on page load.
 * The tablet's identity is an HttpOnly cookie this script cannot read; the page posts to itself and the server attaches the credential. */
(function () {
    'use strict';

    var QUEUE_KEY = 'tigercs.employeeReview.pending';
    var RELOAD_GUARD_KEY = 'tigercs.employeeReview.reloadedAt';
    var IDLE_RESET_MS = 90000;
    var RETRY_MS = 15000;

    // ---- storage that may be unavailable (private mode, quota): fall back to memory so the session still works ----
    var memoryQueue = [];
    function loadQueue() {
        try {
            var raw = window.localStorage.getItem(QUEUE_KEY);
            return raw ? JSON.parse(raw) : [];
        } catch (e) {
            return memoryQueue;
        }
    }
    function saveQueue(list) {
        memoryQueue = list;
        try { window.localStorage.setItem(QUEUE_KEY, JSON.stringify(list)); } catch (e) { /* kept in memory */ }
    }

    // ---- notice pages (inactive / unavailable): just look again after a while, so a re-activated tablet recovers by itself ----
    var reloadMs = parseInt(document.body.getAttribute('data-auto-reload'), 10);
    if (reloadMs > 0) {
        window.setTimeout(function () { window.location.reload(); }, reloadMs);
    }

    // ---- activation page: move the code from the link's #fragment into the form (a fragment is never sent to any server) ----
    var activate = document.querySelector('[data-activate-form]');
    if (activate) {
        var match = /(?:^#|&)code=([A-Za-z0-9_-]+)/.exec(window.location.hash || '');
        if (match) {
            activate.querySelector('input[name="Code"]').value = match[1];
            if (window.history && window.history.replaceState) {
                window.history.replaceState(null, '', window.location.pathname + window.location.search);
            }
        }
    }

    var root = document.querySelector('[data-tablet-root]');
    if (!root) {
        return;
    }

    var thanksMs = parseInt(root.getAttribute('data-thanks-ms'), 10) || 5000;
    var formView = root.querySelector('[data-view="form"]');
    var thanksView = root.querySelector('[data-view="thanks"]');
    var stars = Array.prototype.slice.call(root.querySelectorAll('[data-star]'));
    var ratingLabel = root.querySelector('[data-rating-label]');
    var comment = root.querySelector('[data-comment]');
    var errorBox = root.querySelector('[data-error]');
    var submitButton = root.querySelector('[data-submit]');
    var pendingNote = root.querySelector('[data-pending-note]');
    var langToggle = root.querySelector('[data-lang-toggle]');
    var antiforgery = (root.querySelector('input[name="__RequestVerificationToken"]') || {}).value || '';

    var rating = 0;
    var lang = root.getAttribute('data-lang') === 'ar' ? 'ar' : 'en';
    var thanksTimer = null;
    var idleTimer = null;
    var sending = false;

    // ---- language ----
    function text(selector) {
        var el = root.querySelector(selector);
        return el ? (el.getAttribute('data-' + lang) || el.textContent) : '';
    }

    function applyLanguage() {
        document.documentElement.lang = lang;
        document.documentElement.dir = lang === 'ar' ? 'rtl' : 'ltr';
        root.setAttribute('data-lang', lang);
        Array.prototype.forEach.call(root.querySelectorAll('[data-i18n]'), function (el) {
            var value = el.getAttribute('data-' + lang);
            if (value !== null && el !== ratingLabel) { el.textContent = value; }
        });
        if (comment) { comment.placeholder = comment.getAttribute('data-placeholder-' + lang) || comment.placeholder; }
        showRatingLabel();
    }

    function setLanguage(next) {
        lang = next;
        try { window.localStorage.setItem('tigercs.tablet.lang', lang); } catch (e) { /* optional */ }
        document.cookie = 'tigercs_tablet_lang=' + lang + '; path=/; max-age=31536000; SameSite=Strict';
        applyLanguage();
    }

    langToggle.addEventListener('click', function () { setLanguage(lang === 'ar' ? 'en' : 'ar'); touch(); });

    // ---- rating ----
    function showRatingLabel() {
        var hint = ratingLabel.getAttribute('data-' + lang);
        ratingLabel.textContent = rating ? text('[data-star-label="' + rating + '"]') : hint;
    }

    function setRating(value) {
        rating = value;
        stars.forEach(function (star) {
            var n = parseInt(star.getAttribute('data-star'), 10);
            star.classList.toggle('is-on', n <= rating);
            star.setAttribute('aria-checked', n === rating ? 'true' : 'false');
        });
        showRatingLabel();
        hideError();
    }

    stars.forEach(function (star) {
        star.addEventListener('click', function () { setRating(parseInt(star.getAttribute('data-star'), 10)); touch(); });
    });

    function showError(message) { errorBox.textContent = message; errorBox.hidden = false; }
    function hideError() { errorBox.hidden = true; errorBox.textContent = ''; }

    // ---- idle reset: a half-filled form is never left on show for the next customer ----
    function touch() {
        window.clearTimeout(idleTimer);
        idleTimer = window.setTimeout(resetForm, IDLE_RESET_MS);
    }
    comment.addEventListener('input', touch);

    function resetForm() {
        window.clearTimeout(thanksTimer);
        setRating(0);
        comment.value = '';
        hideError();
        pendingNote.hidden = true;
        thanksView.hidden = true;
        formView.hidden = false;
        submitButton.disabled = false;
        submitButton.textContent = submitButton.getAttribute('data-' + lang);
        sending = false;
    }

    // ---- sending ----
    function newId() {
        if (window.crypto && window.crypto.randomUUID) { return window.crypto.randomUUID(); }
        var bytes = new Uint8Array(16);
        if (window.crypto && window.crypto.getRandomValues) { window.crypto.getRandomValues(bytes); }
        else { for (var i = 0; i < 16; i++) { bytes[i] = Math.floor(Math.random() * 256); } }
        bytes[6] = (bytes[6] & 0x0f) | 0x40;
        bytes[8] = (bytes[8] & 0x3f) | 0x80;
        var hex = Array.prototype.map.call(bytes, function (b) { return ('0' + b.toString(16)).slice(-2); }).join('');
        return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16) + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
    }

    // Resolves to: saved | rejected | invalid-device | inactive | retry. Never throws.
    function send(item) {
        return window.fetch(window.location.pathname + '?handler=Submit', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': antiforgery },
            body: JSON.stringify({ submissionId: item.id, rating: item.rating, comment: item.comment })
        }).then(function (response) {
            if (response.ok) { return 'saved'; }
            if (response.status === 422) { return 'rejected'; }
            if (response.status === 401) { return 'invalid-device'; }
            if (response.status === 403) { return 'inactive'; }
            if (response.status === 400) { reloadForFreshToken(); }
            return 'retry';
        }).catch(function () { return 'retry'; });
    }

    // A 400 means the page's anti-forgery token went stale (e.g. the server restarted): load a fresh page, at most once a minute.
    function reloadForFreshToken() {
        try {
            var last = parseInt(window.sessionStorage.getItem(RELOAD_GUARD_KEY), 10) || 0;
            if (Date.now() - last < 60000) { return; }
            window.sessionStorage.setItem(RELOAD_GUARD_KEY, String(Date.now()));
        } catch (e) { return; }
        window.setTimeout(function () { window.location.reload(); }, 500);
    }

    function removeFromQueue(id) {
        saveQueue(loadQueue().filter(function (item) { return item.id !== id; }));
    }

    // One send loop at a time. A caller that arrives while one is running waits for it, then runs its own pass, so a review stored
    // meanwhile is attempted before the caller is told whether it was delivered.
    var inFlight = null;
    function flush() {
        if (inFlight) {
            return inFlight.then(flush);
        }
        inFlight = drain().then(function () { inFlight = null; }, function () { inFlight = null; });
        return inFlight;
    }

    function drain() {
        var queue = loadQueue();
        if (!queue.length) { return Promise.resolve(); }
        return send(queue[0]).then(function (result) {
            if (result === 'saved' || result === 'rejected') {
                // 'rejected' can never succeed on retry (the server refused the review itself): drop it rather than block the queue.
                removeFromQueue(queue[0].id);
                return drain();
            }
            if (result === 'invalid-device') {
                window.setTimeout(function () { window.location.reload(); }, 1000); // the server sends the tablet back to activation
            }
            return undefined; // inactive / retry: keep everything queued and try again on the timer
        });
    }

    function showThanks(delivered) {
        formView.hidden = true;
        thanksView.hidden = false;
        pendingNote.hidden = delivered;
        window.clearTimeout(idleTimer);
        window.clearTimeout(thanksTimer);
        thanksTimer = window.setTimeout(resetForm, thanksMs);
    }

    submitButton.addEventListener('click', function () {
        if (sending) { return; }
        if (!rating) { showError(text('[data-text-choose-rating]')); return; }
        hideError();
        sending = true;
        submitButton.disabled = true;
        submitButton.textContent = text('[data-text-sending]');

        var item = { id: newId(), rating: rating, comment: comment.value.trim().slice(0, 1000) || null };
        // Stored first: from this moment the review survives a failed send, a reload or a power cut.
        saveQueue(loadQueue().concat([item]));
        flush().then(function () {
            var delivered = !loadQueue().some(function (queued) { return queued.id === item.id; });
            showThanks(delivered);
        });
    });

    window.setInterval(flush, RETRY_MS);
    window.addEventListener('online', flush);

    applyLanguage();
    touch();
    flush(); // reviews left unsent by an earlier session go out as soon as the page is back
})();
