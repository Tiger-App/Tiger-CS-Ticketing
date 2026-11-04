/* Tower assignments page: live search over the table, removable tags that mirror the ticked towers of the multi-select, confirm before copying.
   Every control also works without this script (the server renders the table, the editor and the checkbox list). */
(() => {
    document.addEventListener('DOMContentLoaded', () => {
        const rows = Array.from(document.querySelectorAll('[data-ta-row]'));
        const empty = document.querySelector('[data-ta-empty]');
        const search = document.querySelector('[data-ta-search]');
        if (search) {
            search.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
            search.addEventListener('input', () => {
                const term = search.value.trim().toLowerCase();
                let shown = 0;
                rows.forEach(r => {
                    const match = term === '' || r.dataset.search.includes(term);
                    r.hidden = !match;
                    if (match) shown++;
                    const editor = r.nextElementSibling && r.nextElementSibling.classList.contains('tower-assign__editor') ? r.nextElementSibling : null;
                    if (editor) editor.hidden = !match;
                });
                if (empty) empty.hidden = shown !== 0;
            });
        }

        document.querySelectorAll('form[data-ta-confirm]').forEach(f =>
            f.addEventListener('submit', e => { if (!window.confirm(f.dataset.taConfirm)) e.preventDefault(); }));

        const editor = document.querySelector('[data-ta-editor]');
        if (!editor) return;
        const tags = editor.querySelector('[data-ta-tags]');
        const boxes = () => Array.from(editor.querySelectorAll('input[type=checkbox][name=towerId]'));
        const render = () => {
            tags.textContent = '';
            const checked = boxes().filter(b => b.checked);
            if (checked.length === 0) {
                const none = document.createElement('span');
                none.className = 'receivables-meta';
                none.textContent = 'No towers selected';
                tags.appendChild(none);
                return;
            }
            checked.forEach(b => {
                const li = document.createElement('span');
                li.className = 'tower-tag';
                li.appendChild(document.createTextNode(b.closest('label').querySelector('span').textContent.trim()));
                const x = document.createElement('button');
                x.type = 'button';
                x.setAttribute('aria-label', 'Remove ' + li.textContent);
                x.textContent = '×';
                x.addEventListener('click', () => { b.checked = false; b.dispatchEvent(new Event('change', { bubbles: true })); });
                li.appendChild(x);
                tags.appendChild(li);
            });
        };
        editor.addEventListener('change', render);
        render();
    });
})();
