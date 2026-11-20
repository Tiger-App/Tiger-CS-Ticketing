/* Ticket Details -> Actions -> Assign / Reassign.
   Two searchable lists (Department, Employee) over plain <select size> controls, so the form still posts without script. Typing filters the options.
   Picking a department loads that department's ACTIVE employees (the server's roster, via the page's ReassignEmployees handler); the Employee list always keeps a
   "No specific employee" entry, which means a department-only assignment. */
(() => {
    document.addEventListener('DOMContentLoaded', () => document.querySelectorAll('[data-reassign]').forEach(init));

    function init(form) {
        const dept = form.querySelector('[data-reassign-dept]');
        const emp = form.querySelector('[data-reassign-emp]');
        const hint = form.querySelector('[data-reassign-emp-hint]');
        if (!dept || !emp) return;
        const baseHint = hint ? hint.textContent : '';
        const currentDept = form.dataset.currentDepartment;
        const currentOwner = form.dataset.currentOwner || '';
        const me = form.dataset.me || '';
        const initialEmployees = Array.from(emp.options).filter(o => o.value);
        let token = 0;

        form.querySelectorAll('input[data-search-list]').forEach(input => {
            const list = input.nextElementSibling;
            if (!list) return;
            input.addEventListener('input', () => filter(list, input.value));
            input.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
        });

        function filter(list, text) {
            const q = text.trim().toLowerCase();
            Array.from(list.options).forEach(o => { o.hidden = !!q && !!o.value && !o.textContent.toLowerCase().includes(q); });
        }

        function fill(items, selectId) {
            emp.textContent = '';
            const none = document.createElement('option');
            none.value = '';
            none.textContent = '— No specific employee (department only) —';
            emp.appendChild(none);
            items.forEach(m => {
                const o = document.createElement('option');
                o.value = m.id;
                o.textContent = m.name + (m.id === me ? ' (me)' : '') + (m.id === currentOwner ? ' — current' : '');
                emp.appendChild(o);
            });
            emp.value = selectId && items.some(m => m.id === selectId) ? selectId : '';
            const search = form.querySelector('#reassign-emp-search');
            if (search) { search.value = ''; }
        }

        dept.addEventListener('change', async () => {
            const mine = ++token;
            const deptId = dept.value;
            if (!deptId) return;
            if (hint) hint.textContent = 'Loading employees…';
            emp.disabled = true;
            try {
                const url = new URL(form.dataset.employeesUrl, window.location.origin);
                url.searchParams.set('departmentId', deptId);
                const response = await fetch(url, { headers: { Accept: 'application/json' }, credentials: 'same-origin' });
                const data = response.ok ? await response.json() : { ok: false, employees: [] };
                if (mine !== token) return;
                fill(data.employees || [], deptId === currentDept ? currentOwner : '');
                if (hint) hint.textContent = data.ok
                    ? (data.employees.length ? baseHint : 'This department has no active employees. The ticket can still be assigned to the department only.')
                    : 'The employee list could not be loaded. You can still assign the department only.';
            } catch {
                if (mine === token && hint) hint.textContent = 'The employee list could not be loaded. You can still assign the department only.';
            } finally {
                if (mine === token) emp.disabled = false;
            }
        });

        form.addEventListener('submit', event => {
            if (dept.value === currentDept && !emp.value) {
                event.preventDefault();
                if (hint) hint.textContent = 'The ticket is already in this department: choose an employee, or pick a different department.';
                emp.focus();
            }
        });

        // A department with nobody active: say so (the department-only assignment still works).
        if (initialEmployees.length === 0 && hint) hint.textContent = 'This department has no active employees. The ticket can still be assigned to the department only.';
    }
})();
