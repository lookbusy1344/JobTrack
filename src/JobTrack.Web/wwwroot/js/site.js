// Gate navigation for elements that carry a confirmation message in data-jt-confirm.
document.addEventListener('click', (event) => {
    const target = event.target.closest('[data-jt-confirm]');
    if (!target) {
        return;
    }

    const message = target.getAttribute('data-jt-confirm');
    if (!message || window.confirm(message)) {
        return;
    }

    event.preventDefault();
});

// Toggle a backdate popover (data-jt-disclosure-toggle="<target id>") open/closed. The panel floats
// over the page from the trigger's own position (`.jt-backdate-panel--floating`, position: fixed)
// rather than pushing surrounding content down, so on first open it is moved to the end of <body> --
// this also escapes any ancestor's own overflow clipping (e.g. the Browse row-height clamp's
// `overflow: hidden`), which a plain `position: absolute` panel nested inside the table could not. aria-controls/
// aria-expanded associate trigger and panel by id, which keeps working regardless of where the panel
// ends up in the DOM. Start-for uses native details/summary instead of this hook entirely, so its
// complete form remains usable without JavaScript; its panel floats via plain CSS positioning
// against its own <details> element, and is pinned to the viewport by this same positioning function
// when JavaScript is available (see pinStartForPanel below and _StartForTrigger.cshtml).
function positionFloatingPopover(panel, trigger) {
    const margin = 8;
    const triggerRect = trigger.getBoundingClientRect();
    const panelRect = panel.getBoundingClientRect();

    let left = triggerRect.right - panelRect.width;
    left = Math.max(margin, Math.min(left, window.innerWidth - panelRect.width - margin));

    let top = triggerRect.bottom + margin;
    if (top + panelRect.height > window.innerHeight - margin) {
        top = Math.max(margin, triggerRect.top - panelRect.height - margin);
    }

    panel.style.left = `${left}px`;
    panel.style.top = `${top}px`;
}

function closeFloatingPopover(panel, trigger) {
    panel.setAttribute('hidden', '');
    trigger.setAttribute('aria-expanded', 'false');
    document.removeEventListener('click', panel._jtOutsideClick, true);
    document.removeEventListener('keydown', panel._jtEscape, true);
    window.removeEventListener('scroll', panel._jtReposition, true);
    window.removeEventListener('resize', panel._jtReposition);
}

document.addEventListener('click', (event) => {
    const trigger = event.target.closest('[data-jt-disclosure-toggle]');
    if (!trigger) {
        return;
    }

    const target = document.getElementById(trigger.getAttribute('data-jt-disclosure-toggle'));
    if (!target) {
        return;
    }

    if (!target.hasAttribute('hidden')) {
        closeFloatingPopover(target, trigger);
        return;
    }

    if (target.parentElement !== document.body) {
        document.body.appendChild(target);
    }

    // Start-for's native <details> and this floating popover are independent disclosures that can
    // otherwise both end up open and overlapping (they share the same z-index) -- opening one closes
    // the other so only one popup is ever showing.
    document.querySelectorAll('.jt-start-for-disclosure[open]').forEach((details) => details.removeAttribute('open'));

    target.removeAttribute('hidden');
    trigger.setAttribute('aria-expanded', 'true');
    positionFloatingPopover(target, trigger);
    // Deliberately not auto-focusing the datetime-local field here: focusing it immediately pops the
    // native date/time picker on mobile browsers before the user has read the panel, which reads as
    // the page misbehaving rather than as a helpful default.

    target._jtReposition = () => positionFloatingPopover(target, trigger);
    target._jtOutsideClick = (outsideEvent) => {
        if (!target.contains(outsideEvent.target) && !trigger.contains(outsideEvent.target)) {
            closeFloatingPopover(target, trigger);
        }
    };
    target._jtEscape = (keyEvent) => {
        if (keyEvent.key === 'Escape') {
            closeFloatingPopover(target, trigger);
            trigger.focus();
        }
    };

    window.addEventListener('scroll', target._jtReposition, true);
    window.addEventListener('resize', target._jtReposition);
    document.addEventListener('click', target._jtOutsideClick, true);
    document.addEventListener('keydown', target._jtEscape, true);
});

// Pin an open Start-for panel to the viewport at coordinates computed from its own <summary>. The
// panel stays where it is in the DOM (moving it would break the native details/summary disclosure
// relationship assistive tech relies on) -- only its positioning scheme changes, from `absolute`
// against its <details> to `fixed`. That matters for the last row of a table: an absolutely
// positioned box inside a table does not extend the document's scrollable area, so a panel hanging
// below the final row cannot be scrolled to at all. Without JavaScript the plain CSS anchoring still
// applies (see _StartForTrigger.cshtml), so the form is never unreachable.
function pinStartForPanel(details) {
    const panel = details.querySelector('.jt-backdate-panel');
    const summary = details.querySelector('summary');
    if (!panel || !summary) {
        return;
    }

    panel.classList.add('jt-backdate-panel--pinned');
    panel._jtReposition = () => positionFloatingPopover(panel, summary);
    panel._jtReposition();

    window.addEventListener('scroll', panel._jtReposition, true);
    window.addEventListener('resize', panel._jtReposition);
}

function unpinStartForPanel(details) {
    const panel = details.querySelector('.jt-backdate-panel');
    if (!panel) {
        return;
    }

    panel.classList.remove('jt-backdate-panel--pinned');
    panel.style.removeProperty('top');
    panel.style.removeProperty('left');
    window.removeEventListener('scroll', panel._jtReposition, true);
    window.removeEventListener('resize', panel._jtReposition);
}

// Opening a Start-for <details> pins its panel (above) and closes any open floating backdate popover,
// so the two disclosure mechanisms never show at once -- the reverse of the exclusion above. The
// 'toggle' event doesn't bubble, but a capturing listener on document still sees it on its way down
// to the target.
document.addEventListener('toggle', (event) => {
    const details = event.target;
    if (!(details instanceof HTMLDetailsElement) || !details.classList.contains('jt-start-for-disclosure')) {
        return;
    }

    if (!details.open) {
        unpinStartForPanel(details);
        return;
    }

    pinStartForPanel(details);
    document.querySelectorAll('.jt-backdate-panel--floating:not([hidden])').forEach((panel) => {
        const trigger = document.querySelector(`[data-jt-disclosure-toggle="${panel.id}"]`);
        if (trigger) {
            closeFloatingPopover(panel, trigger);
        }
    });
}, true);

// Recent-job history moved to principal-bound protected state. Remove the legacy origin-global
// payload on every page load so descriptions retained by an older deployment cannot survive until
// another account uses this browser.
const JT_HISTORY_STORAGE_KEY = 'jobtrack.history.v1';

try {
    window.localStorage.removeItem(JT_HISTORY_STORAGE_KEY);
} catch (error) {
    // Storage unavailable -- nothing to migrate.
}
