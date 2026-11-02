// scripts/verify.mjs
// Cheap core-functionality check for the Arena sandbox: launch the bundled
// Chromium, navigate, wait for the app to mount, run assertions, exit 0 on
// PASS / 1 on FAIL. No image is produced — DOM/text assertions only, about
// one second per run including browser launch. See SKILL.md ("When not to
// use this skill").
//
// Edit `check(page)` to assert what your app should show after mounting, then
// add interaction checks (click → re-assert) below it.
//
// Usage (run it from a folder that can resolve node_modules):
//   npm i --no-save puppeteer-core @sparticuz/chromium
//   node scripts/verify.mjs http://127.0.0.1:5173/
//   node scripts/verify.mjs http://127.0.0.1:5173/ --wait ".session-loaded" --timeout 60
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { brotliDecompressSync } from "node:zlib";

/**
 * @sparticuz/chromium ships a browser for Amazon Linux 2023, which — like this
 * sandbox — has no NSS. It therefore bundles the three shared objects the
 * browser needs in `bin/al2023.tar.br`. Unpack them, point the loader at them.
 * See SKILL.md Step 3.
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
  console.log(`verify: unpacked the NSS runtime into ${libDir}`);
  return libDir;
}

const argv = process.argv.slice(2);
const flag = (name, fallback = null) => {
  const index = argv.indexOf(`--${name}`);
  return index === -1 ? fallback : (argv[index + 1] ?? true);
};
const has = (name) => argv.includes(`--${name}`);

const url = argv.find((arg) => arg.startsWith("http")) ?? "http://127.0.0.1:5173/";
const waitFor = flag("wait");
const timeoutSeconds = Number(flag("timeout", 30));

let puppeteer;
let chromium;
try {
  puppeteer = (await import("puppeteer-core")).default;
  chromium = (await import("@sparticuz/chromium")).default;
} catch {
  console.error(
    "verify: missing driver.\n\n" +
      "  npm i --no-save puppeteer-core @sparticuz/chromium\n\n" +
      "  (run this in the folder holding this script, or copy the script into a\n" +
      "   folder that already has node_modules)",
  );
  process.exit(2);
}

/**
 * Edit this to assert what your app should show once mounted.
 * Return true (PASS) or false (FAIL). Throwing is fine — it fails the run.
 *
 * Example:
 *   const clicked = await page.evaluate((label) => {
 *     const el = [...document.querySelectorAll('button')].find(
 *       (n) => n.textContent.trim() === label);
 *     el?.click(); return Boolean(el);
 *   }, "Fixer");
 *   await new Promise((r) => setTimeout(r, 500));
 *   return await page.evaluate(() => document.getElementById("status").textContent === "fixed");
 */
async function check(page) {
  const mounted = await page.evaluate(() => {
    const root = document.getElementById("root");
    return root ? root.children.length > 0 : document.body.children.length > 0;
  });
  if (!mounted) return false;
  console.log(
    "verify: page text: " +
      (await page.evaluate(() => document.body.innerText.slice(0, 400))),
  );
  return true;
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
  console.error("verify: the browser could not start.\n");
  if (missing) {
    console.error(`  missing shared library: ${missing[1]}`);
    console.error("  Set EXTRA_LIBS to a directory holding the NSS libraries.");
  } else {
    console.error(message.split("\n").slice(0, 8).join("\n"));
  }
  process.exit(1);
}

const page = await browser.newPage();
page.on("pageerror", (error) => console.error(`pageerror: ${error}`));
page.on("console", (message) => {
  if (message.type() === "error") console.error(`console: ${message.text()}`);
});

const target = new URL(url);
await page.setDefaultTimeout(60000);
await page.setViewport({ width: 1600, height: 900, deviceScaleFactor: 1 });
await page.goto(target.toString(), { waitUntil: "domcontentloaded", timeout: 30000 });
// `networkidle0` never fires against a dev server that keeps a websocket open.
const started = Date.now();
const ready = await page
  .evaluate((query) => {
    if (query) return Boolean(document.querySelector(query));
    const root = document.getElementById("root");
    return root ? root.children.length > 0 : document.body.children.length > 0;
  }, waitFor ?? null)
  .catch(() => false);
if (!ready) {
  console.error(
    `verify: app did not mount (waited for ${waitFor ?? "any content"}) —`,
    `capturing DOM text anyway:`,
  );
  console.log(await page.evaluate(() => document.body.innerText.slice(0, 1000)));
  await browser.close();
  process.exit(1);
}

let ok;
try {
  ok = await check(page);
} catch (error) {
  console.error(`verify: check failed: ${error}`);
  ok = false;
}

await browser.close();
console.log(`verify: ${ok ? "PASS" : "FAIL"} (${Date.now() - started}ms)`);
process.exit(ok ? 0 : 1);
