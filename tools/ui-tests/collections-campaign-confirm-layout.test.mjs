// Layout of the "Send to Genesys" confirmation dialog in a REAL browser (desktop, short laptop, phone portrait and landscape), using the real stylesheets and 33 long rows.
// It needs Playwright with Chromium; where that is not installed the tests are skipped (the jsdom tests do not measure layout). Nothing here talks to Genesys or the application.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';

const here = dirname(fileURLToPath(import.meta.url));
const CSS = resolve(here, '../../src/TigerCS.Web/wwwroot/css');

let chromium = null;
try { chromium = createRequire(import.meta.url)('playwright').chromium; } catch { /* not installed */ }

const rows = Array.from({ length: 33 }, (_, i) => `<tr><td>FATIMA MOHAMMED ABDULJALIL KAID ${i}</td><td>+971585665478</td><td class="confirm-col-optional">showhomes2024@gmail.com</td><td>TP125-${1300 + i}</td><td class="confirm-col-optional">Overdue reminder - day 1</td><td class="num">AED 1,172,400.00</td><td class="confirm-col-optional">01 Sep 2026</td></tr>`).join('');
const PAGE = `<!doctype html><html><head><link rel="stylesheet" href="${pathToFileURL(CSS + '/site.css')}"><link rel="stylesheet" href="${pathToFileURL(CSS + '/collections-campaigns.css')}"></head><body><h1>Collections Campaigns</h1>
<dialog class="manual-confirm"><form class="panel review-confirm" data-confirm-form><h2>Send 33 unit record(s) to the TigerCS Manual Collections contact list?</h2>
<div class="review-confirm__body"><div class="review-confirm__summary"><dl class="review-snapshot review-snapshot--compact"><dt>Unit records</dt><dd>33</dd><dt>Total amount</dt><dd>AED 38,512,345.67</dd><dt class="summary-extra">Sending mode</dt><dd class="summary-extra">Manual (any time; no send date or schedule)</dd><dt>Campaign</dt><dd>Overdue reminder - day 1</dd><dt>Destination Genesys contact list</dt><dd>TigerCS Manual Collections contact list (11111111-2222-4333-8444-555555555555)</dd><dt class="summary-extra">Financial source</dt><dd class="summary-extra">Reconciled</dd></dl></div>
<div class="review-confirm__table" tabindex="0"><table class="table confirm-table"><thead><tr><th>Customer</th><th>Phone</th><th class="confirm-col-optional">Email</th><th>Unit</th><th class="confirm-col-optional">Stage</th><th class="num">Amount due</th><th class="confirm-col-optional">Due date</th></tr></thead><tbody>${rows}</tbody></table></div></div>
<div class="review-confirm__footer"><label><input type="checkbox" name="ackCampaign" required> I understand that an active Genesys campaign using these lists may start calling the uploaded contacts immediately, and that "uploaded" does not mean "contacted".</label>
<p class="review-confirm__hint" data-confirm-hint>Confirm is unavailable until you tick the box above.</p><div class="review-confirm__actions"><button class="btn btn-primary" disabled data-confirm-button>Confirm: send 33 unit record(s) to Genesys</button><a class="btn">Cancel</a></div></div></form></dialog></body></html>`;

const SIZES = [['desktop', 1280, 720], ['short laptop', 1280, 560], ['phone portrait', 390, 844], ['phone landscape', 844, 390]];

for (const [name, width, height] of SIZES) {
    test(`confirmation dialog layout: ${name} (${width}x${height})`, { skip: chromium ? false : 'Playwright is not installed' }, async t => {
        let browser;
        try { browser = await chromium.launch(); } catch { t.skip('Chromium is not available'); return; }
        try {
            const file = resolve(mkdtempSync(resolve(tmpdir(), 'confirm-layout-')), 'page.html');
            writeFileSync(file, PAGE);
            const page = await browser.newPage({ viewport: { width, height } });
            await page.goto(pathToFileURL(file).href);
            await page.evaluate(() => document.querySelector('dialog').showModal());
            const m = await page.evaluate(() => {
                const $ = s => document.querySelector(s), r = e => e.getBoundingClientRect();
                const inView = e => { const x = r(e); return x.top >= 0 && x.bottom <= innerHeight + 0.5; };
                const dialog = $('dialog'), table = $('.review-confirm__table'), body = $('.review-confirm__body');
                return {
                    dialogScrolls: dialog.scrollHeight > dialog.clientHeight + 1 || dialog.scrollWidth > dialog.clientWidth + 1,
                    bodyClipped: body.scrollHeight > body.clientHeight + 1,
                    tableScrollsVertically: table.scrollHeight > table.clientHeight + 1,
                    tableScrollsSideways: table.scrollWidth > table.clientWidth + 1, pageScrollsSideways: document.documentElement.scrollWidth > innerWidth,
                    title: inView($('h2')), footer: inView($('.review-confirm__footer')), button: inView($('[data-confirm-button]')), hint: inView($('[data-confirm-hint]')),
                    tableHeight: r(table).height, dialogHeight: r(dialog).height
                };
            });
            assert.equal(m.dialogScrolls, false, 'the dialog has no scrollbar of its own');
            assert.equal(m.bodyClipped, false, 'nothing is cut off inside the dialog');
            assert.equal(m.tableScrollsVertically, true, 'the list of unit records is the one scrolling region');
            assert.equal(m.tableScrollsSideways, false, 'no sideways scrolling in the table');
            assert.equal(m.pageScrollsSideways, false);
            assert.ok(m.title && m.footer && m.button && m.hint, 'the heading, the buttons and the reason for the disabled Confirm stay in view');
            assert.ok(m.tableHeight >= 56, `the list keeps a usable height (${m.tableHeight}px)`);
            assert.ok(m.dialogHeight <= height, 'the dialog never exceeds the window');
        } finally { await browser.close(); }
    });
}
