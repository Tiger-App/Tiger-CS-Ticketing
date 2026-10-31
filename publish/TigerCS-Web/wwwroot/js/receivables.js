/*
  Collections Receivables + Campaigns: asynchronous results, month/year selectors, payment-status / minimum-amount controls and the
  "Load data" action. The page shell renders at once; ONLY the results area ([data-results]) shows loading, errors and progress.
  Without JavaScript every link/form still works through the full-page render (?render=full).
*/
(() => {
    const form = document.querySelector('[data-results-form]');
    const box = document.querySelector('[data-results]');
    if (!form || !box) return;
    const submit = form.querySelector('button[type=submit]');
    const spinner = form.querySelector('.receivables-loading');
    const timeoutNote = form.querySelector('.receivables-timeout');
    const WATCHDOG_MS = 120000;   // results are a local read; anything longer is a fault, not progress
    const MAX_AUTO_RELOADS = 3;   // per user action: after a refresh ends the list is re-read at most this many times, never in a loop
    let controller = null, watchdog = 0, autoReloads = 0, expectStart = false;

    // ONE monitor per page. While a refresh runs it polls the tiny status handler (never the results fragment); when the server says nothing is loading it fires once and the list
    // is read ONE more time. load() / navigation / filter changes stop it first, so an obsolete loop can never outlive the request that started it.
    const Monitor = window.ReceivablesLoadMonitor;
    const statusUrl = () => `${box.dataset.resultsUrl}?handler=Status`;
    const monitor = Monitor ? Monitor.create({
        fetchStatus: async signal => {
            const response = await fetch(statusUrl(), { signal, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' }, cache: 'no-store' });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            return response.json();
        },
        isHidden: () => document.hidden === true,
        onStatus: status => updateRunningNotice(status),
        onDone: () => { if (autoReloads < MAX_AUTO_RELOADS) { autoReloads++; load(currentQuery(), false); } else showPollStopped('the list was refreshed several times'); },
        onGiveUp: (status, reason) => showPollStopped(reason === 'error' ? 'the status could not be read' : 'it is taking longer than expected', status)
    }) : null;
    const stopMonitor = () => { if (monitor) monitor.stop(); };

    // Live text in the "Loading data for ..." notice, from the status answer (no list request).
    function updateRunningNotice(status) {
        (status.companies || []).forEach(c => {
            const line = document.querySelector(`[data-company-state="${c.id}"]`);
            if (!line) return;
            if (c.loading && c.startedUtc) {
                const minutes = Math.max(0, Math.round((Date.now() - Date.parse(c.startedUtc)) / 60000));
                line.textContent = `${c.name}: refreshing for ${minutes} min` + (c.hasData && c.lastSuccess ? `; showing data from ${c.lastSuccess} meanwhile.` : '.');
            }
        });
    }
    // Polling ended without a final answer. The data on screen stays; the notice stops claiming progress and says what happened, with a way to look again.
    function showPollStopped(why, status) {
        const notice = document.querySelector('[data-load-running]');
        if (!notice) return;
        notice.removeAttribute('data-load-running'); notice.removeAttribute('data-poll-active');
        notice.setAttribute('data-poll-stopped', ''); notice.setAttribute('role', 'alert'); notice.classList.add('receivables-notice--warning');
        notice.textContent = '';
        const text = document.createElement('span');
        text.textContent = `The refresh is still running on the server, but this page stopped checking automatically because ${why}. The data shown is the last valid snapshot.`;
        const again = document.createElement('button'); again.type = 'button'; again.className = 'btn'; again.textContent = 'Check again'; again.setAttribute('data-poll-resume', '');
        notice.append(text, ' ', again);
    }

    const setBusy = busy => {
        form.toggleAttribute('aria-busy', busy);
        box.toggleAttribute('aria-busy', busy);
        if (spinner) spinner.hidden = !busy;
        if (submit) submit.disabled = busy;
    };
    const queryFromForm = () => {
        const q = new URLSearchParams();
        new FormData(form).forEach((value, key) => { if (String(value) !== '' || key === 'minTotal') q.append(key, String(value)); });   // an EMPTY Minimum Total means "cleared" and must be sent   // disabled inputs are not part of FormData
        return q;
    };
    const resultsUrl = q => `${box.dataset.resultsUrl}?${q.toString()}${q.toString() ? '&' : ''}handler=Results`;
    const currentQuery = () => { const q = new URLSearchParams(location.search); q.delete('render'); q.delete('handler'); return q; };
    const message = (text, retry) => {
        box.innerHTML = '';
        const panel = document.createElement('div'); panel.className = 'panel';
        const inner = document.createElement('div'); inner.className = 'error-state'; inner.setAttribute('role', 'alert'); inner.setAttribute('data-campaign-error', '');
        const p = document.createElement('p'); p.textContent = text; inner.appendChild(p);
        if (retry) { const b = document.createElement('button'); b.type = 'button'; b.className = 'btn'; b.textContent = 'Retry'; b.setAttribute('data-results-retry', ''); inner.appendChild(b); }
        panel.appendChild(inner); box.appendChild(panel);
    };
    const showLoading = () => {
        box.innerHTML = '';
        const d = document.createElement('div'); d.className = 'receivables-results__loading'; d.setAttribute('role', 'status'); d.setAttribute('data-results-loading', '');
        d.textContent = 'Loading…'; box.appendChild(d);
    };

    // Which payment views the loaded data can answer: unreliable ones are disabled and say why (never enabled against data that cannot back them).
    const status = form.querySelector('[data-payment-status]');
    const minInput = form.querySelector('[data-min-amount]');
    const minHelp = form.querySelector('[data-min-help]');
    const applyAvailability = views => {
        if (!status) return;
        [...status.options].forEach(o => {
            const needs = o.dataset.needs;
            if (!needs) return;
            const ok = views ? (needs === 'breakdown' ? views.breakdown : views.paid) : false;
            o.disabled = !ok && !o.selected;
            o.title = ok ? '' : needs === 'breakdown'
                ? 'The source does not return original and paid amounts, so Unpaid and Partially paid cannot be told apart.'
                : 'Fully paid instalments are not in the loaded data yet. Use Load data to include them.';
        });
    };
    const applyMinState = () => {
        if (!status || !minInput) return;
        const off = status.value === 'paid' || status.value === 'all';
        minInput.disabled = off;
        if (minHelp) minHelp.textContent = off ? 'Not applied to Fully paid and All, so paid instalments are never hidden.'
            : 'Shows instalments whose remaining unpaid amount is at least this value (≥).';
    };
    if (status) { applyAvailability(null); status.addEventListener('change', applyMinState); applyMinState(); }

    // By unit: the unit's unpaid instalments are expanded in place (View Details). Without JavaScript the detail rows simply stay visible (the toggle buttons are hidden until this runs).
    const setUnitOpen = (unit, open) => {
        const detail = unit.querySelector('[data-unit-detail]'), button = unit.querySelector('[data-unit-toggle]');
        detail.hidden = !open; button.setAttribute('aria-expanded', String(open)); button.textContent = open ? 'Hide Details' : 'View Details';
    };
    const collapseUnits = () => box.querySelectorAll('[data-unit]').forEach(u => { u.querySelector('[data-unit-toggle]').hidden = false; setUnitOpen(u, false); });

    // The "loading data" notice may live in the results or (Campaigns) in the separate snapshot status box.
    const running = () => document.querySelector('[data-load-running]');

    const afterRender = () => {
        collapseUnits();
        const marker = box.querySelector('[data-payment-views]');
        if (marker) applyAvailability({ breakdown: marker.dataset.breakdown === 'true', paid: marker.dataset.paid === 'true' });
        const eff = box.querySelector('[data-effective-filters]');
        if (eff) {   // show the dates the server actually used when the user left them empty
            const from = form.querySelector('[data-date-from]'), to = form.querySelector('[data-date-to]');
            if (from && !from.value && eff.dataset.from) from.value = eff.dataset.from;
            if (to && !to.value && eff.dataset.to) to.value = eff.dataset.to;
            syncMonthFromDates();
        }
        // A refresh is running (or was just requested and is about to start): watch it with the small status poll, never by re-reading the list.
        if (monitor && (running() || expectStart)) { const expect = expectStart && !running(); expectStart = false; monitor.start({ expectActive: expect }); }
    };

    async function load(query, push) {
        if (controller) controller.abort();
        stopMonitor(); window.clearTimeout(watchdog);
        controller = new AbortController();
        const mine = controller;
        if (timeoutNote) timeoutNote.hidden = true;
        const polling = !push && running() !== null;   // keep the current notice and list while re-reading after a refresh
        if (!polling) showLoading();
        setBusy(true);
        let timedOut = false;
        watchdog = window.setTimeout(() => { timedOut = true; mine.abort(); }, WATCHDOG_MS);
        try {
            if (push) history.pushState(null, '', `${location.pathname}${query.toString() ? '?' + query.toString() : ''}`);
            const response = await fetch(resultsUrl(query), { signal: mine.signal, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } });
            if (response.status === 401 || response.redirected && /login/i.test(response.url)) { location.reload(); return; }
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const html = await response.text();
            if (mine !== controller) return;
            box.innerHTML = html;
            afterRender();
        } catch (error) {
            expectStart = false;
            if (mine !== controller && !timedOut) return;   // superseded by a newer request: that request owns the UI now
            if (timedOut) { if (timeoutNote) timeoutNote.hidden = false; message('The list is taking too long. The request was stopped.', true); }
            else if (error && error.name === 'AbortError') { return; }
            else message('The list could not be loaded. This is not an empty result.', true);
        } finally {
            window.clearTimeout(watchdog);
            if (mine === controller) setBusy(false);   // always restore the button after success, failure or cancellation
        }
    }

    // View (By unit / By instalment): switching re-runs the search with the same filters.
    form.querySelectorAll('[data-view-radio]').forEach(r => r.addEventListener('change', () => form.requestSubmit()));
    form.addEventListener('submit', event => { event.preventDefault(); autoReloads = 0; load(queryFromForm(), true); });
    box.addEventListener('click', event => {
        const link = event.target.closest('a[data-results-link]');
        if (link) {
            event.preventDefault(); autoReloads = 0;
            const q = new URL(link.href, location.origin).searchParams; q.delete('render'); q.delete('handler');
            load(q, true);
            return;
        }
        const toggle = event.target.closest('[data-unit-toggle]');
        if (toggle) { setUnitOpen(toggle.closest('[data-unit]'), toggle.getAttribute('aria-expanded') !== 'true'); return; }
        if (event.target.closest('[data-results-retry]')) { event.preventDefault(); autoReloads = 0; load(currentQuery(), false); }
        if (event.target.closest('[data-poll-resume]')) { event.preventDefault(); autoReloads = 0; load(currentQuery(), false); }
    });
    window.addEventListener('popstate', () => location.reload());
    window.addEventListener('pageshow', event => { if (event.persisted) setBusy(false); });
    window.addEventListener('pagehide', stopMonitor);   // leaving the page ends the loop
    window.addEventListener('campaign-preview-invalidated', stopMonitor);   // a filter changed: the list being watched is obsolete, so is the watch

    // Month + Year set From / To to the first and last day of that month (any leap year handled by Date). Editing a date returns to "Custom range".
    const month = form.querySelector('[data-month-select]'), year = form.querySelector('[data-year-select]');
    const from = form.querySelector('[data-date-from]'), to = form.querySelector('[data-date-to]');
    const pad = n => String(n).padStart(2, '0');
    function syncMonthFromDates() {
        if (!month || !year || !from || !to) return;
        const f = /^(\d{4})-(\d{2})-(\d{2})$/.exec(from.value), t = /^(\d{4})-(\d{2})-(\d{2})$/.exec(to.value);
        if (f && t && f[3] === '01' && f[1] === t[1] && f[2] === t[2] && +t[3] === new Date(Date.UTC(+f[1], +f[2], 0)).getUTCDate()) { month.value = String(+f[2]); year.value = f[1]; }
        else { month.value = ''; if (!from.value && !to.value) year.value = ''; }
    }
    function applyMonth() {
        if (!month || !year || !from || !to || !month.value) return;
        if (!year.value) year.value = String(new Date().getFullYear());
        const y = +year.value, m = +month.value, last = new Date(Date.UTC(y, m, 0)).getUTCDate();
        from.value = `${y}-${pad(m)}-01`; to.value = `${y}-${pad(m)}-${pad(last)}`;
        [from, to].forEach(i => { i.dataset.auto = 'false'; });   // a chosen month is an explicit range: stage defaults must not override it
        autoReloads = 0; load(queryFromForm(), true);
    }
    if (month && year && (!from || !to)) {   // pages without date inputs (Receivables): choosing a month / year reloads the list
        month.addEventListener('change', () => { if (month.value && !year.value) year.value = String(new Date().getFullYear()); if (!month.value) year.value = ''; autoReloads = 0; load(queryFromForm(), true); });
        year.addEventListener('change', () => { if (month.value) { autoReloads = 0; load(queryFromForm(), true); } });
    }
    if (month && year && from && to) {
        month.addEventListener('change', applyMonth);
        year.addEventListener('change', () => { if (month.value) applyMonth(); });
        [from, to].forEach(i => i && i.addEventListener('input', () => { month.value = ''; year.value = ''; }));
    }

    // "Load data" / "Load missing data" (inside the results): start the background load without leaving the page, then show its progress.
    document.addEventListener('submit', async event => {
        const f = event.target;
        if (!(f instanceof HTMLFormElement) || !f.hasAttribute('data-load-form')) return;
        event.preventDefault();
        const button = f.querySelector('[data-load-button]'), note = f.querySelector('.receivables-loading');
        const ret = f.querySelector('input[name=returnUrl]'); if (ret) ret.value = location.pathname + location.search;
        if (button) button.disabled = true; if (note) note.hidden = false;
        try {
            await fetch(f.action, { method: 'POST', body: new FormData(f), credentials: 'same-origin' });
        } catch { /* the reload below shows the real state */ }
        finally { if (button) button.disabled = false; if (note) note.hidden = true; }
        autoReloads = 0; expectStart = true; load(currentQuery(), false);   // the job may take a moment to start: the monitor waits for it instead of concluding "nothing is loading"
    }, true);

    // First load: the shell is already on screen; fetch the results for the URL.
    document.addEventListener('DOMContentLoaded', () => {
        if (box.querySelector('[data-results-loading]')) load(currentQuery(), false);
        else afterRender();
    });
})();
