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
terminal.attachCustomKeyEventHandler(event => {
    if (!event.metaKey) return true;
    if (event.type !== "keydown") return false;
    switch (event.key.toLowerCase()) {
        case "c": post("copy", {text: terminal.getSelection()}); event.preventDefault(); break;
        case "v": post("paste"); event.preventDefault(); break;
        case "a": terminal.selectAll(); event.preventDefault(); break;
    }
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
window.pasteTerminal = text => terminal.paste(text);
window.focusTerminal = () => { fit.fit(); terminal.focus(); };
fit.fit();
post("ready", {cols: terminal.cols, rows: terminal.rows});
