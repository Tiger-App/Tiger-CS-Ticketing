// Behaviour of the real confirmation-dialog script (collections-campaign-confirm.js, with collections-campaign-approve.js beside it) against the dialog's markup:
// loading state, one submission, success only after the backend confirms the upload, the real backend error, retry only when safe, unknown outcomes, and the page state kept.
// fetch is a mock: nothing here contacts Genesys, the API or any server.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { JSDOM } from 'jsdom';

const js = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'TigerCS.Web', 'wwwroot', 'js');
const read = name => readFileSync(join(js, name), 'utf8');

const RETURN_URL = '/Collections/Campaigns?stage=CurrentMonthReminder&sendingMode=Manual&paymentStatus=Overdue&minTotal=250&towerId=7';
const SENDING = 'Sending to Genesys... Please do not close this window.';

// The markup of _CampaignApproval.cshtml's Manual confirmation (status on top, scrolling body, footer), with the hidden fields the plain post carries.
const PAGE = `<!doctype html><html><body>
<dialog class="manual-confirm" data-manual-confirm open aria-labelledby="manualConfirmTitle">
<form method="post" action="/Collections/Campaigns?handler=Confirm" class="panel review-confirm" data-confirm-form
      data-return-url="${RETURN_URL}" data-history-url="/Collections/Campaigns" data-send-status-url="/Collections/Campaigns?handler=Send&dispatch=" data-send-details-url="/Collections/Campaigns?dispatch="
      data-poll-first-ms="5" data-poll-every-ms="5" data-poll-max-ms="400" data-request-timeout-ms="80">
  <h2 id="manualConfirmTitle">Send 2 customer(s) to the Follow Up contact list?</h2>
  <div class="review-confirm__status" data-confirm-status data-confirm-state="idle" hidden tabindex="-1">
    <span class="confirm-spinner" data-confirm-spinner aria-hidden="true" hidden></span>
    <div class="review-confirm__status-text"><p data-confirm-title></p><p data-confirm-detail></p><p data-confirm-actions></p></div>
  </div>
  <div class="review-confirm__body" data-confirm-body>
    <table><tbody><tr><td>Customer A</td></tr><tr><td>Customer B</td></tr></tbody></table>
    <button type="submit" formaction="/Collections/Campaigns?handler=Approve" name="contactPage" value="2" formnovalidate data-paging>More contacts</button>
  </div>
  <div class="review-confirm__footer" data-confirm-footer>
    <input type="hidden" name="__RequestVerificationToken" value="tok" />
    <input type="hidden" name="stage" value="CurrentMonthReminder" /><input type="hidden" name="sendingMode" value="Manual" />
    <input type="hidden" name="dateFrom" value="2026-09-01" /><input type="hidden" name="minTotal" value="250" /><input type="hidden" name="paymentStatus" value="Overdue" />
    <input type="hidden" name="towerId" value="7" /><input type="hidden" name="contactList" value="Manual" />
    <input type="hidden" name="selectionMode" value="SelectedUnits" /><input type="hidden" name="unitKeys" value="a,b" />
    <input type="hidden" name="expectedCount" value="2" /><input type="hidden" name="expectedFingerprint" value="FP" /><input type="hidden" name="idempotencyKey" value="key-1" />
    <label><input type="checkbox" name="ackCampaign" value="true" required checked /> I understand</label>
    <button class="btn btn-primary" type="submit" data-confirm-button>Confirm: send 2 customer(s) to Genesys</button>
    <a class="btn" href="${RETURN_URL}" data-confirm-cancel>Cancel</a>
  </div>
</form></dialog></body></html>`;

const windows = [];
after(() => windows.forEach(w => w.close()));

const json = (body, status = 200) => ({ ok: status < 400, status, headers: { get: () => 'application/json; charset=utf-8' }, json: async () => body });
const html = (status = 200) => ({ ok: status < 400, status, headers: { get: () => 'text/html' }, json: async () => { throw new Error('not json'); } });
const SEND = (over = {}) => ({ status: 'Queued', phase: 'Queued', active: true, approvedCount: 2, uploadedCount: 0, excludedCount: 0, failedCount: 0, unknownCount: 0, statusReason: null, ...over });
const ACCEPTED = { ok: true, dispatchId: 'd1d1d1d1-0000-0000-0000-000000000001', status: 'Queued', approvedCount: 2, unknown: false, retry: false, outcome: 'Accepted' };

/** `handler(url, options)` answers each request; confirm posts and status polls are recorded separately. */
function boot(handler, { withApprove = true, selection = null } = {}) {
    const dom = new JSDOM(PAGE, { url: 'https://tiger.test/Collections/Campaigns', runScripts: 'outside-only', pretendToBeVisual: true });
    const { window } = dom; const { document } = window; windows.push(window);
    if (selection) window.sessionStorage.setItem('collections-campaign-selection', JSON.stringify(selection));
    const confirms = [], polls = [], events = [];
    window.fetch = (url, options = {}) => {
        const u = String(url);
        if (u.includes('handler=Confirm')) { confirms.push({ url: u, options }); return handler({ kind: 'confirm', url: u, options, n: confirms.length }); }
        polls.push(u); return handler({ kind: 'poll', url: u, options, n: polls.length });
    };
    window.addEventListener('collections-send-accepted', e => events.push(e.detail));
    if (withApprove) window.eval(read('collections-campaign-approve.js'));
    window.eval(read('collections-campaign-confirm.js'));
    const q = s => document.querySelector(s);
    const form = q('[data-confirm-form]');
    const state = () => q('[data-confirm-status]').getAttribute('data-confirm-state');
    const title = () => q('[data-confirm-title]').textContent;
    const detail = () => q('[data-confirm-detail]').textContent;
    const wait = ms => new Promise(r => setTimeout(r, ms));
    const until = async (cond, what, ms = 1500) => { const t = Date.now(); while (!cond()) { if (Date.now() - t > ms) assert.fail(`timed out waiting for ${what}; state=${state()} title="${title()}"`); await wait(5); } };
    const fields = options => { const out = {}; for (const [k, v] of options.body.entries()) out[k] = v; return out; };
    const actions = () => [...document.querySelectorAll('[data-confirm-actions] a, [data-confirm-actions] button')];
    return { window, document, q, form, state, title, detail, wait, until, fields, actions, confirms, polls, events, button: q('[data-confirm-button]') };
}

// ---------------------------------------------------------------- loading

test('Confirm shows the loading state at once, in the dialog, and disables the button before any answer', async () => {
    let finish;
    const t = boot(() => new Promise(r => { finish = () => r(json({ ok: false, message: 'x', retry: false, unknown: false, outcome: 'ValidationError' })); }));
    assert.equal(t.q('[data-confirm-status]').hidden, true, 'nothing is shown before the person confirms');
    t.button.click();
    // Synchronously after the click, before the server has answered:
    assert.equal(t.button.disabled, true, 'the button is disabled immediately');
    assert.equal(t.q('[data-confirm-status]').hidden, false);
    assert.equal(t.state(), 'sending');
    assert.equal(t.title(), SENDING);
    assert.equal(t.q('[data-confirm-spinner]').hidden, false, 'a spinner is visible');
    assert.equal(t.q('[data-confirm-status]').getAttribute('role'), 'status');
    assert.ok(t.q('dialog[data-manual-confirm] [data-confirm-status]'), 'the state is inside the dialog');
    assert.equal(t.form.getAttribute('aria-busy'), 'true');
    assert.equal(t.q('[data-paging]').disabled, true, 'paging cannot be pressed while sending');
    assert.equal(t.q('[data-confirm-cancel]').getAttribute('aria-disabled'), 'true');
    assert.equal(t.confirms.length, 1);
    finish(); await t.until(() => t.state() === 'refused', 'the refusal');
});

test('the request is the plain post: same fields, acknowledgement included, Manual mode and filters kept, no navigation', async () => {
    const t = boot(() => Promise.resolve(json({ ok: false, message: 'x', retry: false, unknown: false })));
    t.button.click();
    await t.until(() => t.confirms.length === 1, 'the request');
    const { options, url } = t.confirms[0];
    assert.equal(url, '/Collections/Campaigns?handler=Confirm');
    assert.equal(options.method, 'POST'); assert.equal(options.headers['X-Requested-With'], 'tigercs-confirm'); assert.equal(options.headers.Accept, 'application/json');
    const f = t.fields(options);
    assert.equal(f.ackCampaign, 'true', 'read before the controls were locked');
    assert.deepEqual([f.stage, f.sendingMode, f.dateFrom, f.minTotal, f.paymentStatus, f.towerId, f.contactList], ['CurrentMonthReminder', 'Manual', '2026-09-01', '250', 'Overdue', '7', 'Manual']);
    assert.deepEqual([f.selectionMode, f.unitKeys, f.expectedCount, f.expectedFingerprint, f.idempotencyKey, f.__RequestVerificationToken], ['SelectedUnits', 'a,b', '2', 'FP', 'key-1', 'tok']);
    assert.equal(t.window.location.href, 'https://tiger.test/Collections/Campaigns', 'the page itself is not navigated');
    // "Back to the list" returns to the same preview: campaign, filters and Manual mode.
    await t.until(() => t.state() === 'refused', 'the refusal');
    assert.equal(t.actions().find(a => a.textContent === 'Back to the list').getAttribute('href'), RETURN_URL);
});

// ---------------------------------------------------------------- success only once the backend confirms the upload

test('accepted is not success: the dialog keeps loading until the send ends, then reports the real uploaded count', async () => {
    const states = [SEND({ status: 'Queued' }), SEND({ status: 'Revalidating', phase: 'Checking current balances (company 4)' }), SEND({ status: 'Sending', phase: 'Uploading batch 1' }),
        SEND({ status: 'Completed', active: false, uploadedCount: 2, approvedCount: 2, excludedCount: 0 })];
    const t = boot(({ kind, n }) => Promise.resolve(kind === 'confirm' ? json(ACCEPTED) : json(states[Math.min(n - 1, states.length - 1)])));
    t.button.click();
    await t.until(() => t.events.length === 1, 'the approval being accepted');
    assert.equal(t.events[0].dispatchId, ACCEPTED.dispatchId);
    // Accepted and queued, nothing uploaded yet: still loading, and no success wording anywhere.
    assert.equal(t.state(), 'tracking'); assert.equal(t.title(), SENDING); assert.equal(t.q('[data-confirm-spinner]').hidden, false);
    assert.doesNotMatch(t.q('[data-confirm-status]').textContent, /uploaded\.|Sent to Genesys:/);
    await t.until(() => t.state() === 'success', 'the confirmed outcome');
    assert.equal(t.title(), 'Sent to Genesys: 2 unit record(s) uploaded.');
    assert.match(t.detail(), /Genesys accepted 2 of 2 approved record\(s\)/); assert.match(t.detail(), /does not mean the customers were called/);
    assert.equal(t.q('[data-confirm-spinner]').hidden, true, 'loading stops');
    assert.equal(t.button.disabled, true, 'the button is never enabled again on this page');
    assert.equal(t.confirms.length, 1, 'one request');
    assert.ok(t.polls.every(u => u === `/Collections/Campaigns?handler=Send&dispatch=${ACCEPTED.dispatchId}`));
    const [open, back] = t.actions();
    assert.deepEqual([open.textContent, open.getAttribute('href')], ['View send details', `/Collections/Campaigns?dispatch=${ACCEPTED.dispatchId}`]);
    assert.equal(back.getAttribute('href'), RETURN_URL);
    assert.equal(t.q('[data-confirm-cancel]').hasAttribute('aria-disabled'), false, 'the dialog can be left again');
});

test('the progress text of the running send is shown while loading', async () => {
    const t = boot(({ kind, n }) => Promise.resolve(kind === 'confirm' ? json(ACCEPTED) : json(n < 3 ? SEND({ status: 'Revalidating', phase: 'Checking current balances (company 4)' }) : SEND({ status: 'Completed', active: false, uploadedCount: 1, approvedCount: 1 }))));
    t.button.click();
    await t.until(() => /Checking current balances/.test(t.detail()), 'the phase');
    assert.equal(t.title(), SENDING);
    await t.until(() => t.state() === 'success', 'the end');
});

test('every outcome that is not a full success is reported as what it is, with real counts and never as success', async () => {
    const cases = [
        [{ status: 'CompletedWithErrors', uploadedCount: 5, failedCount: 2, unknownCount: 0, approvedCount: 7 }, 'partial', /^Partly sent: 5 unit record\(s\) uploaded\.$/, /2 were rejected by Genesys/],
        [{ status: 'CompletedWithErrors', uploadedCount: 0, failedCount: 3, approvedCount: 3, statusReason: 'Genesys rejected the batch.' }, 'failure', /^The send failed: nothing was uploaded/, /Genesys rejected the batch\./],
        [{ status: 'CompletedWithErrors', uploadedCount: 0, unknownCount: 3, approvedCount: 3 }, 'unknown', /^Genesys did not confirm the upload\. Do not send this list again\./, /3 record\(s\) are unconfirmed/],
        [{ status: 'ReviewRequired', uploadedCount: 0, approvedCount: 3, statusReason: '1 approved record(s) changed after approval. Nothing was sent.' }, 'failure', /^Stopped for review: nothing was sent/, /1 approved record\(s\) changed/],
        [{ status: 'Failed', uploadedCount: 0, approvedCount: 3, statusReason: 'Current balances could not be read, so nothing was sent.' }, 'failure', /^The send failed: nothing was uploaded/, /Current balances could not be read/],
        [{ status: 'Cancelled', approvedCount: 3 }, 'failure', /^The send was cancelled/, /^$/],
        [{ status: 'Completed', uploadedCount: 0, approvedCount: 3, excludedCount: 3 }, 'failure', /^Nothing was uploaded to Genesys\./, /3 record\(s\) were paid or settled/]
    ];
    for (const [over, kind, titleRe, detailRe] of cases) {
        const t = boot(({ kind: k }) => Promise.resolve(k === 'confirm' ? json(ACCEPTED) : json(SEND({ active: false, ...over }))));
        t.button.click();
        await t.until(() => t.state() !== 'sending' && t.state() !== 'tracking', `the outcome ${over.status}`);
        assert.equal(t.state(), kind, over.status); assert.match(t.title(), titleRe); assert.match(t.detail(), detailRe);
        assert.doesNotMatch(t.q('[data-confirm-status]').textContent, /Sent to Genesys:/);
        assert.equal(t.q('[data-confirm-spinner]').hidden, true);
        assert.equal(t.actions().some(a => a.hasAttribute('data-confirm-retry')), false, 'a finished send is never retried from here');
    }
});

// ---------------------------------------------------------------- failure: the backend's message, retry only when safe

test('a refusal shows the real backend message, stops loading, offers no retry for a list that must be reviewed, and keeps the selection', async () => {
    const message = 'The records, amounts or contacts changed since this list was shown. Review the current list and confirm again.';
    const t = boot(() => Promise.resolve(json({ ok: false, message, retry: false, unknown: false, outcome: 'Conflict' })), { selection: ['a', 'b'] });
    t.button.click();
    await t.until(() => t.state() === 'refused', 'the refusal');
    assert.equal(t.title(), 'The send was not approved. Nothing was sent to Genesys.');
    assert.equal(t.detail(), message, 'the backend text, verbatim');
    assert.equal(t.q('[data-confirm-spinner]').hidden, true);
    assert.equal(t.q('[data-confirm-status]').getAttribute('role'), 'alert');
    assert.equal(t.actions().some(a => a.hasAttribute('data-confirm-retry')), false);
    assert.equal(t.button.disabled, true);
    assert.equal(t.events.length, 0, 'nothing was accepted');
    assert.equal(t.window.sessionStorage.getItem('collections-campaign-selection'), '["a","b"]', 'the ticked customers are kept');
    assert.equal(t.polls.length, 0);
});

test('a definite refusal that created nothing offers Try again, which re-sends the same idempotency key and then follows the send', async () => {
    const message = 'Customer dispatch to Genesys is switched off in this environment.';
    const t = boot(({ kind, n }) => Promise.resolve(kind === 'confirm'
        ? (n === 1 ? json({ ok: false, message, retry: true, unknown: false, outcome: 'ServiceUnavailable' }, 200) : json(ACCEPTED))
        : json(SEND({ status: 'Completed', active: false, uploadedCount: 2 }))));
    t.button.click();
    await t.until(() => t.state() === 'refused', 'the refusal');
    assert.equal(t.detail(), message);
    const again = t.q('[data-confirm-retry]');
    assert.ok(again, 'Try again is offered'); assert.equal(again.textContent, 'Try again');
    assert.equal(t.confirms.length, 1, 'nothing is retried by itself');
    await t.wait(40); assert.equal(t.confirms.length, 1);
    again.click();
    assert.equal(t.state(), 'sending'); assert.equal(t.title(), SENDING);
    await t.until(() => t.state() === 'success', 'the second attempt');
    assert.equal(t.confirms.length, 2);
    const [first, second] = t.confirms.map(c => t.fields(c.options));
    assert.deepEqual(second, first, 'the very same fields, idempotency key included');
    assert.equal(second.idempotencyKey, 'key-1'); assert.equal(second.ackCampaign, 'true');
    assert.equal(t.title(), 'Sent to Genesys: 2 unit record(s) uploaded.');
});

test('Try again cannot be used twice at once', async () => {
    let release;
    const t = boot(({ kind, n }) => kind === 'confirm' && n === 1 ? Promise.resolve(json({ ok: false, message: 'busy', retry: true, unknown: false }))
        : kind === 'confirm' ? new Promise(r => { release = () => r(json({ ok: false, message: 'busy again', retry: false, unknown: false })); }) : Promise.resolve(json(SEND())));
    t.button.click(); await t.until(() => t.state() === 'refused', 'the refusal');
    const again = t.q('[data-confirm-retry]'); again.click(); again.click();
    assert.equal(t.confirms.length, 2, 'one retry request, not two');
    release(); await t.until(() => t.state() === 'refused', 'the second refusal');
    assert.equal(t.q('[data-confirm-retry]'), null);
});

// ---------------------------------------------------------------- unknown outcome: never retried, verify first

test('a network failure is an unknown outcome: no automatic retry, the form stays closed, the person is told to check the recent sends', async () => {
    const t = boot(() => Promise.reject(new TypeError('Failed to fetch')), { selection: ['a'] });
    t.button.click();
    await t.until(() => t.state() === 'unknown', 'the unknown outcome');
    assert.equal(t.title(), 'The outcome is not known yet. Do not send this list again.');
    assert.match(t.detail(), /connection was lost/); assert.match(t.detail(), /Check the recent sends first/);
    assert.equal(t.q('[data-confirm-spinner]').hidden, true);
    assert.equal(t.q('[data-confirm-status]').getAttribute('role'), 'alert');
    assert.equal(t.actions().some(a => a.hasAttribute('data-confirm-retry')), false, 'no Try again');
    assert.deepEqual(t.actions().map(a => [a.textContent, a.getAttribute('href')]), [['Check recent sends', '/Collections/Campaigns'], ['Back to the list', RETURN_URL]]);
    assert.equal(t.button.disabled, true);
    await t.wait(80);
    assert.equal(t.confirms.length, 1, 'never retried automatically');
    assert.equal(t.events.length, 0);
    // The form cannot be submitted again, by any route.
    const submit = new t.window.Event('submit', { bubbles: true, cancelable: true });
    t.form.dispatchEvent(submit); assert.equal(submit.defaultPrevented, true); assert.equal(t.confirms.length, 1);
});

test('no answer in time is an unknown outcome too (the request is aborted client-side, never resent)', async () => {
    const t = boot(({ kind, options }) => new Promise((_, reject) => { options.signal.addEventListener('abort', () => { const e = new Error('aborted'); e.name = 'AbortError'; reject(e); }); }));
    t.button.click();
    await t.until(() => t.state() === 'unknown', 'the timeout');
    assert.match(t.detail(), /did not answer in time/);
    await t.wait(60); assert.equal(t.confirms.length, 1);
});

test('an unusable reply (a login page, a server error page) is an unknown outcome, not a success and not a retry', async () => {
    for (const reply of [html(200), html(502), json({ ok: false }, 500)]) {
        const t = boot(() => Promise.resolve(reply));
        t.button.click();
        await t.until(() => t.state() === 'unknown', 'the unusable reply');
        assert.match(t.detail(), /unusable reply/); assert.equal(t.q('[data-confirm-retry]'), null);
    }
});

test('an answer the server itself marks unknown (API unreachable / timed out) is reported without a retry', async () => {
    const t = boot(() => Promise.resolve(json({ ok: false, unknown: true, retry: false, outcome: 'Unreachable', message: 'The Tiger CS API did not confirm the outcome, so it is not known whether this send was approved.' })));
    t.button.click();
    await t.until(() => t.state() === 'unknown', 'the unknown outcome');
    assert.match(t.detail(), /not known whether this send was approved/); assert.equal(t.q('[data-confirm-retry]'), null);
});

test('if the status of an accepted send cannot be read, the dialog says so, links to that send, and never claims success', async () => {
    const t = boot(({ kind }) => kind === 'confirm' ? Promise.resolve(json(ACCEPTED)) : Promise.reject(new Error('offline')));
    t.button.click();
    await t.until(() => t.state() === 'unknown', 'the unknown status');
    assert.match(t.detail(), /status of this send could not be read/);
    assert.equal(t.polls.length, 3, 'three failed reads in a row, then it stops');
    assert.equal(t.actions()[0].getAttribute('href'), `/Collections/Campaigns?dispatch=${ACCEPTED.dispatchId}`);
    assert.doesNotMatch(t.q('[data-confirm-status]').textContent, /Sent to Genesys:/);
    // The status endpoint's own "Unknown" (the API could not answer) is the same.
    const u = boot(({ kind }) => Promise.resolve(kind === 'confirm' ? json(ACCEPTED) : json(SEND({ status: 'Unknown', active: false }))));
    u.button.click(); await u.until(() => u.state() === 'unknown', 'the Unknown status');
});

test('a send that keeps running past the limit is reported as still unknown, not as success', async () => {
    const t = boot(({ kind }) => Promise.resolve(kind === 'confirm' ? json(ACCEPTED) : json(SEND({ status: 'Sending' }))));
    t.form.dataset.pollMaxMs = '60';
    t.button.click();
    await t.until(() => t.state() === 'unknown', 'the time limit', 3000);
    assert.match(t.detail(), /taking longer than expected/);
});

// ---------------------------------------------------------------- one submission only

test('repeated clicks, Enter and repeated submit events send exactly one request', async () => {
    let finish;
    const t = boot(() => new Promise(r => { finish = () => r(json({ ok: false, message: 'x', retry: false, unknown: false })); }));
    t.button.click(); t.button.click(); t.button.click();
    t.form.requestSubmit();                                           // Enter in the form
    t.form.requestSubmit();
    const submit = new t.window.Event('submit', { bubbles: true, cancelable: true }); t.form.dispatchEvent(submit);
    assert.equal(submit.defaultPrevented, true, 'a stray submit is cancelled');
    assert.equal(t.confirms.length, 1);
    finish(); await t.until(() => t.state() === 'refused', 'the answer');
    t.form.requestSubmit(); t.form.dispatchEvent(new t.window.Event('submit', { bubbles: true, cancelable: true }));
    assert.equal(t.confirms.length, 1, 'and still one after the answer');
});

test('while sending, the paging buttons do not submit either, and the acknowledgement cannot be changed', async () => {
    let finish;
    const t = boot(() => new Promise(r => { finish = () => r(json({ ok: false, message: 'x', retry: false, unknown: false })); }));
    t.button.click();
    const paging = new t.window.SubmitEvent('submit', { bubbles: true, cancelable: true, submitter: t.q('[data-paging]') });
    t.form.dispatchEvent(paging); assert.equal(paging.defaultPrevented, true);
    const ack = t.q('input[name="ackCampaign"]');
    const click = new t.window.MouseEvent('click', { bubbles: true, cancelable: true }); ack.dispatchEvent(click);
    assert.equal(click.defaultPrevented, true); assert.equal(ack.checked, true);
    assert.equal(t.confirms.length, 1);
    finish(); await t.until(() => t.state() === 'refused', 'the answer');
});

test('before the first press the paging buttons still post the form normally (More contacts)', async () => {
    const t = boot(() => Promise.resolve(json({ ok: true })));
    const paging = new t.window.SubmitEvent('submit', { bubbles: true, cancelable: true, submitter: t.q('[data-paging]') });
    t.form.dispatchEvent(paging);
    assert.equal(paging.defaultPrevented, false, 'not taken over');
    assert.equal(t.confirms.length, 0); assert.equal(t.state(), 'idle');
});

// ---------------------------------------------------------------- the dialog cannot be dismissed while sending

test('Escape and Cancel are blocked while the request or the upload is followed, and released when it ends', async () => {
    let finish;
    const t = boot(({ kind }) => kind === 'confirm' ? new Promise(r => { finish = () => r(json(ACCEPTED)); }) : Promise.resolve(json(SEND({ status: 'Completed', active: false, uploadedCount: 2 }))));
    t.button.click();
    const escape = new t.window.Event('cancel', { cancelable: true }); t.q('dialog').dispatchEvent(escape);
    assert.equal(escape.defaultPrevented, true, 'Escape does not close the dialog while sending');
    const cancel = new t.window.MouseEvent('click', { bubbles: true, cancelable: true }); t.q('[data-confirm-cancel]').dispatchEvent(cancel);
    assert.equal(cancel.defaultPrevented, true);
    finish(); await t.until(() => t.state() === 'success', 'the end');
    const after = new t.window.Event('cancel', { cancelable: true }); t.q('dialog').dispatchEvent(after);
    assert.equal(after.defaultPrevented, false, 'released once the result is shown');
});

// ---------------------------------------------------------------- selection and the plain-post fallback

test('the ticked customers are cleared only when the server accepted the approval', async () => {
    const accepted = boot(({ kind }) => Promise.resolve(kind === 'confirm' ? json(ACCEPTED) : json(SEND({ status: 'Completed', active: false, uploadedCount: 2 }))), { selection: ['a', 'b'] });
    accepted.button.click(); await accepted.until(() => accepted.state() === 'success', 'success');
    assert.equal(accepted.window.sessionStorage.getItem('collections-campaign-selection'), '[]');
    const refused = boot(() => Promise.resolve(json({ ok: false, message: 'no', retry: false, unknown: false })), { selection: ['a', 'b'] });
    refused.button.click(); await refused.until(() => refused.state() === 'refused', 'refusal');
    assert.equal(refused.window.sessionStorage.getItem('collections-campaign-selection'), '["a","b"]');
    const unknown = boot(() => Promise.reject(new Error('offline')), { selection: ['a', 'b'] });
    unknown.button.click(); await unknown.until(() => unknown.state() === 'unknown', 'unknown');
    assert.equal(unknown.window.sessionStorage.getItem('collections-campaign-selection'), '["a","b"]', 'an unknown outcome keeps the selection so the list can be checked');
});

test('without the dialog script the plain post still protects against a double submit', async () => {
    const dom = new JSDOM(PAGE, { url: 'https://tiger.test/Collections/Campaigns', runScripts: 'outside-only', pretendToBeVisual: true });
    const { window } = dom; windows.push(window);
    window.sessionStorage.setItem('collections-campaign-selection', '["a"]');
    window.eval(read('collections-campaign-approve.js'));          // no confirm script
    let posts = 0;
    window.document.addEventListener('submit', e => { posts++; e.preventDefault(); });   // stands in for the navigation
    const button = window.document.querySelector('[data-confirm-button]');
    button.click(); button.click();
    assert.equal(posts, 1); assert.equal(button.disabled, true); assert.equal(button.textContent, 'Sending…');
    assert.equal(window.sessionStorage.getItem('collections-campaign-selection'), '[]');
});
