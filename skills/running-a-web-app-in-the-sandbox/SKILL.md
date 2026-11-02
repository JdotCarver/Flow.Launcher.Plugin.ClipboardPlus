---
name: running-a-web-app-in-the-sandbox
description: Verify a web app in the Arena sandbox with headless Chromium — screenshot it for visual checks, or assert DOM/text for core functionality. Use when a task needs to confirm a page mounts, an interaction lands, or a layout looks right.
version: "1.0.0"
compatibility: Arena sandbox; Node.js; outbound access to registry.npmjs.org
---

# Running a web app in the sandbox

A coding agent that cannot see the page is reduced to reading CSS and hoping.
The Arena sandbox has no browser installed and no usable apt mirror, so the
obvious routes fail — but a working one exists: `@sparticuz/chromium` ships a
Chromium build that bundles the NSS libraries it needs
(`node_modules/@sparticuz/chromium/bin/al2023.tar.br`). Unpack that archive,
point `LD_LIBRARY_PATH` at it, and the browser starts. Nothing is compiled or
downloaded from anywhere else. Full derivation: `scripts/screenshot.mjs`.

## When to use this skill

- Confirm a page mounts and settles after a deploy or an edit.
- Check layout, overflow, or crowding at a real viewport (this project designs
  at 2560×1080; screenshot at that size).
- Verify an interaction lands: click a tab/button, then re-check the DOM.

## When not to use this skill

- Core functionality (does this button change state? does this endpoint
  return data?). Use DOM/text assertions with `scripts/verify.mjs` instead:
  machine-readable, about one second per run including browser launch, and no
  vision tooling needed. Validated in this sandbox: a click-and-assert round
  trip passes in ~677 ms.
- Proving interaction, keyboard behaviour, or state transitions from a single
  image — screenshots show composition only (Step 8).

## Prerequisites

- The app served on `http://127.0.0.1:<port>/`, bound to `0.0.0.0` so the
  live preview works. Never point the browser at a `file://` URL.
- If the app talks to a backend on another port, proxy `/api` in the dev
  server (Vite: `server.proxy`) so both the sandbox browser and the human's
  preview work.
- `npm i --no-save puppeteer-core @sparticuz/chromium` in the folder holding
  the script (`--no-save` keeps the lockfile clean). `node_modules/` is
  pruned between turns: reinstall, don't assume.

## Steps

### 1 — Serve the app

```bash
npm run dev -- --host 0.0.0.0   # or: vite, next dev, python -m http.server
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5173/
```

### 2 — Install the browser

```bash
npm i --no-save puppeteer-core @sparticuz/chromium
```

### 3 — Unpack the NSS libraries Chromium needs

This is the whole trick. `al2023.tar.br` contains the three shared objects the
browser dies without (`libnspr4.so`, `libnss3.so`, `libnssutil3.so`). Both
scripts unpack it to `$TMPDIR/chromium-libs` and set `LD_LIBRARY_PATH`. See
`scripts/screenshot.mjs` for the ~20-line routine.

### 4 — Launch

```js
import puppeteer from "puppeteer-core";
import chromium from "@sparticuz/chromium";

const browser = await puppeteer.launch({
  executablePath: await chromium.executablePath(),
  headless: "shell",
  // Do NOT pass `...chromium.args`: that Lambda-tuned set includes flags that
  // stop apps from mounting here.
  args: ["--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage"],
  env: { ...process.env, LD_LIBRARY_PATH: "<unpacked lib dir>" },
});
```

`--no-sandbox` is required because the sandbox runs as root. `--disable-gpu`
avoids a GPU process that cannot exist; WebGL still runs through SwiftShader.

### 5 — Wait for the page, not for the network

`waitUntil: "networkidle0"` hangs forever against any dev server that holds a
websocket open (Vite HMR, Next fast refresh, a socket.io app). Use:

```js
await page.goto(url, { waitUntil: "domcontentloaded", timeout: 30000 });
await page.waitForSelector("#root > *", { timeout: 60000 }); // app mounted
await new Promise((r) => setTimeout(r, 600));                 // settle: fonts, animation
```

If the framework mounts into another node, read `index.html` and use that
selector. If the app boots through an async session fetch, wait for a
selector that only exists once the data is in.

### 6 — Capture or assert

**Core functionality (default):**

```bash
node scripts/verify.mjs http://127.0.0.1:5173/
```

`verify.mjs` navigates, waits for the app to mount, runs the assertions you
encode in its `check(page)` function, and exits `0` (PASS) or `1` (FAIL) with
a one-line verdict. Adapt `check(page)` to your DOM; add click → re-assert
for interactions.

**Visual/layout:**

```bash
node scripts/screenshot.mjs http://127.0.0.1:5173/ --out shots/home.png
node scripts/screenshot.mjs http://127.0.0.1:5173/ --tab Fixer --width 2560 --height 1080
node scripts/screenshot.mjs http://127.0.0.1:5173/ --dump     # page text instead of an image
```

Read the image back with your file-reading tool — a screenshot is only useful
if you actually look at it.

### 7 — Troubleshooting

| Symptom | First check |
|---|---|
| Black rectangle | Compare against `document.body.innerText`: an empty table on a dark theme photographs black. |
| Nothing mounted | `document.getElementById("root").children.length === 0` means a JS error — attach `page.on("pageerror")` and `page.on("console")`. |
| Request failing | `page.on("requestfailed")`; a 404/502 for the session endpoint often leaves the app on a splash screen. |
| Still rendering | 2 cores with a software rasteriser can block the main thread for a minute — verify with a smaller viewport before blaming the renderer. |
| Browser won't start | Missing shared library — `ldd` on the binary names it; override with `EXTRA_LIBS=<dir>`. |

Only then suspect the renderer, and test it with a fallback mode before
reporting it broken.

## Do / Don't

- Do wait for a real screen element, not `#root` when data arrives async.
- Do screenshot at the design canvas (2560×1080) for layout decisions.
- Do keep scripts inside the project next to `node_modules`: a bare import
  resolves relative to the script, not the shell's working directory.
- Don't `apt-get install` anything: no package lists, no mirror.
- Don't install `playwright` — `playwright install` cannot fetch a browser here.
- Don't use `networkidle0`, don't pass `...chromium.args`, and don't present
  a jsdom test as visual verification — jsdom has no layout engine.

## Files

- `SKILL.md` — this runbook.
- `scripts/screenshot.mjs` — screenshot/capture tool (`--out`, `--tab`, `--wait`, `--timeout`, `--dump`, `--probe`).
- `scripts/verify.mjs` — DOM/text verification tool; adapt `check(page)` to your app.
