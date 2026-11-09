/*
  Collections Campaigns: the "Confirm: send N unit record(s) to Genesys" dialog.

  What the person sees, in order, inside the dialog (the state is always at its top, never below the contact list):
    1. Confirm is pressed: at once the button is disabled, the form is locked and a spinner says "Sending to Genesys... Please do not close this window."
       The form is submitted ONCE: a second click, Enter, or a repeated submit event does nothing while a send is being tracked or after it has ended.
    2. The server accepts (or refuses) the approval. A refusal shows the backend's own message. "Accepted" is NOT success: the upload happens afterwards in a background job.
    3. The dialog follows that send's own status until it ends and only then reports it, with the real numbers: "N unit record(s) uploaded" (what Genesys accepted).
       Partial or failed outcomes say what was uploaded, rejected or unconfirmed; they never read as success.
    4. An answer that does not say whether the approval was created (no answer in time, unreachable, unusable reply) is an UNKNOWN outcome: nothing is retried, the form stays locked and
       the person is told to check the recent sends first. "Try again" is offered only for a definite refusal that created nothing (server: retry=true); it re-sends the same idempotency key.

  Campaign, filters and Manual mode are not touched: the page is not navigated, and the posted form carries the same hidden fields as the plain post (which still works without JavaScript).
  Texts from the server are written with textContent, never as markup.
*/
(() => {
    if (typeof fetch !== 'function' || typeof FormData !== 'function') return;   // the plain form post (and its redirect) remains the fallback

    const SENDING = 'Sending to Genesys... Please do not close this window.';
    const AJAX_HEADER = 'X-Requested-With', AJAX_VALUE = 'tigercs-confirm';
    // Overridable per form (data-*-ms) so tests do not wait; the defaults are the real behaviour.
    const num = (form, name, fallback) => { const v = Number(form.dataset[name]); return Number.isFinite(v) && v >= 0 && form.dataset[name] !== undefined ? v : fallback; };

    // One send per page: idle -> sending (request) -> tracking (upload running) -> ended | unknown | refused. Only idle may submit; refused-with-retry may be retried by its own button.
    let state = 'idle';
    let retryable = false;
    let dispatchId = null;

    const fmt = n => Number(n).toLocaleString('en-US');
    const el = (tag, attrs = {}, text) => { const e = document.createElement(tag); for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v); if (text !== undefined) e.textContent = text; return e; };

    const parts = form => ({
        dialog: form.closest('dialog'),
        button: form.querySelector('[data-confirm-button]'),
        status: form.querySelector('[data-confirm-status]'),
        spinner: form.querySelector('[data-confirm-spinner]'),
        title: form.querySelector('[data-confirm-title]'),
        detail: form.querySelector('[data-confirm-detail]'),
        actions: form.querySelector('[data-confirm-actions]'),
        cancel: form.querySelector('[data-confirm-cancel]')
    });

    /**
     * From the first press on the form is closed for good: the Confirm button and the paging buttons stay disabled, and the acknowledgement cannot be changed (it is NOT disabled: a disabled
     * control is not part of a form post, and a retry must send the same acknowledgement). While a send is followed, the dialog cannot be left either (<paramref name="busy"/>).
     */
    const lock = (form, busy) => {
        const p = parts(form);
        form.setAttribute('aria-busy', busy ? 'true' : 'false');
        if (p.button) p.button.disabled = true;
        form.querySelectorAll('button[formaction], button[name="contactPage"]').forEach(b => { b.disabled = true; });
        const ack = form.querySelector('input[name="ackCampaign"]');
        if (ack) ack.setAttribute('aria-disabled', 'true');
        if (p.cancel) { if (busy) { p.cancel.setAttribute('aria-disabled', 'true'); p.cancel.setAttribute('tabindex', '-1'); } else { p.cancel.removeAttribute('aria-disabled'); p.cancel.removeAttribute('tabindex'); } }
    };

    const leaveGuard = e => { e.preventDefault(); e.returnValue = ''; };
    const guardLeaving = on => { window[on ? 'addEventListener' : 'removeEventListener']('beforeunload', leaveGuard); };

    /** Writes only what changed: a poll that finds the same text must not make a screen reader announce it again. */
    const setText = (node, text) => { if (node && node.textContent !== text) node.textContent = text; };

    /** Shows one state: sending | tracking | success | partial | failure | unknown | refused. */
    const show = (form, kind, title, detail, { spinner = false, actions = [] } = {}) => {
        const p = parts(form);
        if (!p.status) return;
        const changed = p.status.getAttribute('data-confirm-state') !== kind;
        p.status.hidden = false;
        p.status.setAttribute('data-confirm-state', kind);
        p.status.setAttribute('role', kind === 'failure' || kind === 'refused' || kind === 'unknown' || kind === 'partial' ? 'alert' : 'status');
        p.status.setAttribute('aria-live', kind === 'failure' || kind === 'refused' || kind === 'unknown' ? 'assertive' : 'polite');
        if (p.spinner) p.spinner.hidden = !spinner;
        setText(p.title, title);
        if (p.detail) { setText(p.detail, detail || ''); p.detail.hidden = !detail; }
        if (p.actions && (changed || actions.length)) { p.actions.replaceChildren(...actions); p.actions.hidden = actions.length === 0; }
        // The state is always inside the visible part of the dialog: when it changes, bring it into view and give it focus for screen readers.
        if (changed) {
            if (typeof p.status.scrollIntoView === 'function') p.status.scrollIntoView({ block: 'nearest' });
            if (typeof p.status.focus === 'function') p.status.focus({ preventScroll: true });
        }
    };

    const link = (href, text, primary) => el('a', { class: primary ? 'btn btn-primary' : 'btn', href }, text);
    const backLink = form => link(form.dataset.returnUrl || '/Collections/Campaigns', 'Back to the list');
    const detailsLink = (form, id, text = 'View send details') => link((form.dataset.sendDetailsUrl || '/Collections/Campaigns?dispatch=') + encodeURIComponent(id), text, true);

    // ------------------------------------------------------------ the answers

    const showUnknown = (form, why, id) => {
        state = 'unknown'; retryable = false; guardLeaving(false); lock(form, false);
        show(form, 'unknown', 'The outcome is not known yet. Do not send this list again.',
            `${why} Check the recent sends first: if this send appears there, it was approved (and may already be uploading); if it does not, go back to the list and start again.`,
            { actions: [id ? detailsLink(form, id, 'Open this send') : link(form.dataset.historyUrl || '/Collections/Campaigns', 'Check recent sends', true), backLink(form)] });
    };

    const showRefused = (form, message, retry) => {
        state = retry ? 'refused' : 'ended'; retryable = !!retry; guardLeaving(false); lock(form, false);
        const actions = [];
        if (retry) {
            const again = el('button', { type: 'button', class: 'btn btn-primary', 'data-confirm-retry': '' }, 'Try again');
            again.addEventListener('click', () => { if (state === 'refused' && retryable) send(form); });
            actions.push(again);
        }
        actions.push(backLink(form));
        show(form, 'refused', 'The send was not approved. Nothing was sent to Genesys.', message || 'The request could not be completed.', { actions });
    };

    /** The send ended: report it from its own status, with the real counts. Never "success" unless Genesys accepted every approved contact. */
    const showEnded = (form, id, s) => {
        state = 'ended'; guardLeaving(false); lock(form, false);
        const up = s.uploadedCount || 0, failed = s.failedCount || 0, unknown = s.unknownCount || 0, excluded = s.excludedCount || 0, approved = s.approvedCount || 0;
        const reason = s.statusReason || '';
        const actions = [detailsLink(form, id), backLink(form)];
        const left = excluded > 0 ? ` ${fmt(excluded)} record(s) were paid or settled before sending and were left out.` : '';
        const note = ' "Uploaded" means Genesys accepted the contacts; it does not mean the customers were called yet.';
        if (s.status === 'Completed' && up > 0 && failed === 0 && unknown === 0) {
            show(form, 'success', `Sent to Genesys: ${fmt(up)} unit record(s) uploaded.`, `Genesys accepted ${fmt(up)} of ${fmt(approved)} approved record(s).${left}${note}`, { actions });
        } else if (up > 0) {
            show(form, 'partial', `Partly sent: ${fmt(up)} unit record(s) uploaded.`,
                `${failed > 0 ? `${fmt(failed)} were rejected by Genesys. ` : ''}${unknown > 0 ? `${fmt(unknown)} are unconfirmed: check them in Genesys before sending anything again. ` : ''}${reason}${left}`.trim(), { actions });
        } else if (unknown > 0) {
            show(form, 'unknown', 'Genesys did not confirm the upload. Do not send this list again.',
                `${fmt(unknown)} record(s) are unconfirmed. Open the send, check the contact list in Genesys, then record what you found.${left}`, { actions });
        } else if (s.status === 'Completed') {
            show(form, 'failure', 'Nothing was uploaded to Genesys.', `${reason || 'Every approved record was paid or settled before sending, or none was accepted.'}${left}`, { actions });
        } else if (s.status === 'ReviewRequired') {
            show(form, 'failure', 'Stopped for review: nothing was sent to Genesys.', `${reason} To send, preview the list again and approve it.`.trim(), { actions });
        } else if (s.status === 'Cancelled') {
            show(form, 'failure', 'The send was cancelled: nothing was sent to Genesys.', reason, { actions });
        } else {
            show(form, 'failure', 'The send failed: nothing was uploaded to Genesys.', `${reason || 'Genesys did not accept the contacts.'} To send, preview the list again and approve it.`, { actions });
        }
    };

    // ------------------------------------------------------------ following the send

    const track = async (form, id) => {
        state = 'tracking';
        const first = num(form, 'pollFirstMs', 1000), every = num(form, 'pollEveryMs', 2000), maxMs = num(form, 'pollMaxMs', 20 * 60 * 1000);
        const url = (form.dataset.sendStatusUrl || '/Collections/Campaigns?handler=Send&dispatch=') + encodeURIComponent(id);
        const started = Date.now(); let errors = 0;
        show(form, 'tracking', SENDING, 'The approval was accepted. Checking current balances and uploading; the result appears here when Genesys has answered.', { spinner: true });
        await new Promise(r => setTimeout(r, first));
        for (;;) {
            try {
                const response = await fetch(url, { headers: { Accept: 'application/json' }, cache: 'no-store', credentials: 'same-origin' });
                if (!response.ok) throw new Error(`HTTP ${response.status}`);
                const s = await response.json(); errors = 0;
                if (s.status === 'Unknown') { showUnknown(form, 'The status of this send could not be read.', id); return; }
                if (!s.active) { showEnded(form, id, s); return; }
                show(form, 'tracking', SENDING, s.phase ? `${s.phase}…` : 'Checking current balances and uploading.', { spinner: true });
            } catch {
                if (++errors >= 3) { showUnknown(form, 'The status of this send could not be read.', id); return; }
            }
            if (Date.now() - started >= maxMs) { showUnknown(form, 'The send is taking longer than expected.', id); return; }
            await new Promise(r => setTimeout(r, every));
        }
    };

    // ------------------------------------------------------------ the one request

    const send = async form => {
        // The form data is read BEFORE the controls are locked: a disabled control is not part of a form post (the acknowledgement would be lost).
        const body = new FormData(form);
        state = 'sending'; retryable = false;
        lock(form, true); guardLeaving(true);
        show(form, 'sending', SENDING, '', { spinner: true });
        const control = typeof AbortController === 'function' ? new AbortController() : null;
        const timer = control ? setTimeout(() => control.abort(), num(form, 'requestTimeoutMs', 120000)) : null;
        let answer;
        try {
            const response = await fetch(form.getAttribute('action') || window.location.href, {
                method: 'POST', body, credentials: 'same-origin', cache: 'no-store', signal: control ? control.signal : undefined,
                headers: { Accept: 'application/json', [AJAX_HEADER]: AJAX_VALUE }
            });
            const type = response.headers && response.headers.get ? (response.headers.get('content-type') || '') : 'application/json';
            if (!response.ok || !type.includes('json')) { showUnknown(form, `The server sent an unusable reply (HTTP ${response.status}).`); return; }
            answer = await response.json();
        } catch (error) {
            showUnknown(form, error && error.name === 'AbortError' ? 'The server did not answer in time.' : 'The server could not be reached or the connection was lost.');
            return;
        } finally { if (timer) clearTimeout(timer); }

        if (!answer || typeof answer !== 'object') { showUnknown(form, 'The server sent an unusable reply.'); return; }
        if (!answer.ok) {
            if (answer.unknown) showUnknown(form, answer.message || 'The server did not confirm the outcome.');
            else showRefused(form, answer.message, answer.retry === true);
            return;
        }
        dispatchId = answer.dispatchId;
        window.dispatchEvent(new CustomEvent('collections-send-accepted', { detail: { dispatchId } }));   // the ticked customers are spent
        await track(form, dispatchId);
    };

    // ------------------------------------------------------------ the acknowledgement

    /**
     * Confirm stays unavailable until the acknowledgement is ticked, and the line under the box says so (a greyed button without a reason looked broken). Without JavaScript the button is enabled and
     * the browser's own "required" check on the box applies, so nothing depends on this for safety: the server refuses an unacknowledged confirmation as well.
     */
    const syncAcknowledgement = form => {
        if (state !== 'idle') return;
        const p = parts(form);
        const ack = form.querySelector('input[name="ackCampaign"]');
        if (!p.button || !ack) return;
        p.button.disabled = !ack.checked;
        const hint = form.querySelector('[data-confirm-hint]');
        if (hint) hint.hidden = ack.checked;
    };
    const syncAll = () => document.querySelectorAll('[data-confirm-form]').forEach(syncAcknowledgement);
    syncAll();
    document.addEventListener('DOMContentLoaded', syncAll);
    document.addEventListener('change', event => {
        const ack = event.target && event.target.closest ? event.target.closest('input[name="ackCampaign"]') : null;
        const form = ack ? ack.closest('[data-confirm-form]') : null;
        if (form) syncAcknowledgement(form);
    });

    // ------------------------------------------------------------ wiring

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!form || !form.matches || !form.matches('[data-confirm-form]')) return;
        // While a send is being tracked, or once it has ended, nothing submits: not a second click, not Enter, not a paging button.
        if (state !== 'idle') { event.preventDefault(); return; }
        const submitter = event.submitter;
        if (submitter && !submitter.hasAttribute('data-confirm-button')) return;   // "More contacts" / "Previous contacts" post the form normally
        event.preventDefault();
        send(form);
        // Capture phase on purpose: this must run BEFORE the bubbling submit listener of collections-campaign-approve.js (plain-post fallback), which only acts on a submit nobody cancelled.
    }, true);

    // The dialog cannot be dismissed (Escape, Cancel) while the request or the upload is being followed.
    document.addEventListener('cancel', event => {
        if (event.target && event.target.matches && event.target.matches('dialog[data-manual-confirm]') && (state === 'sending' || state === 'tracking')) event.preventDefault();
    }, true);
    document.addEventListener('click', event => {
        const target = event.target;
        if (!target || !target.closest) return;
        const cancel = target.closest('[data-confirm-cancel]');
        if (cancel && (state === 'sending' || state === 'tracking')) event.preventDefault();
        // The acknowledgement is part of what was sent: it cannot be changed after the first press.
        const ack = target.closest('input[name="ackCampaign"]');
        if (ack && ack.closest('[data-confirm-form]') && state !== 'idle') event.preventDefault();
    });
})();
