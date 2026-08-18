# Lushbdo Companion

A Windows tray app that watches Black Desert Online's loot log with passive
screen capture and OCR, and feeds what you gather into your running session on
[lushbdo.com](https://lushbdo.com) — replacing hand-typing counts mid-run.

**The site is the product; this app is the typing you no longer do.** Sessions
start and stop on the site, the register lives on the site, misreads are
resolved on the site. The app pairs once, then sits in the tray.

## Status

Milestone (b) — eyes. The app pairs, and watches a picked region of the screen:
passive capture at ~2 fps, offline OCR, every line printed to the live log.
It deliberately does **not** send what it reads yet — consecutive frames
re-read the same lines, and without milestone (c)'s scroll dedup that would
double-count. This log-only stage is also how the real loot-line shapes get
enumerated from live play before (c) parses them.

- [x] **(a) The pipe** — tray app, token pairing, live log, test batch, update notice
- [x] **(b) Eyes** — drag-a-rectangle region pick, `Windows.Graphics.Capture` + `Windows.Media.Ocr` over it
- [ ] **(c) Dedup** — frame-to-frame scroll alignment; OCR only the newly revealed strip
- [ ] **(d) Polish** — start with Windows, quiet failure handling, first tagged release

## Setup

1. On [lushbdo.com](https://lushbdo.com): Settings → Devices → pair a device.
   The token is shown **once** — copy it.
2. Run the app. It opens Settings on first launch: paste the token, Save.
3. Right-click the tray icon → **Send test batch** with a gather session
   running on the site. Watch the log; refresh the session page.

The token is stored DPAPI-encrypted per Windows user — the settings file is
useless on another machine or account. Revoking the device on the site kills
the token on its next request; the app will tell you in the log.

## Watching the loot log

Set the game up once:

- **Borderless windowed** (or windowed) mode — screen capture cannot see
  exclusive fullscreen, exactly like OBS.
- A **dedicated chat tab** filtered to item acquisition messages only, with an
  **opaque background**, at a **fixed position and size**.
- English client (v1 reads English only).

Then, from the tray icon: **Pick loot log region…**. The app finds the game's
window, photographs one frame of it, and shows that still full-screen — drag a
rectangle around the chat tab on it, Esc cancels. Because the frame comes from
the game window's own surface, it does not matter what is covering the game at
that moment: open the tray menu over a browser and the picker still shows a
clean still of the chat. (If the game window cannot be found, the app falls
back to picking on the live screen after a short "switch to the game"
countdown.)

The app then watches that region of the **game window itself — never the
monitor**: passive capture at ~2 fps, OCR'd offline by Windows, every line
printed to the log window. Capturing the window's own surface means:

- **The app can only ever see the game, never the desktop.** Other windows
  crossing the region are structurally invisible to it — there is no pixel of
  anything but the game it could ever read.
- Tabbing away neither blinds nor contaminates the watcher: the game's surface
  keeps being read behind whatever covers it.
- The region sticks to the game window, so it survives the window moving and
  the game restarting.
- The game not running is not an error. The watcher says it is waiting for the
  game window and starts by itself once the game is up — same again after the
  game exits and relaunches.

The log announces when capture is live and heartbeats every couple of minutes
when nothing changes, so a silent log always means something is wrong.

In this milestone the lines are **logged only, never sent** — until scroll
dedup lands (milestone c), consecutive frames re-read the same lines and
sending them would double-count. Open the log, play, and watch what it reads.

On Windows 11 the app asks the OS to skip the yellow "this window is being
captured" border and usually may. On Windows 10 that API does not exist: the
border around the game window is unavoidable there, same as with OBS.

### Built to sit beside a running game

- Capture is the same compositor path OBS uses — window capture, its least
  invasive mode — but sampled, not streamed: the app drains the frame pool
  twice a second and the compositor skips it entirely in between.
- The region is cropped on the GPU — only the chat-sized rectangle ever
  crosses to the CPU, never the whole window.
- OCR runs only when the region's pixels actually changed; a static chat
  costs one memory compare per tick (fractions of a millisecond).
- Buffers are allocated once and reused — the steady state allocates
  practically nothing, so the GC stays quiet.
- The process runs at below-normal priority: when the game wants the CPU,
  the game wins.

### Windows SmartScreen

The `.exe` is unsigned (code-signing certificates cost real money), so the
first run on each machine shows "Windows protected your PC". **More info →
Run anyway.** The download you should trust is the one from this repository's
Releases page and nowhere else.

## The rules this app lives by

These come from the platform's own non-negotiables and they are what keeps this
tool in the same ToS class as streaming software:

- **Passive, always.** It reads pixels the way OBS does. It never sends input,
  never touches the game's process or memory, never automates anything.
- **No game data ships in it.** Item names are matched server-side; the app
  sends raw text and counts.
- **Never guess.** A line the server cannot confidently match is held and shown
  to you on the session page — never silently dropped, never stored as a
  plausible wrong item.

## Building

```sh
dotnet build src/LushbdoCompanion
dotnet run --project src/LushbdoCompanion
```

Publish a shippable single-file exe:

```sh
dotnet publish src/LushbdoCompanion -c Release
```

## Versioning and releases

Semantic-ish versions (`0.1.0`, `0.2.0`, …) set in the `.csproj`. A release is
a git tag `v<version>` plus a GitHub Release with the published `.exe`
attached. The app checks the newest release at startup and daily, and shows a
tray notification with a download link when it is behind — it never updates
itself.

## Server side

The pairing surface, the ingest contract, the name matcher and the held-lines
flow live in the (private) site repository — the app repo tracks only the app.
The contract this app speaks is `POST /gather/ingest` with a bearer token:
`{batchId, lines: [{name, count}]}`.
