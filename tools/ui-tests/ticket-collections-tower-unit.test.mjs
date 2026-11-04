// Behaviour of ticket-collections-tower-unit.js against the Transfer form's markup: the Tower + Unit block shows only for Collections, loads the customer's verified
// towers once, lists the units of the chosen tower, preselects the ticket's own pair, and blocks the submit with a clear message unless both are chosen.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { JSDOM } from 'jsdom';

const script = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'TigerCS.Web', 'wwwroot', 'js', 'ticket-collections-tower-unit.js'), 'utf8');
const OPTIONS = {
    status: 'Ok', message: null, preselectedTowerId: 1, preselectedUnitId: 3,
    towers: [
        { towerId: 1, towerCode: '140', towerName: 'Tiger Tower 140', units: [{ unitId: 3, unitNumber: '1001', projectName: 'P79' }] },
        { towerId: 2, towerCode: '141', towerName: 'Tiger Tower 141', units: [{ unitId: 4, unitNumber: '502', projectName: 'P80' }, { unitId: 6, unitNumber: '503', projectName: 'P80' }] }
    ]
};
const PAGE = `<!doctype html><body><form id="f" method="post">
<select id="Transfer_TargetDepartmentId"><option value="3" selected>Facilities</option><option value="7">Collections</option></select>
<div class="ct" hidden data-ct data-ct-trigger="#Transfer_TargetDepartmentId" data-ct-collections-ids="7" data-ct-url="/load">
 <select name="Transfer.CollectionsTowerId" data-ct-tower disabled><option value="">Select a tower</option></select>
 <select name="Transfer.CollectionsUnitId" data-ct-unit disabled><option value="">Select a tower first</option></select>
 <p data-ct-hint></p><p data-ct-error hidden></p></div>
<button type="submit">Transfer</button></form></body>`;

const windows = [];
after(() => windows.forEach(w => w.close()));

async function load() {
    const dom = new JSDOM(PAGE, { runScripts: 'outside-only', url: 'http://localhost/Tickets/1' });
    windows.push(dom.window);
    let fetched = 0;
    dom.window.fetch = async () => { fetched++; return { json: async () => OPTIONS }; };
    dom.window.eval(script);
    const d = dom.window.document;
    const pick = (id, value) => { const s = d.getElementById(id); s.value = value; s.dispatchEvent(new dom.window.Event('change', { bubbles: true })); };
    const submit = () => { const e = new dom.window.Event('submit', { bubbles: true, cancelable: true }); d.getElementById('f').dispatchEvent(e); return e.defaultPrevented; };
    const settle = () => new Promise(r => setTimeout(r, 10));
    await settle();     // jsdom fires DOMContentLoaded itself, once, after the script is registered
    return { d, pick, submit, settle, fetched: () => fetched, win: dom.window };
}

test('hidden, disabled and not fetched for any department other than Collections', async () => {
    const t = await load();
    assert.equal(t.d.querySelector('[data-ct]').hidden, true);
    assert.equal(t.d.querySelector('[data-ct-tower]').disabled, true);
    assert.equal(t.submit(), false);          // another department: the form submits normally
    assert.equal(t.fetched(), 0);
});

test('choosing Collections shows the block, loads the options once and preselects the ticket\'s own tower and unit', async () => {
    const t = await load();
    t.pick('Transfer_TargetDepartmentId', '7'); await t.settle();
    const tower = t.d.querySelector('[data-ct-tower]'), unit = t.d.querySelector('[data-ct-unit]');
    assert.equal(t.d.querySelector('[data-ct]').hidden, false);
    assert.equal(tower.disabled, false);
    assert.equal(tower.value, '1');
    assert.equal(unit.value, '3');
    assert.deepEqual([...tower.options].map(o => o.text), ['Select a tower', '140 - Tiger Tower 140', '141 - Tiger Tower 141']);
    t.pick('Transfer_TargetDepartmentId', '3'); t.pick('Transfer_TargetDepartmentId', '7'); await t.settle();
    assert.equal(t.fetched(), 1);
});

test('the units offered are those of the chosen tower only; changing the tower clears the unit', async () => {
    const t = await load();
    t.pick('Transfer_TargetDepartmentId', '7'); await t.settle();
    const unit = t.d.querySelector('[data-ct-unit]');
    const tower = t.d.querySelector('[data-ct-tower]');
    tower.value = '2'; tower.dispatchEvent(new t.win.Event('change', { bubbles: true }));
    assert.deepEqual([...unit.options].map(o => o.text), ['Select a unit', 'Unit 502 - P80', 'Unit 503 - P80']);
    assert.equal(unit.value, '');
});

test('submitting without both a tower and a unit is blocked with a clear message; with both it goes through', async () => {
    const t = await load();
    t.pick('Transfer_TargetDepartmentId', '7'); await t.settle();
    const tower = t.d.querySelector('[data-ct-tower]'), unit = t.d.querySelector('[data-ct-unit]'), error = t.d.querySelector('[data-ct-error]');
    tower.value = '2'; tower.dispatchEvent(new t.win.Event('change', { bubbles: true }));     // tower chosen, unit not
    assert.equal(t.submit(), true);
    assert.equal(error.hidden, false);
    assert.equal(error.textContent, 'Select both a Tower and a Unit before transferring to Collections.');
    unit.value = '4'; unit.dispatchEvent(new t.win.Event('change', { bubbles: true }));
    assert.equal(error.hidden, true);
    assert.equal(t.submit(), false);
});
