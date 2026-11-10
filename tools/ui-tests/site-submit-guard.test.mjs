// site.js's global double-submit guard must not lock a form whose own handler already cancelled the submit (a validation message): the Transfer
// button used to stay disabled for good after one blocked attempt, so the corrected form could never be sent.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { JSDOM } from 'jsdom';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'TigerCS.Web', 'wwwroot', 'js');
const site = readFileSync(join(root, 'site.js'), 'utf8');
const windows = [];
after(() => windows.forEach(w => w.close()));

function page() {
    const dom = new JSDOM('<!doctype html><body><form id="f"><button type="submit" id="b">Transfer</button></form></body>', { runScripts: 'outside-only', url: 'http://localhost/' });
    windows.push(dom.window);
    dom.window.eval(site);
    const d = dom.window.document, form = d.getElementById('f');
    const submit = () => { const e = new dom.window.Event('submit', { bubbles: true, cancelable: true }); form.dispatchEvent(e); return e; };
    const tick = () => new Promise(r => setTimeout(r, 5));
    return { d, form, submit, tick, win: dom.window };
}

test('a submit cancelled by the form\'s own handler leaves the form and its button usable', async () => {
    const t = page();
    t.form.addEventListener('submit', e => e.preventDefault());       // the form's validation, which runs before the document-level guard
    t.submit(); await t.tick();
    assert.equal(t.form.hasAttribute('data-submitting'), false);
    assert.equal(t.d.getElementById('b').disabled, false);
});

test('after a blocked attempt the corrected form can be submitted, and only once', async () => {
    const t = page();
    let valid = false;
    t.form.addEventListener('submit', e => { if (!valid) e.preventDefault(); });
    t.submit(); await t.tick();
    valid = true;
    const second = t.submit(); await t.tick();
    assert.equal(second.defaultPrevented, false);                      // goes through
    assert.equal(t.form.hasAttribute('data-submitting'), true);        // and is now guarded
    assert.equal(t.submit().defaultPrevented, true);                   // a double click is still swallowed
});
