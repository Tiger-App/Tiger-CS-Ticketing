// Behaviour of the real Campaigns scripts (receivables.js, collections-campaign-filters.js, collections-campaign-approve.js) against the page's markup:
// filter first, Preview Customers loads, any filter change clears the customers and ticks, Approve & Send is gated, the snapshot status survives.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { JSDOM } from 'jsdom';

const js = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'TigerCS.Web', 'wwwroot', 'js');
const read = name => readFileSync(join(js, name), 'utf8');

const PAGE = `<!doctype html><html><body>
<div class="receivables-workspace">
<form id="campaignFilters" method="get" action="/Collections/Campaigns" data-receivables-filter data-results-form data-campaign-filters>
  <select id="campaignStage" name="stage" data-campaign-stage><option value="OverdueReminder" selected>Overdue</option><option value="LegalNotice">Legal</option></select>
  <select id="campaignTower" name="towerId" data-tower-select><option value="" selected>All</option><option value="7">T7</option></select>
  <select id="campaignPayment" name="paymentStatus" data-payment-status><option value="Both" selected>Both</option><option value="Overdue">Overdue</option></select>
  <select id="campaignPeriod" data-period data-today="2026-10-21"><option value="UpToToday" selected>Up to today</option><option value="ThisMonth">This month</option><option value="LastSixMonths">Last 6 months</option><option value="Custom">Custom</option></select>
  <div role="radiogroup" data-sending-mode><label><input type="radio" name="sendingMode" value="Manual" id="modeManual" />Manual</label><label><input type="radio" name="sendingMode" value="Auto" id="modeAuto" checked />Auto</label></div>
  <div class="campaign-date-slot" data-date-slot><span data-mode-hint="Auto" data-policy-schedule>Schedule: 01 Nov 2026</span></div>
  <details data-advanced-filters><div class="campaign-advanced__grid">
    <div class="field-group" data-date-group><label for="campaignDate" data-date-label>Preview date</label>
    <input id="campaignDate" name="businessDate" type="date" data-campaign-date value="" /></div>
    <input id="campaignFrom" name="dateFrom" type="date" data-date-from value="" />
    <input id="campaignTo" name="dateTo" type="date" data-date-to value="2026-10-21" />
    <input id="campaignMin" name="minTotal" type="number" value="100" />
  </div></details>
  <span class="receivables-loading" hidden></span><span class="receivables-timeout" hidden></span>
  <button type="submit" name="render" value="full" data-preview-button>Preview Customers</button>
</form>
<section data-snapshot-box><p data-snapshot-idle>idle</p></section>
<div data-kpi-box></div>
<input form="campaignFilters" type="search" id="campaignSearch" name="search" value="" />
<div data-results data-results-url="/Collections/Campaigns"><div data-results-idle>No customers loaded yet.</div></div>
</div></body></html>`;

const RESULTS = (blocked = false) => `
<div data-snapshot-status><div data-load-problem>Could not load data for Tiger Group Sharjah</div><p>Financial source: <strong>Reconciled</strong></p></div>
<div class="panel" data-customer-results data-preview-current>
  <div data-campaign-kpis><ul><li data-ready-count>2</li></ul></div>
  <form data-approve-form>
    <input type="hidden" name="unitKeys" value="" data-selected-units />
    <label><input type="radio" name="selectionMode" value="SelectedUnits" checked /> ticked (<span data-selected-count>0</span>)</label>
    <label><input type="radio" name="selectionMode" value="AllMatching" data-all-matching /> all (<span data-all-count>2</span>)</label>
    <button type="submit" data-approve-button data-blocked="${blocked}" ${blocked ? 'disabled' : ''}>Approve &amp; Send</button>
    <button type="button" data-clear-selection>Clear</button>
    <span data-selected-summary>0 selected</span>
    <span data-approve-reason>Only Ready customers can be sent.</span>
  </form>
  <table><thead><tr><th><input type="checkbox" data-select-page /></th></tr></thead><tbody>
    <tr><td><input type="checkbox" class="campaign-select" data-unit-key="a" /></td></tr>
    <tr><td><input type="checkbox" class="campaign-select" data-unit-key="b" /></td></tr>
    <tr><td><input type="checkbox" class="row-select-disabled" disabled title="Blocked: cannot be selected" /></td></tr>
  </tbody></table>
</div>`;

const windows = [];
after(() => windows.forEach(w => w.close()));

async function boot({ blocked = false } = {}) {
    const dom = new JSDOM(PAGE, { url: 'https://tiger.test/Collections/Campaigns', runScripts: 'outside-only', pretendToBeVisual: true });
    const { window } = dom; const { document } = window; windows.push(window);
    const fetches = [];
    window.fetch = async url => { fetches.push(String(url)); return { ok: true, status: 200, redirected: false, url: String(url), text: async () => RESULTS(blocked) }; };
    for (const f of ['receivables-load-monitor.js', 'receivables.js', 'collections-campaign-filters.js', 'collections-campaign-approve.js']) window.eval(read(f));
    document.dispatchEvent(new window.Event('DOMContentLoaded'));
    const tick = () => new Promise(r => setTimeout(r, 10));
    const preview = async () => { document.querySelector('#campaignFilters').requestSubmit(); await tick(); await tick(); };
    const change = (el, value) => { el.value = value; el.dispatchEvent(new window.Event('change', { bubbles: true })); };
    return { window, document, fetches, tick, preview, change, q: s => document.querySelector(s) };
}

test('initial load shows the filters only and queries nothing', async () => {
    const { fetches, q } = await boot();
    assert.equal(fetches.length, 0);
    assert.ok(q('[data-results-idle]'));
    assert.equal(q('[data-approve-form]'), null);
});

test('Preview Customers loads once with the filters, lifts the snapshot status, and gates Approve & Send on ticks', async () => {
    const { fetches, q, document, window, preview, tick } = await boot();
    q('#campaignSearch').value = '3001';
    await preview();
    assert.equal(fetches.length, 1);
    assert.match(fetches[0], /handler=Results/); assert.match(fetches[0], /stage=OverdueReminder/); assert.match(fetches[0], /search=3001/); assert.match(fetches[0], /dateTo=2026-10-21/);
    assert.doesNotMatch(fetches[0], /render=/);
    assert.ok(q('[data-snapshot-box] [data-load-problem]'), 'snapshot status is lifted into its own box');
    assert.equal(q('[data-results] [data-snapshot-status]'), null);
    assert.equal(q('[data-snapshot-idle]'), null);
    const button = q('[data-approve-button]');
    assert.equal(button.disabled, true, 'nothing ticked');
    assert.match(q('[data-approve-reason]').textContent, /Tick at least one/);
    const boxes = [...document.querySelectorAll('.campaign-select')];
    boxes[0].checked = true; boxes[0].dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.equal(button.disabled, false); assert.equal(q('[data-selected-count]').textContent, '1');
    boxes[0].checked = false; boxes[0].dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.equal(button.disabled, true);
    const all = q('[data-all-matching]'); all.checked = true; all.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.equal(button.disabled, false, 'all eligible customers matching the filters');
    await tick();
});

test('a reconciliation block keeps Approve & Send disabled whatever is ticked', async () => {
    const { q, document, window, preview } = await boot({ blocked: true });
    await preview();
    const box = document.querySelector('.campaign-select'); box.checked = true; box.dispatchEvent(new window.Event('change', { bubbles: true }));
    const all = q('[data-all-matching]'); all.checked = true; all.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.equal(q('[data-approve-button]').disabled, true);
});

for (const [name, mutate] of [
    ['stage', (c, q) => c.change(q('#campaignStage'), 'LegalNotice')],
    ['tower', (c, q) => c.change(q('#campaignTower'), '7')],
    ['payment status', (c, q) => c.change(q('#campaignPayment'), 'Overdue')],
    ['period', (c, q) => c.change(q('#campaignPeriod'), 'ThisMonth')],
    ['minimum total', (c, q) => c.change(q('#campaignMin'), '250')],
    ['search', (c, q) => { q('#campaignSearch').value = 'x'; q('#campaignSearch').dispatchEvent(new c.window.Event('input', { bubbles: true })); }],
]) {
    test(`changing ${name} clears the customers and ticks, keeps the snapshot status, and does not reload`, async () => {
        const ctx = await boot();
        await ctx.preview();
        const first = ctx.document.querySelector('.campaign-select'); first.checked = true; first.dispatchEvent(new ctx.window.Event('change', { bubbles: true }));
        assert.equal(ctx.window.sessionStorage.getItem('collections-campaign-selection'), '["a"]');
        mutate(ctx, ctx.q);
        assert.equal(ctx.q('[data-approve-form]'), null, 'no Approve & Send without a current preview');
        assert.equal(ctx.q('.campaign-select'), null);
        assert.ok(ctx.q('[data-results-stale]'), 'asks for a new preview');
        assert.match(ctx.q('[data-results]').textContent, /Click Preview Customers/);
        assert.ok(ctx.q('[data-snapshot-box] [data-load-problem]'), 'a refresh problem is never hidden by a filter change');
        assert.deepEqual(JSON.parse(ctx.window.sessionStorage.getItem('collections-campaign-selection') || '[]'), [], 'the ticks are gone');
        assert.equal(ctx.fetches.length, 1, 'only the Preview Customers click loaded customers');
        await ctx.preview();
        assert.equal(ctx.fetches.length, 2);
        assert.equal(ctx.q('[data-selected-count]').textContent, '0', 'a new preview starts with no ticks');
    });
}

test('Period fills the visible dates from the preview date and falls back to Custom range when a date is edited', async () => {
    const { q, change, window } = await boot();
    const from = q('#campaignFrom'), to = q('#campaignTo'), period = q('#campaignPeriod');
    change(q('#campaignDate'), '2026-10-14');
    assert.deepEqual([from.value, to.value], ['', '2026-10-14'], 'Up to today follows the preview date');
    change(period, 'ThisMonth'); assert.deepEqual([from.value, to.value], ['2026-10-01', '2026-10-31']);
    change(period, 'LastSixMonths'); assert.deepEqual([from.value, to.value], ['2026-04-14', '2026-10-14']);
    change(q('#campaignDate'), '2026-08-31'); assert.deepEqual([from.value, to.value], ['2026-02-28', '2026-08-31'], 'month ends clamp like the server');
    change(period, 'UpToToday'); assert.deepEqual([from.value, to.value], ['', '2026-08-31']);
    from.value = '2026-03-02'; from.dispatchEvent(new window.Event('input', { bubbles: true }));
    assert.equal(period.value, 'Custom');
    change(period, 'ThisMonth'); change(period, 'Custom');
    assert.equal(q('[data-advanced-filters]').open, true, 'Custom range opens the date fields');
});

test('typing in a filter before any preview does not show the stale notice', async () => {
    const { q, change } = await boot();
    change(q('#campaignStage'), 'LegalNotice');
    assert.ok(q('[data-results-idle]')); assert.equal(q('[data-results-stale]'), null);
});

test('filters edited while customers are loading do not leave the old customers on screen', async () => {
    const ctx = await boot();
    let release; ctx.window.fetch = () => new Promise(res => { release = () => res({ ok: true, status: 200, redirected: false, url: '', text: async () => RESULTS() }); });
    ctx.document.querySelector('#campaignFilters').requestSubmit(); await ctx.tick();
    ctx.change(ctx.q('#campaignStage'), 'LegalNotice');
    release(); await ctx.tick(); await ctx.tick();
    assert.equal(ctx.q('.campaign-select'), null); assert.ok(ctx.q('[data-results-stale]'));
});

const pick = (ctx, mode) => { const r = ctx.q(mode === 'Manual' ? '#modeManual' : '#modeAuto'); r.checked = true; r.dispatchEvent(new ctx.window.Event('change', { bubbles: true })); };

test('Manual mode moves the one date input beside the toggle as "Send date"; Auto puts it back as "Preview date" with the schedule label', async () => {
    const ctx = await boot(); const { q, document } = ctx;
    const date = q('#campaignDate');
    assert.ok(q('[data-advanced-filters] [data-date-group]'), 'Auto: the date lives under More filters');
    assert.equal(q('[data-policy-schedule]').hidden, false, 'Auto: the policy schedule label is shown');
    pick(ctx, 'Manual');
    assert.ok(q('[data-date-slot] [data-date-group]'), 'Manual: the date picker moves next to the toggle');
    assert.equal(q('[data-date-label]').textContent, 'Send date');
    assert.equal(date.value, '2026-10-21', 'defaults to today when empty');
    assert.equal(q('[data-policy-schedule]').hidden, true, 'Manual: no schedule label');
    date.value = '2026-10-25';
    pick(ctx, 'Auto');
    assert.ok(q('[data-advanced-filters] [data-date-group]')); assert.equal(q('[data-date-label]').textContent, 'Preview date');
    assert.equal(date.value, '2026-10-25', 'the chosen date is kept, never duplicated');
    assert.equal(document.querySelectorAll('input[name="businessDate"]').length, 1, 'exactly one businessDate is ever submitted');
    assert.equal(q('[data-policy-schedule]').hidden, false);
});

test('changing the sending mode after a preview clears the customers, the KPI cards and the ticks like any filter', async () => {
    const ctx = await boot(); const { q, preview } = ctx;
    await preview();
    assert.ok(q('[data-customer-results]'));
    assert.ok(q('[data-kpi-box] [data-campaign-kpis]'), 'the KPI cards are lifted above the results section');
    assert.equal(q('[data-results] [data-campaign-kpis]'), null);
    pick(ctx, 'Manual');
    assert.equal(q('[data-customer-results]'), null); assert.ok(q('[data-results-stale]'));
    assert.equal(q('[data-kpi-box]').children.length, 0, 'stale customers leave no KPI cards behind');
    await preview();
    assert.ok(q('[data-customer-results]'), 'Preview Customers loads the list for the new mode');
    assert.ok(q('[data-kpi-box] [data-campaign-kpis]'));
});

test('Select all ticks only the enabled Ready customers, never a blocked row', async () => {
    const { q, document, window, preview } = await boot();
    await preview();
    const all = q('[data-select-page]');
    all.checked = true; all.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.deepEqual([...document.querySelectorAll('.campaign-select')].map(b => b.checked), [true, true]);
    assert.equal(q('.row-select-disabled').checked, false, 'a blocked / preview-only row is never ticked');
    assert.equal(q('[data-selected-count]').textContent, '2');
    assert.equal(q('[data-selected-summary]').textContent, '2 selected', 'the count stays next to Approve & Send');
    all.checked = false; all.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.equal(q('[data-selected-summary]').textContent, '0 selected');
});

test('Select all shows a partial state, and the summary follows the selection mode', async () => {
    const { q, document, window, preview } = await boot();
    await preview();
    const first = document.querySelector('.campaign-select'); first.checked = true; first.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.equal(q('[data-select-page]').indeterminate, true); assert.equal(q('[data-select-page]').checked, false);
    assert.equal(q('[data-selected-summary]').textContent, '1 selected');
    const allMatching = q('[data-all-matching]'); allMatching.checked = true; allMatching.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.equal(q('[data-selected-summary]').textContent, 'All 2 eligible');
});

test('a disabled Select all (nothing Ready) selects nothing and a disabled row box cannot be ticked', async () => {
    const { q, document, window, preview } = await boot();
    await preview();
    q('[data-select-page]').disabled = true;
    const blocked = q('.row-select-disabled');
    blocked.click();                                                  // a disabled control ignores clicks
    assert.equal(blocked.checked, false);
    q('[data-select-page]').click();
    assert.equal(document.querySelectorAll('.campaign-select:checked').length, 0);
    assert.equal(q('[data-selected-count]').textContent, '0');
});
