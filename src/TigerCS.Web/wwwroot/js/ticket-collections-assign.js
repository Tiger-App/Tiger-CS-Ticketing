/* Ticket Details: Assign / Reassign beside "Assigned To" (Collections tickets). Posts the form with fetch, then updates "Assigned To", the Current line, the
   row version and the assignment-history table in place; the page is never left. Without script the form is an ordinary post that redirects back. */
(() => {
    document.addEventListener('DOMContentLoaded', () => {
        const root = document.querySelector('[data-collections-assign]');
        if (!root) return;
        const form = root.querySelector('form');
        const select = root.querySelector('[data-assign-select]');
        const search = root.querySelector('[data-assign-search]');
        const status = root.querySelector('[data-assign-status]');
        const say = (text, bad) => { status.textContent = text; status.classList.toggle('is-error', !!bad); };

        search.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
        search.addEventListener('input', () => {
            const term = search.value.trim().toLowerCase();
            Array.from(select.options).forEach(o => { o.hidden = term !== '' && !o.textContent.toLowerCase().includes(term); });
        });

        form.addEventListener('submit', async e => {
            e.preventDefault();
            if (!select.value) { say('Choose a team member.', true); return; }
            const button = form.querySelector('button[type=submit]');
            button.disabled = true;
            say('Saving…');
            try {
                const response = await fetch(root.dataset.postUrl, { method: 'POST', body: new FormData(form), headers: { 'X-Requested-With': 'fetch', 'Accept': 'application/json' }, credentials: 'same-origin' });
                const data = await response.json();
                if (!data.ok) { say(data.message || 'Could not save.', true); return; }
                const label = document.querySelector('.tw-owner .tw-fact__v');
                if (label) label.textContent = data.ownerName;
                root.querySelector('[data-assign-current]').textContent = data.ownerName;
                root.querySelector('summary').textContent = 'Reassign';
                form.querySelector('[data-assign-rowversion]').value = data.rowVersion;
                Array.from(select.options).forEach(o => {
                    const wasCurrent = o.disabled;
                    o.disabled = o.value === data.ownerId;
                    o.textContent = o.textContent.replace(' (current)', '') + (o.value === data.ownerId ? ' (current)' : '');
                    if (wasCurrent && o.value !== data.ownerId) o.selected = false;
                });
                select.selectedIndex = -1;
                form.querySelector('input[type=text]').value = '';
                renderHistory(data.history);
                say('Ticket assigned.');
                root.open = false;
            } catch {
                say('Could not reach the server. Try again.', true);
            } finally {
                button.disabled = false;
            }
        });

        function renderHistory(items) {
            const table = document.querySelector('[data-assignment-history]');
            const empty = document.querySelector('[data-assignment-history-empty]');
            if (!table || !items) return;
            const body = table.querySelector('tbody');
            body.textContent = '';
            items.slice().reverse().forEach(h => {
                const tr = document.createElement('tr');
                const when = new Date(h.changedAtUtc).toLocaleString('en-GB', { timeZone: 'Asia/Dubai', dateStyle: 'medium', timeStyle: 'short' });
                [when, h.type, h.previousEmployeeName || 'No one', h.newEmployeeName, h.changedByName,
                 [h.towerCode, h.unitNumber].filter(Boolean).join(' / ') || '—', h.reason || '—'].forEach(text => {
                    const td = document.createElement('td');
                    td.textContent = text;
                    tr.appendChild(td);
                });
                body.appendChild(tr);
            });
            table.hidden = items.length === 0;
            if (empty) empty.hidden = items.length > 0;
        }
    });
})();
