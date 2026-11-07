/*
  Collections Campaigns: select customers, then Approve & Send. Ticked customers survive paging and the asynchronous results reload (session only; the server
  re-checks every ticked customer at approval and again before upload). Also polls the approval-data refresh and a running send, so neither can sit on "Queued".
*/
(() => {
    const KEY = 'collections-campaign-selection';
    const load = () => { try { return new Set(JSON.parse(sessionStorage.getItem(KEY) || '[]')); } catch { return new Set(); } };
    const save = set => { try { sessionStorage.setItem(KEY, JSON.stringify([...set])); } catch { /* storage unavailable */ } };
    const selected = load();
    // Manual destination: remembered for the session so paging the same preview keeps it.
    const LIST_KEY = 'collections-direct-contact-list';
    const readList = () => { try { return sessionStorage.getItem(LIST_KEY) || ''; } catch { return ''; } };
    const writeList = value => { try { sessionStorage.setItem(LIST_KEY, value); } catch { /* storage unavailable */ } };

    const sync = () => {
        const form = document.querySelector('[data-approve-form]');
        document.querySelectorAll('.campaign-select').forEach(box => { box.checked = selected.has(box.dataset.unitKey); });
        document.querySelectorAll('[data-selected-count]').forEach(c => { if (c.textContent !== String(selected.size)) c.textContent = String(selected.size); });   // sync() also runs from a MutationObserver: write only on change
        const hidden = document.querySelector('[data-selected-units]');
        if (hidden) hidden.value = [...selected].join(',');
        const all = document.querySelector('[data-select-page]');
        if (all) {
            // Only Ready customers have an enabled box; Select all covers exactly those and is disabled (server-side, with its reason) when there are none.
            const boxes = [...document.querySelectorAll('.campaign-select:not(:disabled)')];
            const ticked = boxes.filter(b => b.checked).length;
            all.checked = boxes.length > 0 && ticked === boxes.length; all.indeterminate = ticked > 0 && ticked < boxes.length;
        }
        const listBox = document.querySelector('select[data-contact-list]');
        // A destination the person chose is kept while they page through the same preview (the server renders the campaign's own list by default); a new preview starts from the default again.
        if (listBox && readList() && listBox.value !== readList() && [...listBox.options].some(o => o.value === readList())) listBox.value = readList();
        if (form) {
            const button = form.querySelector('[data-approve-button]');
            const mode = form.querySelector('input[name="selectionMode"]:checked');
            // Enabled only for a current preview (this form exists only inside the current results), a valid reconciliation (server: data-blocked) and eligible customers chosen.
            const blocked = !!button && button.dataset.blocked === 'true';
            const nothingTicked = !!mode && mode.value === 'SelectedUnits' && selected.size === 0;
            // Manual always has a destination (the campaign's own list unless another suitable one was chosen).
            const noList = !!listBox && !listBox.value;
            if (button) button.disabled = blocked || nothingTicked || !mode || noList;
            // The selection stays visible next to Approve & Send (written only when it changes: sync() also runs from a MutationObserver).
            const summary = form.querySelector('[data-selected-summary]');
            if (summary && mode) {
                const allCount = (form.querySelector('[data-all-count]') || {}).textContent || '0';
                const text = mode.value === 'AllMatching' ? `All ${allCount} eligible` : `${selected.size} selected`;
                if (summary.textContent !== text) summary.textContent = text;
            }
            // Export for Genesys needs no selection: with nothing ticked it exports every customer matching the filters; ticks only narrow it. Only the server-side block (permission, no data) disables it.
            const exportButton = form.querySelector('[data-export-genesys-button]');
            if (exportButton) exportButton.disabled = exportButton.dataset.blocked === 'true';
            const exportReason = form.querySelector('[data-export-genesys-reason]');
            if (exportReason && exportButton) {
                if (!exportReason.dataset.default) exportReason.dataset.default = exportReason.textContent;
                const narrowed = !!mode && mode.value === 'SelectedUnits' && selected.size > 0;
                const exportText = exportButton.dataset.blocked !== 'true' && narrowed ? `Only the ${selected.size} ticked customer(s) are exported. ` + exportReason.dataset.default : exportReason.dataset.default;
                if (exportReason.textContent !== exportText) exportReason.textContent = exportText;
            }
            const reason = form.querySelector('[data-approve-reason]');
            if (reason) {
                if (!reason.dataset.default) reason.dataset.default = reason.textContent;
                // Only when it differs: sync() also runs from a MutationObserver on the results, so an unconditional write would re-trigger itself forever.
                const text = !blocked && noList ? 'Choose the Genesys contact list (Current Month or Follow Up).'
                    : !blocked && nothingTicked ? 'Tick at least one Ready customer, or choose all eligible customers.' : reason.dataset.default;
                if (reason.textContent !== text) reason.textContent = text;
            }
        }
    };

    document.addEventListener('change', event => {
        const t = event.target;
        if (t.classList && t.classList.contains('campaign-select')) { t.checked ? selected.add(t.dataset.unitKey) : selected.delete(t.dataset.unitKey); save(selected); sync(); }
        else if (t.matches && t.matches('[data-select-page]')) { document.querySelectorAll('.campaign-select:not(:disabled)').forEach(b => { b.checked = t.checked; t.checked ? selected.add(b.dataset.unitKey) : selected.delete(b.dataset.unitKey); }); save(selected); sync(); }
        else if (t.matches && t.matches('input[name="selectionMode"]')) sync();
        else if (t.matches && t.matches('select[data-contact-list]')) { writeList(t.value); sync(); }
    });
    document.addEventListener('click', event => {
        if (event.target.closest && event.target.closest('[data-clear-selection]')) { selected.clear(); save(selected); sync(); }
    });
    // A new preview (filters changed) starts a new selection; paging keeps it.
    const filter = document.querySelector('[data-results-form]');
    if (filter) filter.addEventListener('submit', () => { selected.clear(); save(selected); writeList(''); });
    // Changed filters clear the customers (filters script); the ticks go with them.
    window.addEventListener('campaign-preview-invalidated', () => { selected.clear(); save(selected); writeList(''); sync(); });
    // The confirmation is sent by collections-campaign-confirm.js (loading state, result, one submission). This is only the plain form post, when that script did not take the submit over:
    // a double click still cannot submit twice (the idempotency key would replay it anyway), and the ticks go once the approval is posted.
    document.addEventListener('submit', event => {
        const form = event.target;
        if (form.matches && form.matches('[data-confirm-form]') && !event.defaultPrevented) {
            const button = form.querySelector('[data-confirm-button]');
            if (button && button.disabled) { event.preventDefault(); return; }
            if (button && event.submitter === button) { button.disabled = true; button.textContent = 'Sending…'; selected.clear(); save(selected); }
        }
    });
    // The approval was accepted by the server (the dialog's own request): the ticked customers are spent. A failed or refused confirmation keeps them, so the list can be reviewed again.
    window.addEventListener('collections-send-accepted', () => { selected.clear(); save(selected); sync(); });
    // An export button is held for a few seconds once pressed: a double click sends one request, not two. (The download does not reload the page, so the button is released on a timer, and
    // sync() then restores its real state.) The server would refuse a second simultaneous export anyway.
    document.addEventListener('submit', event => {
        const button = event.submitter;
        if (!button || !button.matches || !button.matches('[data-export-genesys-button],[data-export-review-manual]')) return;
        button.disabled = true;
        setTimeout(() => { button.disabled = false; sync(); }, 4000);
    });
    const results = document.querySelector('[data-results]');
    if (results) new MutationObserver(sync).observe(results, { childList: true, subtree: true });
    sync();

    // Manual confirmation: the server renders the dialog open; upgrade it to a modal so the count, amount and destination list are the only thing to act on.
    const directConfirm = document.querySelector('dialog[data-manual-confirm]');
    if (directConfirm && typeof directConfirm.showModal === 'function') { directConfirm.close(); directConfirm.showModal(); }

    // A chained, bounded poll: the next request is scheduled only after the previous answer, it ends when the server says the work is over, after maxMs, or after three failures in a row.
    // Ending without an answer is said on the page (never an endless "Queued"), and the person can look again by reloading.
    const boundedPoll = (url, firstMs, everyMs, maxMs, onState, giveUp) => {
        const started = Date.now(); let errors = 0;
        const tick = async () => {
            if (document.hidden) { window.setTimeout(tick, everyMs); return; }
            try {
                const response = await fetch(url, { headers: { Accept: 'application/json' }, cache: 'no-store' });
                if (!response.ok) throw new Error(`HTTP ${response.status}`);
                const state = await response.json(); errors = 0;
                if (!state.active) { window.location.reload(); return; }
                onState(state);
            } catch { if (++errors >= 3) { giveUp('its status could not be read'); return; } }
            if (Date.now() - started >= maxMs) { giveUp('it is taking longer than expected'); return; }
            window.setTimeout(tick, everyMs);
        };
        window.setTimeout(tick, firstMs);
    };
    const stopped = (host, why) => {
        const note = document.createElement('p'); note.className = 'receivables-notice receivables-notice--warning'; note.setAttribute('role', 'alert'); note.setAttribute('data-poll-stopped', '');
        note.textContent = `This page stopped checking automatically because ${why}. Reload the page to see the current state.`;
        host.appendChild(note);
    };

    // Approval-data refresh: poll until it ends, then reload so a failure (with its reason and Retry) or the finished data appears.
    const run = document.querySelector('[data-run-active="true"]');
    if (run) {
        const bar = run.querySelector('[data-run-progress]');
        const phase = run.querySelector('[data-run-phase]');
        boundedPoll('/Collections/Campaigns?handler=Run', 2000, 3000, 20 * 60 * 1000, state => {
            if (bar) bar.value = state.progressPercent;
            if (phase) phase.textContent = state.phase + '…';
        }, why => stopped(run, why));
    }

    // A running send: poll its status and reload when it ends.
    const send = document.querySelector('[data-dispatch-active="true"]');
    if (send) {
        const id = send.dataset.dispatchId;
        boundedPoll('/Collections/Campaigns?handler=Send&dispatch=' + encodeURIComponent(id), 3000, 4000, 60 * 60 * 1000, () => {}, why => stopped(send, why));
    }
})();
