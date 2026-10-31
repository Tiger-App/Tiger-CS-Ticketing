/* Searchable tower dropdown. Enhances <select data-tower-select>; without JavaScript the plain select keeps working. */
(() => {
    const init = picker => {
        const select = picker.querySelector('[data-tower-select]');
        if (!select || select.dataset.enhanced) return;
        select.dataset.enhanced = 'true';
        const allText = picker.dataset.allText || 'All towers';
        const options = Array.from(select.options).map(o => ({ value: o.value, label: o.text.trim() }));
        const id = select.id;
        const input = document.createElement('input');
        input.type = 'text'; input.className = 'field-input tower-picker__input'; input.autocomplete = 'off';
        input.setAttribute('role', 'combobox'); input.setAttribute('aria-autocomplete', 'list');
        input.setAttribute('aria-expanded', 'false'); input.setAttribute('aria-controls', id + '-list');
        input.setAttribute('aria-label', 'Tower (type a number or name)'); input.placeholder = allText;
        const list = document.createElement('ul');
        list.id = id + '-list'; list.className = 'tower-picker__list'; list.setAttribute('role', 'listbox'); list.hidden = true;
        select.hidden = true;
        select.insertAdjacentElement('afterend', list);
        select.insertAdjacentElement('afterend', input);
        let active = -1; let shown = [];

        const selectedLabel = () => { const o = options.find(x => x.value === select.value); return o && o.value ? o.label : ''; };
        const close = () => { list.hidden = true; input.setAttribute('aria-expanded', 'false'); active = -1; input.removeAttribute('aria-activedescendant'); };
        const choose = option => { const changed = select.value !== option.value; select.value = option.value; input.value = option.value ? option.label : ''; close(); if (changed) select.dispatchEvent(new Event('change', { bubbles: true })); };
        const render = () => {
            const term = input.value.trim().toLowerCase();
            shown = options.filter(o => !term || o.label.toLowerCase().includes(term));
            list.textContent = '';
            if (shown.length === 0) {
                const empty = document.createElement('li'); empty.className = 'tower-picker__empty'; empty.textContent = 'No matching tower'; list.appendChild(empty);
            }
            shown.forEach((o, i) => {
                const li = document.createElement('li');
                li.id = id + '-opt-' + i; li.className = 'tower-picker__option'; li.setAttribute('role', 'option');
                li.setAttribute('aria-selected', i === active ? 'true' : 'false'); li.textContent = o.value ? o.label : allText;
                li.addEventListener('mousedown', e => { e.preventDefault(); choose(o); });
                list.appendChild(li);
            });
            list.hidden = false; input.setAttribute('aria-expanded', 'true');
        };

        input.value = selectedLabel();
        input.addEventListener('focus', () => { input.select(); render(); });
        input.addEventListener('input', () => { active = -1; render(); });
        input.addEventListener('keydown', e => {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                e.preventDefault(); if (list.hidden) render();
                active = (active + (e.key === 'ArrowDown' ? 1 : -1) + shown.length) % Math.max(shown.length, 1);
                render(); input.setAttribute('aria-activedescendant', id + '-opt-' + active);
            } else if (e.key === 'Enter') {
                if (!list.hidden && active >= 0 && shown[active]) { e.preventDefault(); choose(shown[active]); }
                else if (!list.hidden && shown.length === 1 && input.value.trim()) { e.preventDefault(); choose(shown[0]); }
            } else if (e.key === 'Escape') { close(); input.value = selectedLabel(); }
        });
        input.addEventListener('blur', () => {
            // Leaving the box with text that is not a chosen tower restores the current choice; an empty box means "All towers".
            if (!input.value.trim() && select.value !== '') { select.value = ''; select.dispatchEvent(new Event('change', { bubbles: true })); }
            input.value = selectedLabel(); close();
        });
    };
    document.addEventListener('DOMContentLoaded', () => document.querySelectorAll('[data-tower-picker]').forEach(init));
})();
