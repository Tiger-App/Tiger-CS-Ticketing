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
        const allText = root.dataset.allText || 'All projects';
        const update = () => {
            const checked = boxes().filter(b => b.checked);
            const total = boxes().length;
            summary.textContent = checked.length === 0 ? allText
                : checked.length === total && total > 1 ? 'All towers selected'
                : checked.length === 1 ? checked[0].closest('label').querySelector('span').textContent.trim()
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
        document.addEventListener('click', e => { if (box.open && !root.contains(e.target)) box.open = false; });
        root.addEventListener('keydown', e => { if (e.key === 'Escape' && box.open) { box.open = false; summary.focus(); } });
        update();
    };
    document.addEventListener('DOMContentLoaded', () => document.querySelectorAll('[data-tower-multi]').forEach(init));
})();
