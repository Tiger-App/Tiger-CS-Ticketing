// Reassign dialog (ticket-collections-assign.js): the chosen employee must be in the posted payload, and the Reassign button must work with the global submit guard.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { JSDOM } from 'jsdom';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'TigerCS.Web', 'wwwroot', 'js');
const assign = readFileSync(join(root, 'ticket-collections-assign.js'), 'utf8');
const site = readFileSync(join(root, 'site.js'), 'utf8');
const C8 = '11111111-1111-1111-1111-111111111111', C2 = '22222222-2222-2222-2222-222222222222';
const PAGE = `<!doctype html><body><div class="tw-owner"><span class="tw-fact__v">Collections 8</span></div>
<div data-collections-assign data-post-url="/post">
 <button type="button" data-assign-open><span data-assign-open-label>Reassign</span></button>
 <dialog data-assign-dialog><form method="post">
  <input type="hidden" name="CollectionsAssign.RowVersionBase64" value="AAA=" data-assign-rowversion />
  <input type="search" data-assign-search /><div data-assign-list>
   <label class="reassign-option is-current"><input type="radio" name="CollectionsAssign.EmployeeId" value="${C8}" disabled data-assign-option /><span class="reassign-option__name">Collections 8</span></label>
   <label class="reassign-option"><input type="radio" name="CollectionsAssign.EmployeeId" value="${C2}" data-assign-option /><span class="reassign-option__name">Collections 2</span></label>
   <p data-assign-nomatch hidden></p></div>
  <p data-assign-selected></p><span data-assign-title>Reassign ticket</span>
  <textarea name="CollectionsAssign.Reason"></textarea><p data-assign-status></p>
  <button type="button" data-assign-cancel>Cancel</button>
  <button type="submit" data-assign-submit disabled><span data-assign-submit-label>Reassign</span></button>
 </form></dialog></div></body>`;
const windows = [];
after(() => windows.forEach(w => w.close()));

async function start({ withSiteGuard }) {
    const dom = new JSDOM(PAGE, { runScripts: 'outside-only', url: 'http://localhost/Tickets/4' });
    const w = dom.window;
    windows.push(w);
    w.HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', ''); };
    w.HTMLDialogElement.prototype.close = function () { this.removeAttribute('open'); };
    const posted = [];
    w.fetch = async (url, init) => {
        posted.push(Object.fromEntries(init.body.entries()));
        return { json: async () => ({ ok: true, ownerId: C2, ownerName: 'Collections 2', rowVersion: 'BBB=', history: null }) };
    };
    if (withSiteGuard) w.eval(site);
    w.eval(assign);
    await new Promise(r => setTimeout(r, 5));
    return { w, d: w.document, posted };
}

for (const withSiteGuard of [false, true]) {
    test(`Collections 8 -> Collections 2 posts the selected employee (site.js guard loaded: ${withSiteGuard})`, async () => {
        const t = await start({ withSiteGuard });
        t.d.querySelector('[data-assign-open]').click();
        const two = t.d.querySelector(`input[value="${C2}"]`);
        two.checked = true; two.dispatchEvent(new t.w.Event('change', { bubbles: true }));
        const submit = t.d.querySelector('[data-assign-submit]');
        assert.equal(submit.disabled, false);                          // Reassign is enabled once a valid employee is selected
        t.d.querySelector('form').dispatchEvent(new t.w.Event('submit', { bubbles: true, cancelable: true }));
        await new Promise(r => setTimeout(r, 20));
        assert.equal(t.posted.length, 1);
        assert.equal(t.posted[0]['CollectionsAssign.EmployeeId'], C2);
        assert.equal(t.posted[0]['CollectionsAssign.RowVersionBase64'], 'AAA=');
        assert.equal(t.d.querySelector('.tw-owner .tw-fact__v').textContent, 'Collections 2');
    });
}

test('the current assignee cannot be chosen, and nothing is posted without a selection', async () => {
    const t = await start({ withSiteGuard: true });
    t.d.querySelector('[data-assign-open]').click();
    assert.equal(t.d.querySelector(`input[value="${C8}"]`).disabled, true);
    assert.equal(t.d.querySelector('[data-assign-submit]').disabled, true);
    t.d.querySelector('form').dispatchEvent(new t.w.Event('submit', { bubbles: true, cancelable: true }));
    await new Promise(r => setTimeout(r, 10));
    assert.equal(t.posted.length, 0);
});
