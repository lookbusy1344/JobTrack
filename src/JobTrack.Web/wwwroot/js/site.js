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

// Toggle a disclosure row/panel (data-jt-disclosure-toggle="<target id>") open/closed, in place of
// the old <details> disclosure -- a <details> can't legally contain a <tr>, so the row/panel
// presentation needs its own show/hide instead of relying on the element's native open state. Shared
// by the backdate disclosure (_BackdateTrigger/_BackdateRow/_BackdatePanel). Start-for uses native
// details/summary instead so its complete form remains usable without JavaScript.
document.addEventListener('click', (event) => {
    const trigger = event.target.closest('[data-jt-disclosure-toggle]');
    if (!trigger) {
        return;
    }

    const target = document.getElementById(trigger.getAttribute('data-jt-disclosure-toggle'));
    if (!target) {
        return;
    }

    const wasHidden = target.hasAttribute('hidden');
    target.toggleAttribute('hidden', !wasHidden);
    trigger.setAttribute('aria-expanded', String(wasHidden));

    if (wasHidden) {
        target.querySelector('input[type="datetime-local"], select, input[type="text"]')?.focus();
    }
});

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
