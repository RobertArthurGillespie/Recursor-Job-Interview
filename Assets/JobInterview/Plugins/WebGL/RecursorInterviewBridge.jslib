// Stage 6B: the Unity (iframe) side of the Stage 6A postMessage bridge.
//
// This file only enforces the browser boundary and relays JSON. It performs no HTTP, never sees
// or stores any credential, and never logs anything: interviewer text and applicant answers must
// not reach browser diagnostics. The C# side (InterviewBridgeMessages) validates every message's
// exact shape.
//
// "Parent origin" means the origin of the Blazor page that embeds this build in an iframe (the
// build's configured allowedHostOrigin). It is NOT this page's own origin, which is wherever the
// Unity files are served (Stage 6A's UnityOrigin) and is usually different.
//
// The listener and outbound messaging are enabled only when ALL of these hold:
//   - the page is framed (window.parent !== window);
//   - the URL fragment is exactly "#parentOrigin=<origin>&bridgeVersion=<version>" (the Stage 6A
//     parent's iframe URL), with <origin> EQUAL to the parent origin configured in the Unity
//     build and <version> equal to the bridge version. The fragment is never trusted by itself.
// Inbound messages are relayed only when event.source is window.parent and event.origin equals
// that parent origin exactly. Outbound messages are posted only to that parent origin, never to
// a wildcard.

var RecursorInterviewBridgePlugin = {
    $RecursorInterviewBridgeState: {
        origin: null,
        listener: null,
        callback: 0,
        maxLength: 16384
    },

    $RecursorInterviewBridgeReadFragment: function () {
        var hash = window.location.hash;
        if (typeof hash !== "string" || hash.charAt(0) !== "#") return null;

        var parts = hash.substring(1).split("&");
        if (parts.length !== 2) return null;

        var values = {};
        for (var i = 0; i < parts.length; i++) {
            var separator = parts[i].indexOf("=");
            if (separator <= 0) return null;

            var key = parts[i].substring(0, separator);
            if (key !== "parentOrigin" && key !== "bridgeVersion") return null;
            if (Object.prototype.hasOwnProperty.call(values, key)) return null;

            try {
                values[key] = decodeURIComponent(parts[i].substring(separator + 1));
            } catch (e) {
                return null;
            }
        }

        if (!Object.prototype.hasOwnProperty.call(values, "parentOrigin") ||
            !Object.prototype.hasOwnProperty.call(values, "bridgeVersion")) return null;

        return values;
    },

    RecursorInterviewBridge_Enable__deps: ["$RecursorInterviewBridgeState", "$RecursorInterviewBridgeReadFragment"],
    RecursorInterviewBridge_Enable: function (expectedOriginPtr, bridgeVersion, callback) {
        var state = RecursorInterviewBridgeState;
        if (state.listener) return 0;
        if (typeof window === "undefined" || !window.parent || window.parent === window) return 0;

        var expectedOrigin = UTF8ToString(expectedOriginPtr);
        if (typeof expectedOrigin !== "string" || expectedOrigin.length === 0) return 0;

        var fragment = RecursorInterviewBridgeReadFragment();
        if (!fragment ||
            fragment.parentOrigin !== expectedOrigin ||
            fragment.bridgeVersion !== String(bridgeVersion)) return 0;

        state.origin = expectedOrigin;
        state.callback = callback;
        state.listener = function (event) {
            if (event.source !== window.parent || event.origin !== state.origin) return;

            var data = event.data;
            if (data === null || typeof data !== "object" || Array.isArray(data)) return;

            var json;
            try {
                json = JSON.stringify(data);
            } catch (e) {
                return;
            }
            if (typeof json !== "string" || json.length > state.maxLength) return;

            var size = lengthBytesUTF8(json) + 1;
            var buffer = _malloc(size);
            try {
                stringToUTF8(json, buffer, size);
                {{{ makeDynCall('vi', 'state.callback') }}}(buffer);
            } finally {
                _free(buffer);
            }
        };

        window.addEventListener("message", state.listener);
        return 1;
    },

    RecursorInterviewBridge_Post__deps: ["$RecursorInterviewBridgeState"],
    RecursorInterviewBridge_Post: function (envelopePtr) {
        var state = RecursorInterviewBridgeState;
        if (!state.listener || typeof state.origin !== "string" || state.origin.length === 0) return 0;

        var message;
        try {
            message = JSON.parse(UTF8ToString(envelopePtr));
        } catch (e) {
            return 0;
        }

        // The Stage 6A host accepts only a plain object, never a JSON string.
        if (message === null || typeof message !== "object" || Array.isArray(message)) return 0;

        window.parent.postMessage(message, state.origin);
        return 1;
    },

    RecursorInterviewBridge_Disable__deps: ["$RecursorInterviewBridgeState"],
    RecursorInterviewBridge_Disable: function () {
        var state = RecursorInterviewBridgeState;
        if (state.listener) window.removeEventListener("message", state.listener);
        state.listener = null;
        state.origin = null;
        state.callback = 0;
    }
};

autoAddDeps(RecursorInterviewBridgePlugin, "$RecursorInterviewBridgeState");
mergeInto(LibraryManager.library, RecursorInterviewBridgePlugin);
