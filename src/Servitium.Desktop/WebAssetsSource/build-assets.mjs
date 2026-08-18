import { cp, mkdir } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const sourceRoot = dirname(fileURLToPath(import.meta.url));
const outputRoot = resolve(process.argv[2] ?? join(sourceRoot, "..", "obj", "WebAssets"));
const expectedParent = resolve(sourceRoot, "../../..");

if (!outputRoot.startsWith(expectedParent + "\\") && !outputRoot.startsWith(expectedParent + "/")) {
  throw new Error("Desktop WebAssets output must stay inside the Servitium repository.");
}

await mkdir(outputRoot, { recursive: true });
await cp(join(sourceRoot, "site"), outputRoot, { recursive: true, force: true });
await copy("@xterm/xterm/lib/xterm.js", "vendor/xterm/xterm.js");
await copy("@xterm/xterm/css/xterm.css", "vendor/xterm/xterm.css");
await copy("@xterm/addon-fit/lib/addon-fit.js", "vendor/xterm/addon-fit.js");
await copy("@xterm/addon-search/lib/addon-search.js", "vendor/xterm/addon-search.js");
await copy("@xterm/addon-webgl/lib/addon-webgl.js", "vendor/xterm/addon-webgl.js");
await cp(join(sourceRoot, "node_modules", "monaco-editor", "min", "vs"), join(outputRoot, "vendor", "monaco", "vs"), { recursive: true, force: true });

async function copy(source, destination) {
  const target = join(outputRoot, destination);
  await mkdir(dirname(target), { recursive: true });
  await cp(join(sourceRoot, "node_modules", source), target, { force: true });
}
