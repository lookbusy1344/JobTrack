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
