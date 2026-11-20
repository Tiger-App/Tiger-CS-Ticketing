// Ticket Details > Activity: client-side filtering and expand/collapse of the already-rendered timeline. Nothing is fetched or removed
// from the page; with scripting off every event is simply shown.
(function () {
    'use strict';
    const root = document.querySelector('[data-activity-timeline]');
    if (!root) return;
    const chips = document.querySelectorAll('[data-activity-filters] [data-filter]');
    const none = root.querySelector('[data-activity-none]');
    const toggleAll = document.querySelector('[data-activity-toggle-all]');
    const rows = () => root.querySelectorAll('details.act-row:not(.act-row--flat)');

    function applyFilter(key) {
        chips.forEach(c => {
            const on = c.dataset.filter === key;
            c.classList.toggle('is-active', on);
            c.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
        let shown = 0;
        root.querySelectorAll('[data-activity-day]').forEach(day => {
            let dayShown = 0;
            day.querySelectorAll('.act-item').forEach(li => {
                const ok = key === 'all' || (li.dataset.filters || '').split(' ').includes(key);
                li.hidden = !ok;
                if (ok) dayShown++;
            });
            day.hidden = dayShown === 0;
            shown += dayShown;
        });
        if (none) none.hidden = shown > 0;
    }

    chips.forEach(c => c.addEventListener('click', () => applyFilter(c.dataset.filter)));

    // A summary of a row with nothing to expand must not toggle.
    root.addEventListener('click', e => {
        const s = e.target.closest('details.act-row--flat > summary');
        if (s) e.preventDefault();
    });

    if (toggleAll) {
        toggleAll.addEventListener('click', () => {
            const expand = toggleAll.getAttribute('aria-expanded') !== 'true';
            rows().forEach(d => { d.open = expand; });
            toggleAll.setAttribute('aria-expanded', expand ? 'true' : 'false');
            toggleAll.textContent = expand ? 'Collapse all' : 'Expand all';
        });
    }
})();
