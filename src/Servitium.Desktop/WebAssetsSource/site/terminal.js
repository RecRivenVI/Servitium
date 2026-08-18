"use strict";

const terminal = new Terminal({ convertEol: true, cursorBlink: true, fontFamily: "Cascadia Mono, Consolas, monospace", fontSize: 14, scrollback: 5000 });
const fit = new FitAddon.FitAddon();
const search = new SearchAddon.SearchAddon();
terminal.loadAddon(fit);
terminal.loadAddon(search);
terminal.open(document.getElementById("terminal"));
try { terminal.loadAddon(new WebglAddon.WebglAddon()); } catch { /* Canvas renderer remains active. */ }
fit.fit();

const bottom = document.getElementById("bottom");
const searchBox = document.getElementById("search");
const history = [];
let historyIndex = 0;

terminal.onData(text => chrome.webview.postMessage({ type: "terminal.input", text }));
terminal.onScroll(() => { bottom.hidden = terminal.buffer.active.viewportY >= terminal.buffer.active.baseY; });
terminal.attachCustomKeyEventHandler(event => {
  if (event.type !== "keydown") return true;
  if (event.ctrlKey && event.key.toLowerCase() === "f") { searchBox.focus(); return false; }
  if (event.key === "Enter") { history.push(currentInputLine()); historyIndex = history.length; }
  if (event.key === "ArrowUp" && historyIndex > 0) { replaceInputLine(history[--historyIndex]); return false; }
  if (event.key === "ArrowDown" && historyIndex < history.length - 1) { replaceInputLine(history[++historyIndex]); return false; }
  return true;
});

document.getElementById("find").addEventListener("click", () => search.findNext(searchBox.value));
searchBox.addEventListener("keydown", event => { if (event.key === "Enter") search.findNext(searchBox.value); });
bottom.addEventListener("click", () => { terminal.scrollToBottom(); bottom.hidden = true; });
document.getElementById("clear").addEventListener("click", () => terminal.clear());
document.getElementById("copy").addEventListener("click", async () => navigator.clipboard.writeText(bufferText()));
document.getElementById("export").addEventListener("click", () => chrome.webview.postMessage({ type: "terminal.export", content: bufferText() }));

chrome.webview.addEventListener("message", event => {
  if (event.data.type === "terminal.output") {
    const value = event.data.payload;
    if (value.snapshot) terminal.reset();
    if (value.truncated) terminal.writeln("\r\n[… output truncated …]\r\n");
    terminal.write(value.text);
  } else if (event.data.type === "theme.changed") {
    terminal.options.theme = event.data.payload.theme === "light"
      ? { background: "#ffffff", foreground: "#1f1f1f" }
      : { background: "#0c0c0c", foreground: "#f2f2f2" };
  } else if (event.data.type === "terminal.settings") {
    const value = Number(event.data.payload.fontSize);
    if (Number.isFinite(value) && value >= 12 && value <= 20) terminal.options.fontSize = value;
    fit.fit();
  }
});

new ResizeObserver(() => { fit.fit(); chrome.webview.postMessage({ type: "terminal.resize", columns: terminal.cols, rows: terminal.rows }); }).observe(document.getElementById("terminal"));

function bufferText() {
  const buffer = terminal.buffer.active;
  const lines = [];
  for (let index = 0; index < buffer.length; index++) lines.push(buffer.getLine(index)?.translateToString(true) ?? "");
  return lines.join("\r\n");
}

function currentInputLine() { return terminal.buffer.active.getLine(terminal.buffer.active.cursorY + terminal.buffer.active.baseY)?.translateToString(true) ?? ""; }
function replaceInputLine(value) { terminal.write("\u001b[2K\r" + value); }
