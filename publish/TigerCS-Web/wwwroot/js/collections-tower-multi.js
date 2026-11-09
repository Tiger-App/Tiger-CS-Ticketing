/* Multi-select Project / Tower filter: search, Select all, Clear all, "N towers selected" summary. Checkboxes are named towerId and live inside the filter form,
   so the existing change handling (collections-campaign-filters.js) sees every change. No selection = All projects.
   The same component drives the Floor filter (data-field-name="floor", data-noun="floors"): there, when the towers change, the list is reloaded from data-floors-url (a page handler that
   returns the floors of the chosen towers) and the floors that still exist stay ticked; ticked floors that no longer exist are dropped. */
(() => {
    const init = root => {
        const box = root.querySelector('details');
        const summary = root.querySelector('[data-tower-summary]');
        const search = root.querySelector('[data-tower-search]');
        const empty = root.querySelector('[data-tower-empty]');
        const list = root.querySelector('[data-tower-list]');
        const fieldName = root.dataset.fieldName || 'towerId';
        const noun = root.dataset.noun || 'towers';
        const allSelectedText = root.dataset.allSelectedText || 'All towers selected';
        let items = Array.from(root.querySelectorAll('[data-tower-item]'));
        const boxes = () => Array.from(root.querySelectorAll('input[type=checkbox][name=' + fieldName + ']'));
        const allText = root.dataset.allText || 'All towers';
        const count = root.querySelector('[data-tower-count]');
        const update = () => {
            const checked = boxes().filter(b => b.checked);
            const total = boxes().length;
            if (count) { count.textContent = checked.length + ' of ' + total + ' selected'; }
            summary.textContent = checked.length === 0 ? allText
                : checked.length === total && total > 1 ? allSelectedText
                : checked.length === 1 ? (checked[0].closest('li').dataset.display || checked[0].closest('label').textContent).trim()
                : checked.length + ' ' + noun + ' selected';
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
        list.addEventListener('keydown', e => {
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

        // ---- Floor only: reload the options whenever the towers of the same form change (and once the page is ready, in case the towers were restored by the browser).
        const url = root.dataset.floorsUrl;
        const form = root.closest('form');
        if (url && form) {
            const note = root.querySelector('[data-floor-note]');
            const emptyItem = root.querySelector('[data-tower-empty]');
            let sequence = 0;
            const render = floors => {
                const kept = new Set(boxes().filter(b => b.checked).map(b => b.value));
                list.querySelectorAll('[data-tower-item]').forEach(li => li.remove());
                floors.forEach(f => {
                    const li = document.createElement('li');
                    li.setAttribute('data-tower-item', '');
                    li.dataset.label = (f.label + ' ' + f.value).toLowerCase();
                    li.dataset.display = f.label;
                    const label = document.createElement('label');
                    const input = document.createElement('input');
                    input.type = 'checkbox'; input.name = fieldName; input.value = String(f.value); input.checked = kept.has(String(f.value));
                    const code = document.createElement('span'); code.className = 'tower-multi__code'; code.textContent = f.label;
                    const name = document.createElement('span'); name.className = 'tower-multi__name'; name.textContent = f.count + (f.count === 1 ? ' unit' : ' units');
                    label.append(input, ' ', code, name); li.appendChild(label);
                    list.insertBefore(li, emptyItem);
                });
                items = Array.from(list.querySelectorAll('[data-tower-item]'));
                search.value = ''; empty.hidden = true;
                const before = Array.from(kept).sort().join(',');
                const after = boxes().filter(b => b.checked).map(b => b.value).sort().join(',');
                update();
                // A ticked floor that the new towers do not have was dropped: that IS a filter change (the preview on screen no longer matches).
                if (before !== after) root.dispatchEvent(new Event('change', { bubbles: true }));
            };
            const reload = async () => {
                const mine = ++sequence;
                const q = new URLSearchParams(); q.set('handler', 'Floors');
                form.querySelectorAll('input[type=checkbox][name=towerId]:checked').forEach(t => q.append('towerId', t.value));
                try {
                    const response = await fetch(url + '?' + q.toString(), { credentials: 'same-origin', headers: { Accept: 'application/json' } });
                    if (!response.ok) throw new Error('floors ' + response.status);
                    const data = await response.json();
                    if (mine !== sequence) return;                       // a newer tower change superseded this answer
                    if (note) { note.hidden = !data.failed; note.textContent = data.failed ? 'The floor list could not be loaded; only "' + allText + '" is available. Retry the page.' : ''; }
                    render(data.failed ? [] : data.floors);
                } catch {
                    if (mine !== sequence) return;
                    if (note) { note.hidden = false; note.textContent = 'The floor list could not be refreshed. Retry the page.'; }
                }
            };
            let timer = 0;
            form.addEventListener('change', e => {
                if (e.target && e.target.matches && e.target.matches('input[type=checkbox][name=towerId]')) { window.clearTimeout(timer); timer = window.setTimeout(reload, 150); }
            });
            // The tower picker's own Select all / Clear all dispatch a bubbling 'change' from the picker root (not from a checkbox): reload for those too.
            form.addEventListener('change', e => {
                if (e.target && e.target.hasAttribute && e.target.hasAttribute('data-tower-multi') && e.target !== root && !e.target.hasAttribute('data-floors-url')) { window.clearTimeout(timer); timer = window.setTimeout(reload, 150); }
            });
        }
    };
    document.addEventListener('DOMContentLoaded', () => document.querySelectorAll('[data-tower-multi]').forEach(init));
})();
