"use strict";

const post = (type, fields = {}) => window.webkit.messageHandlers.terminal.postMessage({type, ...fields});
const element = document.getElementById("terminal");
const terminal = new Terminal({
    fontFamily: "Menlo, monospace", fontSize: 13, cursorBlink: false,
    scrollback: 10000, minimumContrastRatio: 4.5,
    theme: { background: "#121823", foreground: "#E2E8F3", cursor: "#72E0B5", selectionBackground: "#36596C" }
});
const fit = new FitAddon.FitAddon();
terminal.loadAddon(fit);
terminal.open(element);
element.addEventListener("mousedown", event => {
    const secondaryClick = event.button === 2 || (event.button === 0 && event.ctrlKey);
    if (!secondaryClick || (terminal.modes.mouseTrackingMode !== "none" && !event.shiftKey)) return;
    event.preventDefault();
    event.stopPropagation();
    terminal.focus();
    post("paste");
}, {capture: true});
element.addEventListener("contextmenu", event => event.preventDefault());
element.addEventListener("paste", event => {
    event.preventDefault();
    event.stopPropagation();
    post("paste");
}, {capture: true});
const copySelection = () => post("copy", {text: terminal.getSelection()});
terminal.attachCustomKeyEventHandler(event => {
    if (event.type !== "keydown" || event.altKey) return true;
    const typed = event.key.toLowerCase();
    const key = /^[a-z]$/.test(typed) ? typed : ({KeyA: "a", KeyC: "c", KeyV: "v"}[event.code] || typed);
    const command = event.metaKey && !event.ctrlKey && !event.shiftKey;
    const control = event.ctrlKey && !event.metaKey;
    const copy = (key === "c" && (command || (control && (event.shiftKey || terminal.hasSelection())))) ||
        (key === "insert" && control && !event.shiftKey);
    const paste = (key === "v" && (command || (control && event.shiftKey))) ||
        (key === "insert" && event.shiftKey && !event.metaKey && !event.ctrlKey);
    if (copy) copySelection();
    else if (paste) post("paste");
    else if (key === "a" && (command || (control && event.shiftKey))) terminal.selectAll();
    else return true;
    event.preventDefault();
    return false;
});
terminal.onData(text => post("input", {text}));
terminal.onBinary(text => post("binary", {text}));
terminal.onResize(({cols, rows}) => post("resize", {cols, rows}));
new ResizeObserver(() => fit.fit()).observe(element);
window.receive = (encoded, batch) => {
    const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
    terminal.write(bytes, () => post("drained", {batch}));
};
window.copyTerminal = copySelection;
window.selectTerminal = () => terminal.selectAll();
window.focusTerminal = () => { fit.fit(); terminal.focus(); };
fit.fit();
post("ready", {cols: terminal.cols, rows: terminal.rows});
