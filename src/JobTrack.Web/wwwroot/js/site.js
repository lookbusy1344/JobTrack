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
// this also escapes any ancestor's own overflow clipping (e.g. `.jt-table-scroll`'s horizontal
// scroll), which a plain `position: absolute` panel nested inside the table could not. aria-controls/
// aria-expanded associate trigger and panel by id, which keeps working regardless of where the panel
// ends up in the DOM. Start-for uses native details/summary instead of this hook entirely, so its
// complete form remains usable without JavaScript; its panel floats via plain CSS positioning
// against its own <details> element (see _StartForTrigger.cshtml).
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

// The reverse of the exclusion above: opening a Start-for <details> closes any open floating
// backdate popover, so the two disclosure mechanisms still never show at once. The 'toggle' event
// doesn't bubble, but a capturing listener on document still sees it on its way down to the target.
document.addEventListener('toggle', (event) => {
    const details = event.target;
    if (!(details instanceof HTMLDetailsElement) || !details.classList.contains('jt-start-for-disclosure') || !details.open) {
        return;
    }

    document.querySelectorAll('.jt-backdate-panel--floating:not([hidden])').forEach((panel) => {
        const trigger = document.querySelector(`[data-jt-disclosure-toggle="${panel.id}"]`);
        if (trigger) {
            closeFloatingPopover(panel, trigger);
        }
    });
}, true);

// Clear client-side "recently visited" job history on sign-out (data-jt-clear-history-on-submit),
// so a stale account's breadcrumbs never leak into the next signed-in session. The storage key must
// match STORAGE_KEY in job-history.js -- that module isn't loaded on every page, so the key is
// duplicated here rather than shared.
const JT_HISTORY_STORAGE_KEY = 'jobtrack.history.v1';

document.addEventListener('submit', (event) => {
    if (!event.target.closest('[data-jt-clear-history-on-submit]')) {
        return;
    }

    try {
        window.localStorage.removeItem(JT_HISTORY_STORAGE_KEY);
    } catch (error) {
        // Storage unavailable -- nothing to clear.
    }
});

// Jobs/Work carries one write-up textarea (#writeUp) shared by several independent action forms --
// Start, Start-for, backdated start, Reopen and start, Change outcome, and each session row's own
// Pause button. Every action must implicitly save whatever write-up text is currently typed, not
// just the one form the textarea happens to live inside. The ending-decision form (Pause/Complete)
// already carries the write-up as part of its own single atomic command, so that case is left alone;
// every other form here fires a separate SaveWriteUp request first, then submits unmodified -- two
// requests, each still a single mutation (an architecture rule Jobs/Work's Razor Page handlers keep
// to), rather than one handler coordinating two. A no-op on every page without a #writeUp textarea,
// and for the one form the textarea already lives in.
document.addEventListener('submit', (event) => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) {
        return;
    }

    const writeUp = document.getElementById('writeUp');
    if (!writeUp || form.contains(writeUp)) {
        return;
    }

    const writeUpForm = writeUp.closest('form');
    const nodeVersion = document.getElementById('writeUpNodeVersion');
    if (!writeUpForm || !nodeVersion) {
        return;
    }

    event.preventDefault();
    saveWriteUpThenSubmit(form, writeUpForm, writeUp.value, nodeVersion.value);
});

async function saveWriteUpThenSubmit(form, writeUpForm, writeUp, nodeVersion) {
    const body = new URLSearchParams({
        LeafNodeId: writeUpForm.elements.namedItem('LeafNodeId')?.value ?? '',
        nodeVersion,
        writeUp,
        __RequestVerificationToken: writeUpForm.elements.namedItem('__RequestVerificationToken')?.value ?? '',
    });

    try {
        await fetch('/Jobs/Work?handler=SaveWriteUp', {
            method: 'POST',
            headers: {'Content-Type': 'application/x-www-form-urlencoded'},
            body,
        });
    } finally {
        form.submit();
    }
}
