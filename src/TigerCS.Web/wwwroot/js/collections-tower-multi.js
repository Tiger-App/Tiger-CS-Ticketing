/* Multi-select Project / Tower filter: search, Select all, Clear all, "N towers selected" summary. Checkboxes are named towerId and live inside the filter form,
   so the existing change handling (collections-campaign-filters.js) sees every change. No selection = All projects. */
(() => {
    const init = root => {
        const box = root.querySelector('details');
        const summary = root.querySelector('[data-tower-summary]');
        const search = root.querySelector('[data-tower-search]');
        const empty = root.querySelector('[data-tower-empty]');
        const items = Array.from(root.querySelectorAll('[data-tower-item]'));
        const boxes = () => Array.from(root.querySelectorAll('input[type=checkbox][name=towerId]'));
        const allText = root.dataset.allText || 'All towers';
        const count = root.querySelector('[data-tower-count]');
        const update = () => {
            const checked = boxes().filter(b => b.checked);
            const total = boxes().length;
            if (count) { count.textContent = checked.length + ' of ' + total + ' selected'; }
            summary.textContent = checked.length === 0 ? allText
                : checked.length === total && total > 1 ? 'All towers selected'
                : checked.length === 1 ? (checked[0].closest('li').dataset.display || checked[0].closest('label').textContent).trim()
                : checked.length + ' towers selected';
        };
        const changed = () => { update(); root.dispatchEvent(new Event('change', { bubbles: true })); };
        const visible = () => items.filter(li => !li.hidden);
        root.addEventListener('change', e => { if (e.target.matches && e.target.matches('input[type=checkbox]')) update(); });
        search.addEventListener('input', () => {
            const term = search.value.trim().toLowerCase();
            items.forEach(li => { li.hidden = term !== '' && !li.dataset.label.includes(term); });
            empty.hidden = visible().length !== 0;
        });
        search.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
        // Select all / Clear all act on the towers the search currently shows.
        root.querySelector('[data-tower-all]').addEventListener('click', () => { visible().forEach(li => { li.querySelector('input').checked = true; }); changed(); });
        root.querySelector('[data-tower-clear]').addEventListener('click', () => { visible().forEach(li => { li.querySelector('input').checked = false; }); changed(); });
        // Keyboard: Down opens the panel from the summary and enters the search; Down / Up walk the visible options (Home / End jump); Up from the first option returns to the search.
        const options = () => visible().map(li => li.querySelector('input'));
        const focusOption = i => { const o = options(); if (o.length) { o[Math.max(0, Math.min(i, o.length - 1))].focus(); } };
        summary.addEventListener('keydown', e => {
            if (e.key === 'ArrowDown') { e.preventDefault(); box.open = true; search.focus(); }
        });
        search.addEventListener('keydown', e => {
            if (e.key === 'ArrowDown') { e.preventDefault(); focusOption(0); }
        });
        root.querySelector('[data-tower-list]').addEventListener('keydown', e => {
            const o = options(), i = o.indexOf(document.activeElement);
            if (i < 0) { return; }
            if (e.key === 'ArrowDown') { e.preventDefault(); focusOption(i + 1); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); if (i === 0) { search.focus(); } else { focusOption(i - 1); } }
            else if (e.key === 'Home') { e.preventDefault(); focusOption(0); }
            else if (e.key === 'End') { e.preventDefault(); focusOption(o.length - 1); }
        });
        // Opening with the mouse or keyboard puts the cursor in the search (not on touch screens, where it would raise the keyboard).
        // 'toggle' fires asynchronously, so only move focus when it is still on the summary (never steal it from an option the user already reached).
        box.addEventListener('toggle', () => { if (box.open && document.activeElement === summary && !window.matchMedia('(pointer: coarse)').matches) { search.focus(); } });
        document.addEventListener('click', e => { if (box.open && !root.contains(e.target)) box.open = false; });
        root.addEventListener('keydown', e => { if (e.key === 'Escape' && box.open) { box.open = false; summary.focus(); } });
        update();
    };
    document.addEventListener('DOMContentLoaded', () => document.querySelectorAll('[data-tower-multi]').forEach(init));
})();
