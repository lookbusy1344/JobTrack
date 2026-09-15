// Passkey enrolment ceremony (ADR 0071 §8.1/§8.2). No account policy lives here: the server
// validated the name and generated the options; this only drives the browser WebAuthn call and posts
// the result back. Loaded on /Account/Security under CSP script-src 'self'.
(function () {
    "use strict";

    function base64urlToBytes(value) {
        var padded = value.replace(/-/g, "+").replace(/_/g, "/");
        while (padded.length % 4 !== 0) {
            padded += "=";
        }
        var binary = atob(padded);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        return bytes.buffer;
    }

    function bytesToBase64url(buffer) {
        var bytes = new Uint8Array(buffer);
        var binary = "";
        for (var i = 0; i < bytes.length; i++) {
            binary += String.fromCharCode(bytes[i]);
        }
        return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }

    // Fallback for engines without PublicKeyCredential.parseCreationOptionsFromJSON: decode the
    // base64url fields the JSON model uses into the ArrayBuffers navigator.credentials.create needs.
    function optionsFromJson(json) {
        if (typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function") {
            return PublicKeyCredential.parseCreationOptionsFromJSON(json);
        }
        var options = Object.assign({}, json);
        options.challenge = base64urlToBytes(json.challenge);
        options.user = Object.assign({}, json.user, {id: base64urlToBytes(json.user.id)});
        if (Array.isArray(json.excludeCredentials)) {
            options.excludeCredentials = json.excludeCredentials.map(function (credential) {
                return Object.assign({}, credential, {id: base64urlToBytes(credential.id)});
            });
        }
        return options;
    }

    // Fallback for credentials without a correct toJSON: build the registration response JSON by hand.
    function credentialToJson(credential) {
        if (typeof credential.toJSON === "function") {
            return credential.toJSON();
        }
        var response = credential.response;
        var json = {
            id: credential.id,
            rawId: bytesToBase64url(credential.rawId),
            type: credential.type,
            clientExtensionResults: credential.getClientExtensionResults ? credential.getClientExtensionResults() : {},
            response: {
                clientDataJSON: bytesToBase64url(response.clientDataJSON),
                attestationObject: bytesToBase64url(response.attestationObject)
            }
        };
        if (typeof response.getTransports === "function") {
            json.response.transports = response.getTransports();
        }
        if (credential.authenticatorAttachment) {
            json.authenticatorAttachment = credential.authenticatorAttachment;
        }
        return json;
    }

    function antiforgeryToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    function showError(message) {
        var element = document.querySelector("[data-passkey-error]");
        if (element) {
            element.textContent = message;
            element.hidden = false;
        }
    }

    async function runCeremony(ceremony) {
        var options;
        try {
            options = optionsFromJson(JSON.parse(ceremony.dataset.creationOptions));
        } catch (parseError) {
            showError("That passkey could not be added. Try again.");
            return;
        }

        var credential;
        try {
            credential = await navigator.credentials.create({publicKey: options});
        } catch (ceremonyError) {
            // User cancellation or timeout is not a failure: return to the page so they can retry.
            window.location.assign(window.location.pathname);
            return;
        }

        try {
            var response = await fetch(ceremony.dataset.completeUrl, {
                method: "POST",
                headers: {"Content-Type": "application/json", "X-CSRF-TOKEN": antiforgeryToken()},
                body: JSON.stringify(credentialToJson(credential))
            });
            var result = await response.json();
            if (result && result.redirect) {
                window.location.assign(result.redirect);
                return;
            }
            showError(result && result.error ? result.error : "That passkey could not be added. Try again.");
        } catch (submitError) {
            showError("That passkey could not be added. Try again.");
        }
    }

    // Fallback for engines without PublicKeyCredential.parseRequestOptionsFromJSON.
    function requestOptionsFromJson(json) {
        if (typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function") {
            return PublicKeyCredential.parseRequestOptionsFromJSON(json);
        }
        var options = Object.assign({}, json);
        options.challenge = base64urlToBytes(json.challenge);
        if (Array.isArray(json.allowCredentials)) {
            options.allowCredentials = json.allowCredentials.map(function (credential) {
                return Object.assign({}, credential, {id: base64urlToBytes(credential.id)});
            });
        }
        return options;
    }

    // Fallback for assertions without a correct toJSON.
    function assertionToJson(credential) {
        if (typeof credential.toJSON === "function") {
            return credential.toJSON();
        }
        var response = credential.response;
        var json = {
            id: credential.id,
            rawId: bytesToBase64url(credential.rawId),
            type: credential.type,
            clientExtensionResults: credential.getClientExtensionResults ? credential.getClientExtensionResults() : {},
            response: {
                clientDataJSON: bytesToBase64url(response.clientDataJSON),
                authenticatorData: bytesToBase64url(response.authenticatorData),
                signature: bytesToBase64url(response.signature),
                userHandle: response.userHandle ? bytesToBase64url(response.userHandle) : null
            }
        };
        if (credential.authenticatorAttachment) {
            json.authenticatorAttachment = credential.authenticatorAttachment;
        }
        return json;
    }

    function showLoginError(login, message) {
        var element = login.querySelector("[data-passkey-login-error]");
        if (element) {
            element.textContent = message;
            element.hidden = false;
        }
    }

    function PasskeyOptionsError(message) {
        this.name = "PasskeyOptionsError";
        this.message = message;
    }

    PasskeyOptionsError.prototype = Object.create(Error.prototype);

    async function requestAssertionOptions(login, signal) {
        var response = await fetch(login.dataset.optionsUrl, {
            method: "POST",
            headers: {"X-CSRF-TOKEN": antiforgeryToken()},
            signal: signal
        });
        var result = await response.json();
        if (!response.ok || result.error) {
            throw new PasskeyOptionsError(result.error || "That passkey could not be used to sign in.");
        }
        return requestOptionsFromJson(result);
    }

    async function submitAssertion(login, credential) {
        var response = await fetch(login.dataset.signinUrl, {
            method: "POST",
            headers: {"Content-Type": "application/json", "X-CSRF-TOKEN": antiforgeryToken()},
            body: JSON.stringify(assertionToJson(credential))
        });
        var result = await response.json();
        if (result && result.redirect) {
            window.location.assign(result.redirect);
            return;
        }
        showLoginError(login, result && result.error ? result.error : "That passkey could not be used to sign in.");
    }

    function initialiseLogin(login) {
        // No WebAuthn: leave the password form exactly as it is, enhancement hidden.
        if (!window.PublicKeyCredential || !navigator.credentials || !navigator.credentials.get) {
            return;
        }

        login.hidden = false;
        var conditionalController = null;
        var abortConditional = function () {
            if (conditionalController) {
                conditionalController.abort();
                conditionalController = null;
            }
        };

        var button = login.querySelector("[data-passkey-login-button]");
        if (button) {
            button.addEventListener("click", async function () {
                abortConditional();
                try {
                    var options = await requestAssertionOptions(login);
                    var credential = await navigator.credentials.get({publicKey: options});
                    await submitAssertion(login, credential);
                } catch (error) {
                    if (error instanceof PasskeyOptionsError) {
                        showLoginError(login, error.message);
                    } else if (!error || (error.name !== "NotAllowedError" && error.name !== "AbortError")) {
                        showLoginError(login, "That passkey could not be used to sign in.");
                    }
                }
            });
        }

        // Password submission and the explicit button must not compete with a pending conditional
        // ceremony for the one protected temporary-state cookie.
        var passwordForm = document.querySelector("[data-password-form]");
        if (passwordForm) {
            passwordForm.addEventListener("submit", abortConditional);
        }

        // Conditional autofill pairs with a username field, so it runs only where the page opts in
        // (the login page). On step-up there is no username field; the explicit button is the only path.
        if (login.dataset.conditional === "true" && typeof PublicKeyCredential.isConditionalMediationAvailable === "function") {
            PublicKeyCredential.isConditionalMediationAvailable().then(async function (available) {
                if (!available) {
                    return;
                }
                try {
                    conditionalController = new AbortController();
                    var options = await requestAssertionOptions(login, conditionalController.signal);
                    var credential = await navigator.credentials.get({
                        publicKey: options,
                        mediation: "conditional",
                        signal: conditionalController.signal
                    });
                    await submitAssertion(login, credential);
                } catch (error) {
                    // Aborted or cancelled: stay silent and leave the password form usable.
                }
            });
        }
    }

    function initialiseEnrolment() {
        var form = document.querySelector("[data-passkey-add-form]");
        if (!form) {
            return;
        }

        if (!window.PublicKeyCredential || !navigator.credentials || !navigator.credentials.create) {
            var unsupported = form.querySelector("[data-passkey-unsupported]");
            if (unsupported) {
                unsupported.hidden = false;
            }
            var submit = form.querySelector('button[type="submit"]');
            if (submit) {
                submit.disabled = true;
            }
            return;
        }

        var ceremony = document.querySelector("[data-passkey-ceremony]");
        if (ceremony) {
            runCeremony(ceremony);
        }
    }

    function initialise() {
        initialiseEnrolment();

        var login = document.querySelector("[data-passkey-login]");
        if (login) {
            initialiseLogin(login);
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initialise);
    } else {
        initialise();
    }
})();
