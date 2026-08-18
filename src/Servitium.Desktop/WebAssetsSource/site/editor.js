"use strict";

self.MonacoEnvironment = { getWorkerUrl: () => "https://servitium.local/vendor/monaco/vs/base/worker/workerMain.js" };
require.config({ paths: { vs: "vendor/monaco/vs" } });
require(["vs/editor/editor.main"], () => {
  let dirty = false;
  let loading = false;
  const editor = monaco.editor.create(document.getElementById("editor"), {
    value: "",
    language: "plaintext",
    automaticLayout: true,
    lineNumbers: "on",
    renderLineHighlight: "all",
    guides: { indentation: true },
    minimap: { enabled: false },
    scrollBeyondLastLine: false
  });

  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, save);
  editor.onDidChangeModelContent(() => {
    if (loading) return;
    dirty = true;
    document.getElementById("dirty").textContent = "Modified";
    chrome.webview.postMessage({ type: "editor.dirty", dirty: true, content: editor.getValue() });
  });
  document.getElementById("eol").addEventListener("click", () => {
    const model = editor.getModel();
    const next = model.getEOL() === "\r\n" ? monaco.editor.EndOfLineSequence.LF : monaco.editor.EndOfLineSequence.CRLF;
    model.pushEOL(next);
    updateEol(model);
  });
  chrome.webview.addEventListener("message", event => {
    if (event.data.type === "editor.load") {
      const value = event.data.payload;
      const model = editor.getModel();
      monaco.editor.setModelLanguage(model, value.language || "plaintext");
      editor.updateOptions({ readOnly: Boolean(value.readOnly) });
      loading = true;
      editor.setValue(value.content || "");
      loading = false;
      dirty = false;
      document.getElementById("dirty").textContent = "";
      updateEol(model);
      chrome.webview.postMessage({ type: "editor.dirty", dirty: false, content: editor.getValue() });
    } else if (event.data.type === "theme.changed") {
      monaco.editor.setTheme(event.data.payload.theme === "light" ? "vs" : "vs-dark");
    }
  });

  function save() {
    dirty = false;
    document.getElementById("dirty").textContent = "";
    chrome.webview.postMessage({ type: "editor.save", dirty: false, content: editor.getValue() });
  }
  function updateEol(model) { document.getElementById("eol").textContent = model.getEOL() === "\r\n" ? "CRLF" : "LF"; }
});
