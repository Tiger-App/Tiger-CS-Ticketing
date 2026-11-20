/* Ticket Details -> Actions -> Assign / Reassign.
   Department and Employee are plain <select>s, so the form still posts without script. Here each one is upgraded into a searchable dropdown
   (a button that opens a popover with a search box and a listbox); the <select> stays in the form, hidden, as the single source of truth.
   Picking a department loads that department's ACTIVE employees (the server's roster, via the page's ReassignEmployees handler); the Employee list always keeps a
   "No specific employee" entry, which means a department-only assignment. Assignment rules themselves are unchanged and live on the server. */
(() => {
    document.addEventListener('DOMContentLoaded', () => document.querySelectorAll('[data-reassign]').forEach(init));

    const CHEVRON = '<svg class="ic combo__chevron" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="m6 9.5 6 6 6-6"/></svg>';
    const SEARCH = '<svg class="ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.35-4.35"/></svg>';
    const CHECK = '<svg class="ic combo__check" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="m5 12.5 4.5 4.5L19 7.5"/></svg>';
    let comboSeq = 0;

    /* ---- Searchable dropdown over a <select data-combo> ---- */
    function enhance(select, labelText) {
        const id = 'combo-' + (++comboSeq);
        const root = document.createElement('div');
        root.className = 'combo';
        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'combo__trigger';
        trigger.id = id + '-trigger';
        trigger.setAttribute('aria-haspopup', 'listbox');
        trigger.setAttribute('aria-expanded', 'false');
        trigger.setAttribute('aria-controls', id + '-pop');
        if (select.id) {
            const label = document.querySelector('label[for="' + select.id + '"]');
            if (label) { if (!label.id) label.id = id + '-label'; trigger.setAttribute('aria-labelledby', label.id + ' ' + trigger.id); label.addEventListener('click', e => { e.preventDefault(); if (!trigger.disabled) trigger.focus(); }); }
        }
        trigger.innerHTML = '<span class="combo__value"></span><span class="combo__spinner" aria-hidden="true"></span>' + CHEVRON;
        const pop = document.createElement('div');
        pop.className = 'combo__pop';
        pop.id = id + '-pop';
        pop.hidden = true;
        pop.innerHTML = '<div class="combo__search">' + SEARCH + '<input type="search" class="combo__input" autocomplete="off" spellcheck="false" role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls="' + id + '-list"></div>'
            + '<ul class="combo__list" role="listbox" id="' + id + '-list" aria-label="' + labelText + '"></ul><p class="combo__empty" hidden></p>';
        const input = pop.querySelector('.combo__input');
        const list = pop.querySelector('.combo__list');
        const empty = pop.querySelector('.combo__empty');
        input.placeholder = select.dataset.comboPlaceholder || 'Search…';
        input.setAttribute('aria-label', input.placeholder.replace(/…$/, ''));
        root.append(trigger, pop);
        select.before(root);
        select.hidden = true;                // still in the form and still posted; the dropdown is its face
        select.tabIndex = -1;
        select.setAttribute('aria-hidden', 'true');

        let items = [];                      // { opt, li, text }
        let active = -1;
        let state = { loading: false, loadingText: '', note: '' };
        const isOpen = () => !pop.hidden;

        const labelOf = o => o.dataset.label || o.textContent.trim();

        function selectedOption() { return select.options[select.selectedIndex] || null; }

        function paintTrigger() {
            const opt = selectedOption();
            const value = trigger.querySelector('.combo__value');
            value.textContent = '';
            if (state.loading) {
                value.append(Object.assign(document.createElement('span'), { className: 'combo__text combo__text--muted', textContent: state.loadingText || 'Loading…' }));
            } else if (opt) {
                value.append(Object.assign(document.createElement('span'), { className: 'combo__text' + (opt.value === '' ? ' combo__text--muted' : ''), textContent: labelOf(opt) }));
                badges(opt).forEach(b => value.append(b));
            } else {
                value.append(Object.assign(document.createElement('span'), { className: 'combo__text combo__text--muted', textContent: 'Select…' }));
            }
            root.classList.toggle('is-loading', state.loading);
            trigger.disabled = select.disabled || state.loading;
            trigger.setAttribute('aria-busy', state.loading ? 'true' : 'false');
        }

        function badges(opt) {
            const out = [];
            if (opt.dataset.current) out.push(Object.assign(document.createElement('span'), { className: 'combo__tag combo__tag--current', textContent: 'Current' }));
            if (opt.dataset.me) out.push(Object.assign(document.createElement('span'), { className: 'combo__tag combo__tag--me', textContent: 'You' }));
            return out;
        }

        function build() {
            list.textContent = '';
            items = Array.from(select.options).map((opt, i) => {
                const li = document.createElement('li');
                li.className = 'combo__opt' + (opt.value === '' ? ' combo__opt--none' : '');
                li.id = id + '-o' + i;
                li.setAttribute('role', 'option');
                li.dataset.index = String(i);
                const main = document.createElement('span');
                main.className = 'combo__opt-main';
                const name = Object.assign(document.createElement('span'), { className: 'combo__opt-name', textContent: labelOf(opt) });
                main.append(name);
                badges(opt).forEach(b => main.append(b));
                li.append(main);
                if (opt.dataset.note) li.append(Object.assign(document.createElement('span'), { className: 'combo__opt-note', textContent: opt.dataset.note }));
                li.insertAdjacentHTML('beforeend', CHECK);
                li.addEventListener('mousedown', e => e.preventDefault());   // keep focus in the search box
                li.addEventListener('click', () => choose(i));
                li.addEventListener('mousemove', () => setActive(i, false));
                list.append(li);
                return { opt, li, text: (labelOf(opt) + ' ' + (opt.dataset.note || '')).toLowerCase() };
            });
            paintSelection();
            filter();
        }

        function paintSelection() {
            items.forEach(it => it.li.setAttribute('aria-selected', it.opt.index === select.selectedIndex ? 'true' : 'false'));
        }

        function visible() { return items.filter(it => !it.li.hidden); }

        function filter() {
            const q = input.value.trim().toLowerCase();
            items.forEach(it => { it.li.hidden = !!q && it.opt.value !== '' && !it.text.includes(q); });
            const shown = visible();
            // "No specific employee" never filters out, so "nothing matched" means no real option matched.
            const real = shown.filter(it => it.opt.value !== '').length;
            const realTotal = items.filter(it => it.opt.value !== '').length;
            if (q && real === 0) { empty.textContent = (select.dataset.comboEmpty || 'No matches.'); empty.hidden = false; }
            else if (!q && realTotal === 0 && state.note) { empty.textContent = state.note; empty.hidden = false; }
            else empty.hidden = true;
            const sel = shown.findIndex(it => it.opt.index === select.selectedIndex);
            setActive(items.indexOf(shown[sel >= 0 ? sel : 0]), true);
        }

        function setActive(index, scroll) {
            active = index;
            items.forEach((it, i) => it.li.classList.toggle('is-active', i === index));
            const it = items[index];
            if (it) { input.setAttribute('aria-activedescendant', it.li.id); if (scroll) it.li.scrollIntoView({ block: 'nearest' }); }
            else input.removeAttribute('aria-activedescendant');
        }

        function move(delta) {
            const shown = visible();
            if (!shown.length) return;
            const at = shown.findIndex(it => items.indexOf(it) === active);
            const next = shown[Math.max(0, Math.min(shown.length - 1, at < 0 ? 0 : at + delta))];
            setActive(items.indexOf(next), true);
        }

        function choose(index) {
            const it = items[index];
            if (!it) return;
            const changed = select.selectedIndex !== it.opt.index;
            select.selectedIndex = it.opt.index;
            paintSelection();
            paintTrigger();
            close(true);
            if (changed) select.dispatchEvent(new Event('change', { bubbles: true }));
        }

        function place() {
            const r = trigger.getBoundingClientRect();
            const vw = document.documentElement.clientWidth, vh = window.innerHeight;
            const gap = 6, margin = 8;
            const width = Math.min(r.width, vw - 2 * margin);
            const left = Math.max(margin, Math.min(r.left, vw - width - margin));
            const below = vh - r.bottom - gap - margin, above = r.top - gap - margin;
            const want = Math.min(pop.scrollHeight, 340);
            const up = below < Math.min(want, 220) && above > below;
            const room = Math.max(140, Math.min(340, up ? above : below));
            pop.style.left = left + 'px';
            pop.style.width = width + 'px';
            pop.style.maxHeight = room + 'px';
            pop.style.top = up ? '' : (r.bottom + gap) + 'px';
            pop.style.bottom = up ? (vh - r.top + gap) + 'px' : '';
            pop.classList.toggle('is-up', up);
        }

        function open() {
            if (trigger.disabled || isOpen()) return;
            document.querySelectorAll('.combo__pop:not([hidden])').forEach(p => p !== pop && p.dispatchEvent(new Event('combo:close')));
            input.value = '';
            filter();
            pop.hidden = false;
            trigger.setAttribute('aria-expanded', 'true');
            root.classList.add('is-open');
            place();
            input.focus({ preventScroll: true });
            const sel = items.findIndex(it => it.opt.index === select.selectedIndex);
            if (sel >= 0) setActive(sel, true);
        }

        function close(returnFocus) {
            if (!isOpen()) return;
            pop.hidden = true;
            trigger.setAttribute('aria-expanded', 'false');
            root.classList.remove('is-open');
            if (returnFocus) trigger.focus({ preventScroll: true });
        }

        trigger.addEventListener('click', () => (isOpen() ? close(true) : open()));
        trigger.addEventListener('keydown', e => {
            if (['ArrowDown', 'ArrowUp', 'Enter', ' '].includes(e.key)) { e.preventDefault(); open(); }
            else if (e.key === 'Escape' && isOpen()) { e.preventDefault(); e.stopPropagation(); close(true); }
        });
        input.addEventListener('input', filter);
        input.addEventListener('keydown', e => {
            if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
            else if (e.key === 'Home') { e.preventDefault(); const s = visible(); if (s.length) setActive(items.indexOf(s[0]), true); }
            else if (e.key === 'End') { e.preventDefault(); const s = visible(); if (s.length) setActive(items.indexOf(s[s.length - 1]), true); }
            else if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); if (active >= 0 && !items[active].li.hidden) choose(active); }
            else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(true); }   // closes the dropdown, not the whole modal
            else if (e.key === 'Tab') close(false);
        });
        pop.addEventListener('combo:close', () => close(false));
        document.addEventListener('mousedown', e => { if (isOpen() && !root.contains(e.target)) close(false); });
        window.addEventListener('resize', () => isOpen() && place());
        document.addEventListener('scroll', e => { if (isOpen() && !pop.contains(e.target)) place(); }, true);
        // The modal closing (Escape, Cancel, outside click) must not leave an orphaned popover behind.
        const details = select.closest('details');
        if (details) new MutationObserver(() => { if (!details.open) close(false); }).observe(details, { attributes: true, attributeFilter: ['open'] });

        const api = {
            /** Re-read the <select> (options, value, disabled) after the script changed it. */
            refresh() { build(); paintTrigger(); },
            /** Busy / informational state: loading disables the trigger and shows a spinner; note is shown in the list when it has no real options. */
            setState(next) { state = { ...state, ...next }; paintTrigger(); filter(); },
            focus() { trigger.focus(); }
        };
        select._combo = api;
        api.refresh();
        return api;
    }

    function init(form) {
        const dept = form.querySelector('[data-reassign-dept]');
        const emp = form.querySelector('[data-reassign-emp]');
        const hint = form.querySelector('[data-reassign-emp-hint]');
        const retry = form.querySelector('[data-reassign-retry]');
        const submit = form.querySelector('[data-reassign-submit]');
        if (!dept || !emp) return;
        const baseHint = hint ? hint.textContent : '';
        const currentDept = form.dataset.currentDepartment;
        const currentOwner = form.dataset.currentOwner || '';
        const me = form.dataset.me || '';
        const initialEmployees = Array.from(emp.options).filter(o => o.value);
        let token = 0;
        let loading = false;

        const deptCombo = dept.matches('[data-combo]') ? enhance(dept, 'Department') : null;
        const empCombo = emp.matches('[data-combo]') ? enhance(emp, 'Employee') : null;

        function say(text, kind) {
            if (!hint) return;
            hint.textContent = text;
            hint.classList.toggle('field-hint--error', kind === 'error' || kind === 'invalid');
            hint.classList.toggle('field-hint--notice', kind === 'notice');
            if (retry) retry.hidden = kind !== 'error';
        }

        function setLoading(on) {
            loading = on;
            emp.disabled = on;
            if (submit) submit.disabled = on;
            if (empCombo) empCombo.setState({ loading: on, loadingText: 'Loading employees…' });
        }

        function fill(items, selectId) {
            emp.textContent = '';
            const none = document.createElement('option');
            none.value = '';
            none.textContent = '— No specific employee (department only) —';
            none.dataset.label = 'No specific employee';
            none.dataset.note = 'Assign to the department only';
            emp.appendChild(none);
            items.forEach(m => {
                const o = document.createElement('option');
                o.value = m.id;
                o.dataset.label = m.name;
                if (m.id === me) o.dataset.me = 'true';
                if (m.id === currentOwner) o.dataset.current = 'true';
                o.textContent = m.name + (m.id === me ? ' (me)' : '') + (m.id === currentOwner ? ' — current' : '');
                emp.appendChild(o);
            });
            emp.value = selectId && items.some(m => m.id === selectId) ? selectId : '';
            if (empCombo) empCombo.refresh();
        }

        async function loadEmployees() {
            const mine = ++token;
            const deptId = dept.value;
            if (!deptId) return;
            say('Loading employees…');
            setLoading(true);
            try {
                const url = new URL(form.dataset.employeesUrl, window.location.origin);
                url.searchParams.set('departmentId', deptId);
                const response = await fetch(url, { headers: { Accept: 'application/json' }, credentials: 'same-origin' });
                const data = response.ok ? await response.json() : { ok: false, employees: [] };
                if (mine !== token) return;
                if (!data.ok) throw new Error('roster');
                const employees = data.employees || [];
                fill(employees, deptId === currentDept ? currentOwner : '');
                const none = !employees.length;
                if (empCombo) empCombo.setState({ note: none ? 'No active employees in this department.' : '' });
                say(none ? 'This department has no active employees. The ticket can still be assigned to the department only.' : baseHint, none ? 'notice' : '');
            } catch {
                if (mine !== token) return;
                // Never leave the previous department's people selectable under a different department.
                fill([], '');
                if (empCombo) empCombo.setState({ note: 'Employees could not be loaded.' });
                say('The employee list could not be loaded. You can retry, or still assign the department only.', 'error');
            } finally {
                if (mine === token) setLoading(false);
            }
        }

        dept.addEventListener('change', loadEmployees);
        if (retry) retry.addEventListener('click', loadEmployees);

        form.addEventListener('submit', event => {
            if (loading) { event.preventDefault(); return; }
            if (dept.value === currentDept && !emp.value) {
                event.preventDefault();
                say('The ticket is already in this department: choose an employee, or pick a different department.', 'invalid');
                if (empCombo) empCombo.focus(); else emp.focus();
            }
        });

        // A department with nobody active: say so (the department-only assignment still works).
        if (initialEmployees.length === 0) {
            if (empCombo) empCombo.setState({ note: 'No active employees in this department.' });
            say('This department has no active employees. The ticket can still be assigned to the department only.', 'notice');
        }
    }
})();
