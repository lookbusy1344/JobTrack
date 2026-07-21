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
