/* Ticket Details: Assign / Reassign beside "Assigned To" (Collections tickets). A button opens a centred modal (<dialog>); the choice is posted with fetch to the
   unchanged CollectionsAssign handler. The modal closes only once the server confirms; then "Assigned To", the Current line, the row version and the
   assignment-history table are updated in place and the page is never left. */
(() => {
    // Run now when the document is already parsed (the script can be injected late or deferred); otherwise wait for it.
    const onReady = fn => (document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', fn) : fn());
    onReady(() => {
        const root = document.querySelector('[data-collections-assign]');
        if (!root) return;
        const dialog = root.querySelector('[data-assign-dialog]');
        const openButton = root.querySelector('[data-assign-open]');
        const form = dialog.querySelector('form');
        const search = dialog.querySelector('[data-assign-search]');
        const list = dialog.querySelector('[data-assign-list]');
        const noMatch = dialog.querySelector('[data-assign-nomatch]');
        const selectedText = dialog.querySelector('[data-assign-selected]');
        const status = dialog.querySelector('[data-assign-status]');
        const submit = dialog.querySelector('[data-assign-submit]');
        const reason = form.querySelector('textarea');
        const options = () => Array.from(list.querySelectorAll('.reassign-option'));
        const checked = () => list.querySelector('input[data-assign-option]:checked');
        let busy = false;
        // The employee who owns the ticket now (empty when nobody does). The submit button is enabled only for a chosen person who is
        // someone else: the decision is made from the radio's own value, never from a disabled state or a label.
        let currentId = (dialog.dataset.currentId || '').toLowerCase();
        const sameAsCurrent = value => currentId !== '' && (value || '').toLowerCase() === currentId;

        const say = (text, bad) => { status.textContent = text; status.classList.toggle('is-error', !!bad); };

        function refresh() {
            const pick = checked();
            const valid = !!pick && !!pick.value && !sameAsCurrent(pick.value);
            submit.disabled = busy || !valid;
            selectedText.textContent = '';
            if (pick) {
                const name = pick.closest('.reassign-option').querySelector('.reassign-option__name').textContent;
                selectedText.append('Selected: ');
                const strong = document.createElement('strong');
                strong.textContent = name;
                selectedText.append(strong);
            } else {
                selectedText.textContent = 'No one selected';
            }
        }

        function setBusy(on) {
            busy = on;
            submit.classList.toggle('is-loading', on);
            dialog.querySelectorAll('[data-assign-cancel], [data-assign-search], textarea').forEach(el => { el.disabled = on; });
            list.querySelectorAll('input').forEach(el => { if (on) el.disabled = true; else el.disabled = el.closest('.reassign-option').classList.contains('is-current'); });
            dialog.querySelector('[data-assign-submit-label]').textContent = on ? 'Reassigning…' : submit.dataset.label;
            refresh();
        }

        function reset() {
            list.querySelectorAll('input').forEach(el => { el.checked = false; });
            search.value = '';
            reason.value = '';
            options().forEach(o => { o.hidden = false; });
            noMatch.hidden = true;
            say('');
            refresh();
        }

        submit.dataset.label = dialog.querySelector('[data-assign-submit-label]').textContent;

        openButton.addEventListener('click', () => { reset(); dialog.showModal(); search.focus(); });
        dialog.querySelectorAll('[data-assign-cancel]').forEach(b => b.addEventListener('click', () => { if (!busy) dialog.close(); }));
        // Escape and a click on the backdrop are cancels too, but never while a save is in flight.
        dialog.addEventListener('cancel', e => { if (busy) e.preventDefault(); });
        dialog.addEventListener('click', e => { if (e.target === dialog && !busy) dialog.close(); });
        dialog.addEventListener('close', () => openButton.focus({ preventScroll: true }));

        search.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
        search.addEventListener('input', () => {
            const term = search.value.trim().toLowerCase();
            let shown = 0;
            options().forEach(o => {
                const match = term === '' || o.textContent.toLowerCase().includes(term);
                o.hidden = !match;
                if (match) shown++;
            });
            noMatch.hidden = shown > 0;
        });
        // 'change' is the normal signal; 'input' and 'click' cover browsers/assistive tech that update the radio without a bubbling change.
        ['change', 'input', 'click'].forEach(type => list.addEventListener(type, () => { say(''); refresh(); }));

        form.addEventListener('submit', async e => {
            e.preventDefault();
            if (busy) return;
            const pick = checked();
            if (!pick || !pick.value) { say('Choose a team member.', true); return; }
            if (sameAsCurrent(pick.value)) { say('The ticket is already assigned to that person. Choose someone else.', true); return; }
            // Build the payload BEFORE the form is put into its busy state: setBusy disables every radio, and a disabled control is left out of FormData -
            // the server would then receive no employee at all and answer "Choose who to assign the ticket to." for a selection that was made.
            const body = new FormData(form);
            body.set('CollectionsAssign.EmployeeId', pick.value);
            setBusy(true);
            say('');
            try {
                const response = await fetch(root.dataset.postUrl, { method: 'POST', body, headers: { 'X-Requested-With': 'fetch', 'Accept': 'application/json' }, credentials: 'same-origin' });
                let data;
                try { data = await response.json(); } catch { data = null; }
                if (!data) { say(response.ok ? 'The server sent an unexpected answer. Reload the page.' : `The assignment was refused (${response.status}). Reload the page and try again.`, true); return; }
                if (!data.ok) { say(data.message || 'Could not save.', true); return; }
                const label = document.querySelector('.tw-owner .tw-fact__v');
                if (label) label.textContent = data.ownerName;
                dialog.querySelector('[data-assign-current]').textContent = data.ownerName;
                form.querySelector('[data-assign-rowversion]').value = data.rowVersion;
                currentId = (data.ownerId || '').toLowerCase();
                dialog.dataset.currentId = currentId;
                openButton.querySelector('[data-assign-open-label]').textContent = 'Reassign';
                dialog.querySelector('[data-assign-title]').textContent = 'Reassign ticket';
                submit.dataset.label = 'Reassign';
                options().forEach(o => {
                    const input = o.querySelector('input');
                    const isCurrent = sameAsCurrent(input.value);
                    o.classList.toggle('is-current', isCurrent);
                    const tag = o.querySelector('.reassign-option__tag');
                    if (isCurrent && !tag) {
                        const t = document.createElement('span');
                        t.className = 'reassign-option__tag';
                        t.textContent = 'Current';
                        o.append(t);
                    } else if (!isCurrent && tag) {
                        tag.remove();
                    }
                });
                renderHistory(data.history);
                setBusy(false); // the dialog closes only now that the server has confirmed
                dialog.close();
            } catch {
                say('Could not reach the server. Try again.', true);
            } finally {
                if (busy) setBusy(false);
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
