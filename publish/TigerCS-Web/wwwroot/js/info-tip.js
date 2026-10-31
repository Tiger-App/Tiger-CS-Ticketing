/*
  Info tips (Pages/Shared/_InfoTip.cshtml). The explanation text is already in the page; this script only adds what CSS cannot:
  - tap / click / Enter / Space on the icon pins the bubble open (one at a time) and keeps aria-expanded in step;
  - Escape closes it; a tap outside, or Tab leaving the icon, closes it;
  - the bubble is placed (position: fixed) next to its icon, flipped above when there is no room below and clamped inside the viewport, so long text is never clipped.
  Everything is delegated from the document, so tips inside the asynchronously reloaded Campaigns results work without re-binding.
*/
(() => {
    const MARGIN = 8, GAP = 8;
    const TIP = '[data-info-tip]';
    let open = null;

    const part = (tip, selector) => tip.querySelector(selector);
    const tipOf = target => (target instanceof Element ? target.closest(TIP) : null);

    // Hover and focus show the bubble by CSS; only its position needs the script.
    const place = tip => {
        const bubble = part(tip, '[data-info-tip-content]'), icon = part(tip, '[data-info-tip-button]');
        if (!bubble || !icon) return;
        const anchor = icon.getBoundingClientRect();
        const width = bubble.offsetWidth, height = bubble.offsetHeight;
        const viewWidth = document.documentElement.clientWidth, viewHeight = window.innerHeight;
        const left = Math.max(MARGIN, Math.min(anchor.left + anchor.width / 2 - width / 2, viewWidth - width - MARGIN));
        const below = anchor.bottom + GAP, above = anchor.top - GAP - height;
        const top = below + height <= viewHeight - MARGIN || above < MARGIN ? below : above;
        bubble.style.setProperty('left', `${Math.round(left)}px`);
        bubble.style.setProperty('top', `${Math.round(Math.max(MARGIN, top))}px`);
    };
    const isShown = tip => tip.classList.contains('is-open') || tip.matches(':hover') || !!part(tip, '[data-info-tip-button]:focus-visible');

    const setOpen = (tip, value) => {
        tip.classList.toggle('is-open', value);
        const icon = part(tip, '[data-info-tip-button]');
        if (icon) icon.setAttribute('aria-expanded', String(value));
        if (value) { tip.classList.remove('is-dismissed'); place(tip); open = tip; }
        else if (open === tip) open = null;
    };
    const closeOpen = except => { if (open && open !== except) setOpen(open, false); };

    document.addEventListener('click', event => {
        const icon = event.target instanceof Element ? event.target.closest('[data-info-tip-button]') : null;
        if (!icon) { if (!tipOf(event.target)) closeOpen(null); return; }       // a tap outside closes; a tap inside the bubble keeps it (text can be selected)
        const tip = tipOf(icon);
        event.preventDefault();                                                // the icon may sit inside a label or form; it never submits or toggles anything
        closeOpen(tip);
        setOpen(tip, !tip.classList.contains('is-open'));
    });
    document.addEventListener('mouseover', event => { const tip = tipOf(event.target); if (tip) place(tip); });
    document.addEventListener('focusin', event => { const tip = tipOf(event.target); if (tip) place(tip); });
    document.addEventListener('mouseout', event => { const tip = tipOf(event.target); if (tip && !tip.contains(event.relatedTarget)) tip.classList.remove('is-dismissed'); });
    document.addEventListener('focusout', event => {
        const tip = tipOf(event.target);
        if (!tip || tip.contains(event.relatedTarget)) return;
        tip.classList.remove('is-dismissed');
        if (tip.classList.contains('is-open')) setOpen(tip, false);
    });
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        const shown = [...document.querySelectorAll(TIP)].filter(isShown);
        shown.forEach(tip => { if (tip.classList.contains('is-open')) setOpen(tip, false); tip.classList.add('is-dismissed'); });
    }, true);

    let frame = 0;
    const reposition = () => {
        if (frame) return;
        frame = window.requestAnimationFrame(() => { frame = 0; if (open && !open.isConnected) open = null; document.querySelectorAll(TIP).forEach(tip => { if (isShown(tip)) place(tip); }); });
    };
    window.addEventListener('resize', reposition);
    window.addEventListener('scroll', reposition, true);
})();
