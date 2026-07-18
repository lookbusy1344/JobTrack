// Client-side "recently visited" job history for Jobs/Browse. Never touches the server -- purely
// a browsing convenience, so any storage failure (private browsing, disabled storage) degrades to
// simply not showing history rather than breaking the page.
(function () {
    "use strict";

    var STORAGE_KEY = "jobtrack.history.v1";
    var HISTORY_CAP = 20;

    var missingNodeContainer = document.querySelector("[data-jt-missing-node-id]");
    if (missingNodeContainer) {
        var missingNodeId = missingNodeContainer.getAttribute("data-jt-missing-node-id");
        saveHistory(loadHistory().filter(function (entry) {
            return entry.id !== missingNodeId;
        }));
    }

    var dataContainer = document.querySelector("[data-jt-node-id]");
    var listElement = document.getElementById("jt-history-list");
    if (!dataContainer || !listElement) {
        return;
    }

    var currentNode = {
        id: dataContainer.getAttribute("data-jt-node-id"),
        description: dataContainer.getAttribute("data-jt-node-description"),
        kind: dataContainer.getAttribute("data-jt-node-kind"),
    };

    function loadHistory() {
        try {
            var raw = window.localStorage.getItem(STORAGE_KEY);
            var parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed : [];
        } catch (error) {
            return [];
        }
    }

    function saveHistory(entries) {
        try {
            window.localStorage.setItem(STORAGE_KEY, JSON.stringify(entries));
        } catch (error) {
            // Storage unavailable -- history just won't persist across navigations.
        }
    }

    function renderHistory(entries) {
        listElement.replaceChildren();

        var visibleEntries = entries.filter(function (entry) {
            return entry.id !== currentNode.id;
        });

        if (visibleEntries.length === 0) {
            var emptyItem = document.createElement("li");
            emptyItem.className = "jt-history-empty";
            emptyItem.textContent = "None yet.";
            listElement.appendChild(emptyItem);
            return;
        }

        visibleEntries.forEach(function (entry) {
            var listItem = document.createElement("li");
            var link = document.createElement("a");
            link.href = "/Jobs/Browse?nodeId=" + encodeURIComponent(entry.id);
            link.className = "jt-preserve-whitespace";
            link.textContent = entry.description + " (ID " + entry.id + ")";
            listItem.appendChild(link);
            listElement.appendChild(listItem);
        });
    }

    var history = loadHistory();
    renderHistory(history);

    var withoutCurrent = history.filter(function (entry) {
        return entry.id !== currentNode.id;
    });
    var updated = [currentNode].concat(withoutCurrent).slice(0, HISTORY_CAP);
    saveHistory(updated);
})();
