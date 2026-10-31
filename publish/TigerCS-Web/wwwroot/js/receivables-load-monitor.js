/*
  Collections: watches a running snapshot refresh by polling a tiny JSON status (handler=Status) - never the results fragment.
  Guarantees (unit-tested in tools/ui-tests/receivables-load-monitor.test.mjs):
    * exactly one loop per monitor: the next poll is scheduled only after the previous answer (or failure) has been handled, so requests never overlap;
    * start() / stop() supersede everything before them: a late answer from an older loop is discarded and its request aborted;
    * it ends as soon as the server reports nothing is loading (onDone), after maxPolls / maxMs (onGiveUp), or after repeated failures (onGiveUp 'error');
    * polling pauses while the tab is hidden (it neither counts nor requests);
    * start({expectActive:true}) tolerates the short gap between "retry requested" and "refresh running" without declaring it done.
*/
(function (root, factory) {
    const api = factory();
    if (typeof module === 'object' && module.exports) module.exports = api; else root.ReceivablesLoadMonitor = api;
})(typeof self !== 'undefined' ? self : this, function () {
    const DEFAULTS = { delays: [4000, 4000, 8000, 8000, 15000], maxPolls: 60, maxMs: 10 * 60 * 1000, graceMs: 30000, maxErrors: 3 };

    function create(options) {
        const o = Object.assign({}, DEFAULTS, options);
        const now = o.now || (() => Date.now());
        const setTimer = o.setTimer || ((fn, ms) => setTimeout(fn, ms));
        const clearTimer = o.clearTimer || (id => clearTimeout(id));
        const hidden = o.isHidden || (() => false);
        const makeController = o.createController || (() => (typeof AbortController === 'function' ? new AbortController() : { signal: undefined, abort() {} }));
        let generation = 0, timer = null, controller = null, polls = 0, errors = 0, startedAt = 0, running = false, seenActive = false, expectActive = false, inFlight = false;

        function halt() {
            generation++; running = false; inFlight = false;
            if (timer !== null) { clearTimer(timer); timer = null; }
            if (controller) { try { controller.abort(); } catch { /* already finished */ } controller = null; }
        }
        function delayFor(n) { return o.delays[Math.min(n, o.delays.length - 1)]; }
        function schedule(mine) { timer = setTimer(() => { timer = null; poll(mine); }, delayFor(polls)); }
        function finish(mine, fn, status, reason) { if (mine !== generation) return; running = false; fn && fn(status, reason); }

        async function poll(mine) {
            if (mine !== generation || !running || inFlight) return;
            if (hidden()) { schedule(mine); return; }
            inFlight = true;
            const mineController = makeController(); controller = mineController;
            let status = null, failed = false;
            try { status = await o.fetchStatus(mineController.signal); }
            catch { failed = true; }
            if (mine !== generation) return;   // superseded while waiting: the newer loop owns the UI
            inFlight = false; controller = null; polls++;
            if (failed || !status || status.available === false) {
                errors++;
                if (errors >= o.maxErrors) return finish(mine, o.onGiveUp, null, 'error');
                if (polls >= o.maxPolls || now() - startedAt >= o.maxMs) return finish(mine, o.onGiveUp, null, 'timeout');
                return schedule(mine);
            }
            errors = 0;
            if (status.active) seenActive = true;
            o.onStatus && o.onStatus(status);
            if (mine !== generation) return;
            const inGrace = expectActive && !seenActive && now() - startedAt < o.graceMs;
            if (!status.active && !inGrace) return finish(mine, o.onDone, status);
            if (polls >= o.maxPolls || now() - startedAt >= o.maxMs) return finish(mine, o.onGiveUp, status, 'timeout');
            schedule(mine);
        }

        return {
            start(opts) {
                halt();
                running = true; polls = 0; errors = 0; seenActive = false; expectActive = !!(opts && opts.expectActive); startedAt = now();
                schedule(generation);
            },
            stop: halt,
            isRunning: () => running,
            stats: () => ({ polls, errors, running, inFlight })
        };
    }
    return { create, DEFAULTS };
});
