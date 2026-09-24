// Stage 6B: executes Assets/JobInterview/Plugins/WebGL/RecursorInterviewBridge.jslib under small
// Emscripten-style shims (the .jslib cannot run inside the Unity Editor).
// Run from the repository root with Node's built-in runner (no npm packages):
//   node --test Tests/WebGL/recursor-interview-bridge.test.mjs
// Lives outside Assets/, so Unity never imports or ships it.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const source = readFileSync(
    path.resolve(here, "../../Assets/JobInterview/Plugins/WebGL/RecursorInterviewBridge.jslib"), "utf8");

const HOST = "https://interview-host.example";

function load({ framed = true, hash = `#parentOrigin=${encodeURIComponent(HOST)}&bridgeVersion=1` } = {}) {
    const memory = new Map();
    let nextPointer = 1;
    const listeners = new Set();
    const parentPosts = [];
    const parent = { postMessage: (message, targetOrigin) => parentPosts.push({ message, targetOrigin }) };
    const win = {
        location: { hash },
        addEventListener: (type, fn) => { if (type === "message") listeners.add(fn); },
        removeEventListener: (type, fn) => { if (type === "message") listeners.delete(fn); },
    };
    win.parent = framed ? parent : win;

    const library = {};
    const shims = {
        window: win,
        LibraryManager: { library },
        mergeInto: (target, plugin) => Object.assign(target, plugin),
        autoAddDeps: () => { },
        UTF8ToString: (pointer) => memory.get(pointer),
        lengthBytesUTF8: (text) => Buffer.byteLength(text, "utf8"),
        _malloc: () => nextPointer++,
        _free: (pointer) => memory.delete(pointer),
        stringToUTF8: (text, pointer) => memory.set(pointer, text),
        __dynCall: (callback) => (pointer) => callback(pointer),
    };

    // Emscripten expands the makeDynCall macro at link time; substitute a direct call.
    const js = source.replace(/\{\{\{\s*makeDynCall\('vi',\s*'([^']+)'\)\s*\}\}\}/g, "__dynCall($1)");
    const names = Object.keys(shims);
    new Function(...names, js)(...names.map((n) => shims[n]));

    // Emscripten exposes $-prefixed library members as globals for the other members.
    const globals = {
        RecursorInterviewBridgeState: library.$RecursorInterviewBridgeState,
        RecursorInterviewBridgeReadFragment: library.$RecursorInterviewBridgeReadFragment,
    };
    const bind = (fn) => (...args) => {
        const saved = {};
        for (const [k, v] of Object.entries({ ...shims, ...globals })) { saved[k] = globalThis[k]; globalThis[k] = v; }
        try { return fn(...args); } finally { for (const k of Object.keys(saved)) globalThis[k] = saved[k]; }
    };

    const str = (text) => { const p = nextPointer++; memory.set(p, text); return p; };
    const received = [];
    const callback = (pointer) => received.push(memory.get(pointer));

    return {
        enable: (origin = HOST, version = 1) => bind(library.RecursorInterviewBridge_Enable)(str(origin), version, callback),
        post: (json) => bind(library.RecursorInterviewBridge_Post)(str(json)),
        disable: () => bind(library.RecursorInterviewBridge_Disable)(),
        dispatch: (event) => { for (const fn of [...listeners]) bind(fn)(event); },
        parent, win, listeners, parentPosts, received,
    };
}

test("enables only when framed and the fragment matches the configured origin and version exactly", () => {
    assert.equal(load().enable(), 1);
    assert.equal(load({ framed: false }).enable(), 0);
    assert.equal(load().enable("https://other.example"), 0);
    assert.equal(load().enable(HOST, 2), 0);
    assert.equal(load().enable(""), 0);

    for (const hash of [
        "", "#", `#parentOrigin=${HOST}`, "#bridgeVersion=1",
        `#parentOrigin=${HOST}&bridgeVersion=1&extra=1`,
        `#parentOrigin=${HOST}&parentOrigin=${HOST}`,
        `#parentOrigin=${HOST}/&bridgeVersion=1`,
        `#parentOrigin=${HOST.toUpperCase()}&bridgeVersion=1`,
        `#parentOrigin=%E0%A4%A&bridgeVersion=1`,
        `#parentorigin=${HOST}&bridgeVersion=1`,
    ]) {
        const bridge = load({ hash });
        assert.equal(bridge.enable(), 0, hash);
        assert.equal(bridge.listeners.size, 0, hash);
    }
});

test("relays only plain-object messages from the parent window at the exact origin", () => {
    const b = load();
    b.enable();
    const good = { protocol: "recursor.interview.bridge", version: 1, type: "bridge.ready", correlationId: "corr-0001", payload: { hostState: "ready" } };

    b.dispatch({ source: {}, origin: HOST, data: good });                       // another window
    b.dispatch({ source: b.parent, origin: "https://evil.example", data: good });
    b.dispatch({ source: b.parent, origin: `${HOST}/`, data: good });
    b.dispatch({ source: b.parent, origin: "null", data: good });
    b.dispatch({ source: b.parent, origin: HOST, data: JSON.stringify(good) });  // a string
    b.dispatch({ source: b.parent, origin: HOST, data: [good] });
    b.dispatch({ source: b.parent, origin: HOST, data: null });
    b.dispatch({ source: b.parent, origin: HOST, data: { big: "x".repeat(20000) } });
    assert.equal(b.received.length, 0);

    b.dispatch({ source: b.parent, origin: HOST, data: good });
    assert.deepEqual(b.received.map((j) => JSON.parse(j)), [good]);
});

test("posts a parsed object only to the configured origin, never a wildcard", () => {
    const b = load();
    assert.equal(b.post('{"a":1}'), 0, "nothing before enable");

    b.enable();
    assert.equal(b.post('{"protocol":"recursor.interview.bridge","version":1}'), 1);
    assert.equal(b.post("not json"), 0);
    assert.equal(b.post('"a string"'), 0);
    assert.equal(b.post("[1]"), 0);
    assert.equal(b.post("null"), 0);

    assert.equal(b.parentPosts.length, 1);
    assert.deepEqual(b.parentPosts[0].message, { protocol: "recursor.interview.bridge", version: 1 });
    assert.equal(b.parentPosts[0].targetOrigin, HOST);
    assert.notEqual(b.parentPosts[0].targetOrigin, "*");
});

test("disable removes the listener and stops posting", () => {
    const b = load();
    b.enable();
    assert.equal(b.listeners.size, 1);

    b.disable();
    assert.equal(b.listeners.size, 0);
    assert.equal(b.post('{"a":1}'), 0);
    assert.equal(b.enable(), 1, "can be enabled again");
});

test("the plugin never writes to the console", () => {
    const calls = [];
    const original = { log: console.log, warn: console.warn, error: console.error, info: console.info, debug: console.debug };
    for (const k of Object.keys(original)) console[k] = (...a) => calls.push(a);
    try {
        const b = load();
        b.enable();
        b.dispatch({ source: b.parent, origin: HOST, data: { answerText: "SENTINEL" } });
        b.post("not json");
        b.post('{"answerText":"SENTINEL"}');
    } finally {
        Object.assign(console, original);
    }
    assert.equal(calls.length, 0);
});
