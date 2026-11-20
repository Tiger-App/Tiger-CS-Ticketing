// Assign / Reassign modal (ticket-reassign.js): searchable dropdowns over the native selects, employee loading per department, and the states around it.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { JSDOM } from 'jsdom';

const js = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'TigerCS.Web', 'wwwroot', 'js', 'ticket-reassign.js'), 'utf8');
const PAGE = `<!doctype html><body><form data-reassign data-employees-url="/emp" data-current-department="2" data-current-owner="o1" data-me="m1">
 <label for="D">Department</label>
 <select id="D" name="Reassign.TargetDepartmentId" data-reassign-dept data-combo data-combo-placeholder="Search departments…">
  <option value="2" selected data-label="Customer Service" data-current="true">Customer Service (current)</option><option value="7" data-label="Collections">Collections</option></select>
 <label for="E">Employee</label>
 <select id="E" name="Reassign.EmployeeId" data-reassign-emp data-combo data-combo-placeholder="Search employees…">
  <option value="" data-label="No specific employee">— No specific employee —</option><option value="o1" selected data-label="Olga" data-current="true">Olga — current</option></select>
 <span data-reassign-emp-hint>base hint</span><button type="button" data-reassign-retry hidden>Try again</button>
 <button type="submit" data-reassign-submit>Confirm Transfer</button></form></body>`;
const windows = [];
after(() => windows.forEach(w => w.close()));
const tick = () => new Promise(r => setTimeout(r, 10));

async function start(fetchImpl) {
    const dom = new JSDOM(PAGE, { runScripts: 'outside-only', url: 'http://localhost/Tickets/1' });
    const w = dom.window; windows.push(w);
    w.Element.prototype.scrollIntoView = () => {};
    w.fetch = fetchImpl;
    w.eval(js);
    await tick();                                  // jsdom fires DOMContentLoaded itself once the script is evaluated
    return { w, d: w.document, form: w.document.querySelector('form'), dept: w.document.getElementById('D'), emp: w.document.getElementById('E') };
}
const pick = (t, select, text) => {
    const combo = select.previousElementSibling;
    combo.querySelector('.combo__trigger').click();
    const input = combo.querySelector('.combo__input');
    input.value = text; input.dispatchEvent(new t.w.Event('input', { bubbles: true }));
    return Array.from(combo.querySelectorAll('.combo__opt')).find(li => !li.hidden && li.textContent.includes(text));
};
const ok = employees => async () => ({ ok: true, json: async () => ({ ok: true, employees }) });

test('each select is upgraded to a searchable dropdown; the native select stays in the form, hidden', async () => {
    const t = await start(ok([]));
    assert.equal(t.d.querySelectorAll('.combo').length, 2);
    assert.equal(t.dept.hidden, true);
    assert.equal(t.dept.name, 'Reassign.TargetDepartmentId');
    assert.match(t.d.querySelector('.combo__trigger').textContent, /Customer Service\s*Current/);
});

test('picking a department sets the native select, loads that department\'s employees and keeps "No specific employee"', async () => {
    let asked = null;
    const t = await start(async url => { asked = String(url); return { ok: true, json: async () => ({ ok: true, employees: [{ id: 'a', name: 'Aisha' }, { id: 'm1', name: 'Me Myself' }] }) }; });
    pick(t, t.dept, 'Collections').click();
    assert.equal(t.dept.value, '7');
    assert.equal(t.emp.disabled, true);                                    // loading: employee picker and Confirm are disabled
    assert.equal(t.d.querySelector('[data-reassign-submit]').disabled, true);
    await tick();
    assert.match(asked, /departmentId=7/);
    assert.deepEqual(Array.from(t.emp.options).map(o => o.value), ['', 'a', 'm1']);
    assert.equal(t.emp.value, '');                                         // the current owner is not in the new department
    assert.equal(t.emp.disabled, false);
    assert.equal(t.d.querySelector('[data-reassign-submit]').disabled, false);
    assert.ok(Array.from(t.emp.previousElementSibling.querySelectorAll('.combo__tag--me')).length >= 0);
});

test('searching filters the options and shows an empty message, but never removes "No specific employee"', async () => {
    const t = await start(ok([]));
    const combo = t.emp.previousElementSibling;
    pick(t, t.emp, 'zzz');
    const visible = Array.from(combo.querySelectorAll('.combo__opt')).filter(li => !li.hidden);
    assert.deepEqual(visible.map(li => li.textContent.includes('No specific employee')), [true]);
    assert.equal(combo.querySelector('.combo__empty').hidden, false);
});

test('a failed load clears the previous department\'s people, offers a retry, and Retry recovers', async () => {
    let fail = true;
    const t = await start(async () => fail ? { ok: false, json: async () => ({}) } : { ok: true, json: async () => ({ ok: true, employees: [{ id: 'r', name: 'Rita' }] }) });
    pick(t, t.dept, 'Collections').click();
    await tick();
    assert.deepEqual(Array.from(t.emp.options).map(o => o.value), ['']);   // no stale employees under another department
    const retry = t.d.querySelector('[data-reassign-retry]');
    assert.equal(retry.hidden, false);
    assert.match(t.d.querySelector('[data-reassign-emp-hint]').textContent, /could not be loaded/);
    fail = false; retry.click(); await tick();
    assert.equal(retry.hidden, true);
    assert.deepEqual(Array.from(t.emp.options).map(o => o.value), ['', 'r']);
});

test('an empty department says so and still allows a department-only assignment', async () => {
    const t = await start(ok([]));
    pick(t, t.dept, 'Collections').click();
    await tick();
    assert.match(t.d.querySelector('[data-reassign-emp-hint]').textContent, /no active employees/);
    assert.equal(t.d.querySelector('[data-reassign-submit]').disabled, false);
    const blocked = new t.w.Event('submit', { bubbles: true, cancelable: true });
    t.form.dispatchEvent(blocked);
    assert.equal(blocked.defaultPrevented, false);                          // another department, no employee: allowed
});

test('same department with no employee is blocked with a message (existing rule), without offering a retry', async () => {
    const t = await start(ok([]));
    t.emp.value = '';
    const e = new t.w.Event('submit', { bubbles: true, cancelable: true });
    t.form.dispatchEvent(e);
    assert.equal(e.defaultPrevented, true);
    assert.match(t.d.querySelector('[data-reassign-emp-hint]').textContent, /already in this department/);
    assert.equal(t.d.querySelector('[data-reassign-retry]').hidden, true);
});
