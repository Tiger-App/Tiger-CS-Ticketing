/* Ticket Details: the Tower + Unit of a move into Collections (Transfer and Reopen forms).
   Shown only when the department select holds a Collections department. The customer's verified towers/units are fetched once, on first need. Tower first, then the units of
   that tower; the ticket's own verified pair is preselected. Reopen: submitting without both is blocked here with a clear message. Transfer (data-ct-optional): both may stay
   empty and the ticket goes to the Collections queue unassigned; only a half selection is blocked. The Api enforces the same rules. */
(() => {
    const MISSING = 'Select both a Tower and a Unit before transferring to Collections.';
    const HALF = 'Select both a Tower and a Unit, or leave both empty to transfer the ticket to the Collections queue.';
    const NO_TOWER = 'No verified tower was found for this customer. The ticket will be transferred to the Collections queue without an assignee, and can be assigned there.';
    document.addEventListener('DOMContentLoaded', () => document.querySelectorAll('[data-ct]').forEach(init));

    function init(root) {
        const trigger = document.querySelector(root.dataset.ctTrigger);
        const ids = (root.dataset.ctCollectionsIds || '').split(',').filter(Boolean);
        const tower = root.querySelector('[data-ct-tower]');
        const unit = root.querySelector('[data-ct-unit]');
        const hint = root.querySelector('[data-ct-hint]');
        const error = root.querySelector('[data-ct-error]');
        const form = root.closest('form');
        const optional = root.hasAttribute('data-ct-optional');
        if (!trigger || !form || ids.length === 0) return;
        let data = null, loading = null;

        const active = () => ids.includes(trigger.value);
        const say = text => { error.textContent = text || ''; error.hidden = !text; };

        function fillUnits() {
            const chosen = data && data.towers.find(t => String(t.towerId) === tower.value);
            unit.textContent = '';
            const first = document.createElement('option');
            first.value = '';
            first.textContent = chosen ? 'Select a unit' : 'Select a tower first';
            unit.appendChild(first);
            (chosen ? chosen.units : []).forEach(u => {
                const o = document.createElement('option');
                o.value = u.unitRef;
                o.textContent = 'Unit ' + u.unitNumber + (u.projectName ? ' - ' + u.projectName : '');
                unit.appendChild(o);
            });
            unit.disabled = !chosen;
        }

        function fillTowers() {
            tower.textContent = '';
            const first = document.createElement('option');
            first.value = '';
            first.textContent = optional ? 'No tower (Collections queue)' : 'Select a tower';
            tower.appendChild(first);
            data.towers.forEach(t => {
                const o = document.createElement('option');
                o.value = t.towerId;
                o.textContent = t.towerName && t.towerName !== t.towerCode ? t.towerCode + ' - ' + t.towerName : t.towerCode;
                tower.appendChild(o);
            });
            if (data.preselectedTowerId) {
                tower.value = String(data.preselectedTowerId);
                fillUnits();
                if (data.preselectedUnitRef) unit.value = data.preselectedUnitRef;
            } else {
                fillUnits();
            }
        }

        async function load() {
            if (data) return;
            loading = loading || fetch(root.dataset.ctUrl, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' })
                .then(r => r.json()).then(d => { data = d; }).catch(() => { data = { towers: [], status: 'Error', message: 'The customer\'s units could not be loaded. Reload the page and try again.' }; });
            await loading;
            fillTowers();
            hint.textContent = optional && data.towers.length === 0
                ? (data.status === 'SourceUnavailable' && data.message ? data.message + ' ' : '') + NO_TOWER
                : data.message || (data.towers.length ? 'Choose the tower first, then the unit this request is about.' + (optional ? ' Leave both empty to transfer to the Collections queue.' : '') : '');
        }

        function sync() {
            const on = active();
            root.hidden = !on;
            tower.disabled = !on;
            unit.disabled = !on || !tower.value;
            say('');
            if (on) load();
        }

        tower.addEventListener('change', () => { fillUnits(); say(''); });
        unit.addEventListener('change', () => say(''));
        trigger.addEventListener('change', sync);
        form.addEventListener('submit', e => {
            if (!active()) return;
            if (optional) {
                if (tower.value && !unit.value) { e.preventDefault(); say(HALF); unit.focus(); }
                return;
            }
            if (!tower.value || !unit.value) { e.preventDefault(); say(MISSING); (tower.value ? unit : tower).focus(); }
        });
        sync();
    }
})();
