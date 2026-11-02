// scripts/screenshot.mjs
// Screenshot tool for the Arena sandbox: launches Chromium from
// `@sparticuz/chromium`, unpacking the NSS libraries bundled in that same
// package so the browser can actually start. Full derivation: SKILL.md.
//
// Usage (run it from a folder that can resolve node_modules):
//   npm i --no-save puppeteer-core @sparticuz/chromium
//   node scripts/screenshot.mjs http://127.0.0.1:5173/ --out shots/home.png
//   node scripts/screenshot.mjs http://127.0.0.1:5173/ --tab Fixer --width 2560 --height 1080
//   node scripts/screenshot.mjs http://127.0.0.1:5173/ --probe   # browser version only
//   node scripts/screenshot.mjs http://127.0.0.1:5173/ --dump     # page text instead of an image
//
// Flags:
//   --out <path>       where to write the PNG (default shot.png)
//   --width/--height   viewport (default 1600x900)
//   --tab <label>      click the tab/button/link whose text matches, then shoot
//   --wait <selector>  selector that means "the app is ready" (default #root > *)
//   --timeout <sec>    how long to wait for it (default 30); a timeout still shoots
//   --query k=v        append a query parameter to the URL (repeatable)
//   --full             capture the full scrollable page
//   --dump             print the page text instead of writing an image
//   --probe            print the browser version and exit
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { brotliDecompressSync } from "node:zlib";

const argv = process.argv.slice(2);
const flag = (name, fallback = null) => {
  const index = argv.indexOf(`--${name}`);
  return index === -1 ? fallback : (argv[index + 1] ?? true);
};
const has = (name) => argv.includes(`--${name}`);
const repeat = (name) => {
  const values = [];
  argv.forEach((arg, index) => {
    if (arg === `--${name}` && argv[index + 1]) values.push(argv[index + 1]);
  });
  return values;
};

const url = argv.find((arg) => arg.startsWith("http")) ?? "http://127.0.0.1:5173/";
const width = Number(flag("width", 1600));
const height = Number(flag("height", 900));
const out = resolve(String(flag("out", "shot.png")));
const tab = flag("tab");
const waitFor = flag("wait");
const probe = has("probe");
const dump = has("dump");
const full = has("full");
const timeoutSeconds = Number(flag("timeout", 30));

/**
 * @sparticuz/chromium ships a browser for Amazon Linux 2023, which — like this
 * sandbox — has no NSS. It therefore bundles the three shared objects the
 * browser needs in `bin/al2023.tar.br`. Unpack them, point the loader at them.
 */
function ensureLibs() {
  if (process.env.EXTRA_LIBS) return process.env.EXTRA_LIBS;
  const outDir = join(tmpdir(), "chromium-libs");
  const libDir = join(outDir, "lib");
  if (existsSync(join(libDir, "libnss3.so"))) return libDir;

  // The package's `exports` map hides package.json: resolve the entry point and
  // walk up until the archive is found.
  const require = createRequire(import.meta.url);
  let dir = dirname(require.resolve("@sparticuz/chromium"));
  while (!existsSync(join(dir, "bin", "al2023.tar.br")) && dir !== dirname(dir)) {
    dir = dirname(dir);
  }
  const archive = join(dir, "bin", "al2023.tar.br");
  if (!existsSync(archive)) return "";

  mkdirSync(outDir, { recursive: true });
  const tarPath = join(outDir, "libs.tar");
  writeFileSync(tarPath, brotliDecompressSync(readFileSync(archive)));
  execFileSync("tar", ["xf", tarPath, "-C", outDir]);
  console.log(`libs: unpacked the NSS runtime into ${libDir}`);
  return libDir;
}

let puppeteer;
let chromium;
try {
  puppeteer = (await import("puppeteer-core")).default;
  chromium = (await import("@sparticuz/chromium")).default;
} catch {
  console.error(
    "screenshot: missing driver.\n\n" +
      "  npm i --no-save puppeteer-core @sparticuz/chromium\n\n" +
      "  (run this in the folder holding this script, or copy the script into a\n" +
      "   folder that already has node_modules)",
  );
  process.exit(2);
}

let browser;
try {
  browser = await puppeteer.launch({
    executablePath: await chromium.executablePath(),
    headless: "shell",
    // Deliberately NOT chromium.args: that Lambda-tuned set includes flags that
    // stop some apps from mounting here.
    args: ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage"],
    env: { ...process.env, LD_LIBRARY_PATH: ensureLibs() },
  });
} catch (error) {
  const message = String(error?.message ?? error);
  const missing = /error while loading shared libraries: ([^:\s]+)/.exec(message);
  console.error("screenshot: the browser could not start.\n");
  if (missing) {
    console.error(`  missing shared library: ${missing[1]}`);
    console.error("  Set EXTRA_LIBS to a directory holding the NSS libraries.");
  } else {
    console.error(message.split("\n").slice(0, 8).join("\n"));
  }
  process.exit(1);
}

if (probe) {
  console.log(`browser: ${await browser.version()}`);
  await browser.close();
  process.exit(0);
}

const page = await browser.newPage();
page.on("pageerror", (error) => console.error(`pageerror: ${error}`));
page.on("console", (message) => {
  if (message.type() === "error") console.error(`console: ${message.text()}`);
});
page.on("requestfailed", (request) =>
  console.error(`request failed: ${request.url()} (${request.failure()?.errorText})`),
);

const target = new URL(url);
for (const pair of repeat("query")) {
  const [key, value = ""] = pair.split("=");
  target.searchParams.set(key, value);
}

/**
 * Poll rather than `waitForSelector`. When a WebGL scene is still initialising,
 * the main thread blocks and every `evaluate` queues behind it — a selector wait
 * then looks like a hang with no diagnostic. Polling can at least say so, and a
 * late screenshot beats no screenshot.
 */
async function settle(page, selector, budgetMs) {
  const started = Date.now();
  while (Date.now() - started < budgetMs) {
    const ready = await page
      .evaluate((query) => {
        if (query) return Boolean(document.querySelector(query));
        const root = document.getElementById("root");
        return (root ? root.children.length : document.body.children.length) > 0;
      }, selector)
      .catch(() => false);
    if (ready) {
      await new Promise((done) => setTimeout(done, 700));
      return true;
    }
    await new Promise((done) => setTimeout(done, 400));
  }
  return false;
}

page.setDefaultTimeout(60000);
await page.setViewport({ width, height, deviceScaleFactor: 1 });
await page.goto(target.toString(), { waitUntil: "domcontentloaded", timeout: 30000 });
// `networkidle0` never fires against a dev server that keeps a websocket open.
const ready = await settle(page, waitFor ? String(waitFor) : null, timeoutSeconds * 1000);
if (!ready) {
  console.error(
    `screenshot: nothing matched ${waitFor ? `"${waitFor}"` : "a mounted app"} after ` +
      `${timeoutSeconds}s — capturing anyway. Raise --timeout, or check pageerror above.`,
  );
}

if (tab) {
  const clicked = await page.evaluate((label) => {
    const nodes = [...document.querySelectorAll('[role="tab"],button,a')];
    const match = nodes.find((node) => node.textContent.trim() === label);
    if (!match) return false;
    match.click();
    return true;
  }, String(tab));
  if (!clicked) console.error(`screenshot: no tab or button reads "${tab}"`);
  await new Promise((done) => setTimeout(done, 900));
}

if (dump) {
  console.log(await page.evaluate(() => document.body.innerText));
} else {
  mkdirSync(dirname(out), { recursive: true });
  await page.screenshot({ path: out, fullPage: full });
  console.log(`wrote ${out}`);
}

await browser.close();
