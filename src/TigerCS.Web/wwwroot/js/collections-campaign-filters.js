/*
  Collections Campaigns, filter first: the Period shortcuts, the collapsible Advanced filters and the invalidation of a stale preview.
  - Period only fills the visible From / To dates (the dates stay the single source of the range; nothing is substituted).
  - Customers load only from "Preview Customers" (receivables.js). Changing any filter afterwards clears the customers and the ticked selection,
    so the table on screen always matches the filters on screen.
  - The financial snapshot status is lifted out of each results fragment into its own box, which filter changes never clear.
*/
(() => {
    const SELECTION_KEY = 'collections-campaign-selection';
    const form = document.querySelector('[data-campaign-filters]');
    const box = document.querySelector('[data-results]');
    if (!form || !box) return;
    const period = form.querySelector('[data-period]');
    const from = form.querySelector('[data-date-from]');
    const to = form.querySelector('[data-date-to]');
    const date = form.querySelector('[data-campaign-date]');
    const advanced = form.querySelector('[data-advanced-filters]');
    const snapshotBox = document.querySelector('[data-snapshot-box]');
    const kpiBox = document.querySelector('[data-kpi-box]');
    const pad = n => String(n).padStart(2, '0');
    const iso = (y, m, d) => `${y}-${pad(m)}-${pad(d)}`;
    const daysIn = (y, m) => new Date(Date.UTC(y, m, 0)).getUTCDate();

    // ---- Sending mode (a Manual / Auto segmented toggle): Manual shows the date beside the toggle as "Send date"; Auto keeps it as "Preview date" under More filters and shows the policy
    // schedule as a small label. One input exists either way (it is moved, never copied), so exactly one businessDate is ever submitted. Changing the mode is a filter change like any
    // other: the customers on screen are cleared until Preview Customers is pressed.
    const modeRadios = [...form.querySelectorAll('input[name="sendingMode"]')];
    const dateGroup = form.querySelector('[data-date-group]');
    const dateSlot = form.querySelector('[data-date-slot]');
    const advancedGrid = form.querySelector('.campaign-advanced__grid');
    const currentMode = () => (modeRadios.find(r => r.checked) || {}).value || 'Auto';
    const applyMode = () => {
        if (modeRadios.length === 0) return;
        const mode = currentMode(), manual = mode === 'Manual', direct = mode === 'DirectSend';
        // Direct Send has no campaign stage and no stage/send date: those controls are hidden AND disabled, so neither is ever submitted.
        const stageSelect = form.querySelector('[data-campaign-stage]');
        if (stageSelect) stageSelect.disabled = direct;
        if (date) date.disabled = direct;
        if (dateGroup) dateGroup.hidden = direct;
        form.querySelectorAll('[data-mode-not]').forEach(e => { e.hidden = e.dataset.modeNot === mode; });
        form.querySelectorAll('[data-mode-only]').forEach(e => { e.hidden = e.dataset.modeOnly !== mode; });
        if (direct && advanced) advanced.open = true;
        if (dateGroup && dateSlot && advancedGrid) {
            const label = dateGroup.querySelector('[data-date-label]');
            if (label) label.textContent = manual ? 'Send date' : 'Preview date';
            if (manual) { dateSlot.prepend(dateGroup); if (date && !date.value && period) date.value = period.dataset.today || date.value; }
            else advancedGrid.prepend(dateGroup);
        }
        form.querySelectorAll('[data-mode-hint]').forEach(h => { h.hidden = h.dataset.modeHint !== mode; });
    };
    modeRadios.forEach(r => r.addEventListener('change', applyMode));

    // ---- Period -> From / To (same ranges as CollectionsDateRanges on the server)
    const anchor = () => {
        if (date && /^\d{4}-\d{2}-\d{2}$/.test(date.value)) return date.value;
        return (period && period.dataset.today) || new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Dubai' }).format(new Date());
    };
    const ranges = () => {
        const [y, m, d] = anchor().split('-').map(Number);
        const back = new Date(Date.UTC(y, m - 1 - 6, 1));                       // six calendar months back, day clamped like DateOnly.AddMonths
        const by = back.getUTCFullYear(), bm = back.getUTCMonth() + 1;
        return {
            UpToToday: { from: '', to: iso(y, m, d) },
            ThisMonth: { from: iso(y, m, 1), to: iso(y, m, daysIn(y, m)) },
            LastSixMonths: { from: iso(by, bm, Math.min(d, daysIn(by, bm))), to: iso(y, m, d) },
        };
    };
    const syncPeriodFromDates = () => {
        if (!period) return;
        const r = ranges();
        const hit = Object.keys(r).find(k => r[k].from === from.value && r[k].to === to.value);
        period.value = hit || 'Custom';
    };
    const applyPeriod = () => {
        if (!period || !from || !to) return;
        if (period.value === 'Custom') { if (advanced) advanced.open = true; from.focus(); return; }
        const r = ranges()[period.value];
        from.value = r.from; to.value = r.to;
    };
    if (period && from && to) {
        period.addEventListener('change', applyPeriod);
        [from, to].forEach(i => i.addEventListener('input', syncPeriodFromDates));
        if (date) date.addEventListener('change', () => { if (period.value !== 'Custom') applyPeriod(); else syncPeriodFromDates(); });
    }

    // ---- Stale previews
    const serialize = () => { const q = new URLSearchParams(); new FormData(form).forEach((v, k) => { if (k !== 'render') q.append(k, String(v)); }); return q.toString(); };
    let applied = serialize();      // the filters the customers on screen (if any) were loaded with
    let stale = false;
    const clearSelection = () => { try { sessionStorage.removeItem(SELECTION_KEY); } catch { /* storage unavailable */ } };
    const showStale = () => {
        box.innerHTML = '';
        if (kpiBox) kpiBox.innerHTML = '';
        const panel = document.createElement('div'); panel.className = 'panel campaign-idle'; panel.setAttribute('role', 'status'); panel.setAttribute('data-results-stale', '');
        const title = document.createElement('p'); title.className = 'empty-state__title'; title.textContent = 'Filters changed.';
        const hint = document.createElement('p'); hint.className = 'receivables-meta'; hint.textContent = 'The previous customers and ticks were cleared. Click Preview Customers to load the list for the new filters.';
        panel.append(title, hint); box.appendChild(panel);
    };
    const hasPreview = () => box.querySelector('[data-preview-current]') !== null || box.querySelector('[data-campaign-error]') !== null;
    const onFilterEdit = event => {
        const t = event.target;
        if (!(t instanceof Element) || t.form !== form) return;
        if (!hasPreview() || serialize() === applied) return;
        stale = true; clearSelection();
        showStale();
        window.dispatchEvent(new CustomEvent('campaign-preview-invalidated'));
    };
    form.addEventListener('change', onFilterEdit);
    document.addEventListener('input', onFilterEdit);           // the search box lives outside the <form> element (form="campaignFilters")
    document.addEventListener('change', event => { if (event.target instanceof Element && event.target.form === form && !form.contains(event.target)) onFilterEdit(event); });
    let submitted = applied;       // what the request in flight was started with
    form.addEventListener('submit', () => { stale = false; clearSelection(); submitted = serialize(); });

    // ---- Lift the snapshot status out of each fragment so it survives clearing the customers
    const lift = () => {
        const status = box.querySelector('[data-snapshot-status]');
        if (status && snapshotBox) {
            snapshotBox.querySelectorAll('[data-snapshot-status], [data-snapshot-idle]').forEach(n => n.remove());
            snapshotBox.appendChild(status);
        }
        // The KPI cards belong to the customers on screen: lifted above the results section with them; removed when the preview is stale or a fragment arrives without any (an error, nothing loaded).
        const kpis = box.querySelector('[data-campaign-kpis]');
        if (stale) { if (kpiBox) kpiBox.innerHTML = ''; }
        else if (kpis && kpiBox) kpiBox.replaceChildren(kpis);
        else if (kpiBox && box.querySelector('[data-campaign-error], [data-nothing-loaded]')) kpiBox.innerHTML = '';
        if (!box.querySelector('[data-preview-current], [data-campaign-error], [data-nothing-loaded]') || stale) return;
        // A filter edited while the request was in flight: those customers do not match the filters on screen.
        if (serialize() !== submitted) { stale = true; clearSelection(); showStale(); window.dispatchEvent(new CustomEvent('campaign-preview-invalidated')); return; }
        applied = submitted;
    };
    new MutationObserver(lift).observe(box, { childList: true });
    lift();
})();
