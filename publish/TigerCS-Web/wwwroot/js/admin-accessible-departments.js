/* Administration -> Users: the searchable multi-select behind "Accessible Departments". A plain checkbox list posts with the form, so it works
   without script; this only adds the search box (it hides non-matching rows, never unticks them, so a hidden selection is still saved). */
(() => {
    document.querySelectorAll('[data-accessible-departments]').forEach(form => {
        const search = form.querySelector('[data-ad-search]');
        const empty = form.querySelector('[data-ad-empty]');
        const rows = Array.from(form.querySelectorAll('[data-ad-list] .ad-option'));
        if (!search) return;
        search.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
        search.addEventListener('input', () => {
            const term = search.value.trim().toLowerCase();
            let shown = 0;
            rows.forEach(row => {
                const match = term === '' || row.textContent.toLowerCase().includes(term);
                row.hidden = !match;
                if (match) shown++;
            });
            if (empty) empty.hidden = shown > 0;
        });
    });
})();
