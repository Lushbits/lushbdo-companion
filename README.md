# Lushbdo Companion

A Windows tray app that watches Black Desert Online's loot log with passive
screen capture and OCR, and feeds what you gather into your running session on
[lushbdo.com](https://lushbdo.com) — replacing hand-typing counts mid-run.

**The site is the product; this app is the typing you no longer do.** Sessions
start and stop on the site, the register lives on the site, misreads are
resolved on the site. The app pairs once, then sits in the tray.

## Status

Milestone (c) — dedup and live sending. The app watches the picked region,
stabilizes it over a rolling window of frames so OCR reads sharp text over the
animated game world, confirms every line across frames, and sends confirmed
pickups to your running gather session in small batches. Lines the site cannot
match are held there for you to resolve — never guessed at, never silently
dropped.

- [x] **(a) The pipe** — tray app, token pairing, live log, test batch, update notice
- [x] **(b) Eyes** — drag-a-rectangle region pick, `Windows.Graphics.Capture` + `Windows.Media.Ocr` over it
- [x] **(c) Dedup** — temporal stabilization, reading consensus, text-anchored scroll tracking, live sending
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
- A **dedicated chat tab** filtered to item acquisition messages only, at a
  **fixed position and size**. A transparent background is fine — the app
  stabilizes the image across frames before reading it. An opaque, dark
  background is still a recommendation that improves accuracy, never a
  requirement.
- English client (v1 reads English only).

Then, from the tray icon: **Watched regions → Loot log**. The app finds the game's
window, photographs one frame of it, and shows that still full-screen — drag a
rectangle around the chat tab's text on it, Esc cancels. Start the rectangle
just right of the `System` chip column — every loot line begins "You have
obtained", so nothing is lost and the chip never reaches OCR. Because the
frame comes from the game window's own surface, it does not matter what is
covering the game at that moment: open the tray menu over a browser and the
picker still shows a clean still of the chat. (If the game window cannot be
found, the app falls back to picking on the live screen after a short "switch
to the game" countdown.)

The app then watches that region of the **game window itself — never the
monitor**: passive capture at ~2 fps, a rolling five-frame median that keeps
the chat glyphs sharp while the world behind them smears away, offline OCR
over that stabilized image. Lines already on screen when watching starts are
old and are never sent. Capturing the window's own surface means:

- **The app can only ever see the game, never the desktop.** Other windows
  crossing the region are structurally invisible to it — there is no pixel of
  anything but the game it could ever read.
- Tabbing away neither blinds nor contaminates the watcher: the game's surface
  keeps being read behind whatever covers it.
- The region sticks to the game window, so it survives the window moving and
  the game restarting.
- The game not running is not an error. The watcher says it is waiting for the
  game window and starts by itself once the game is up — same again after the
  game exits and relaunches. What the chat shows after a gap is treated as
  old, never recounted.

A pickup is sent only after its reading recurs on a later frame — nothing is
ever sent on one frame's word. Confirmed lines leave in a small batch every
few seconds; the site matches names server-side, and what it cannot match is
held on the session page for you to resolve. No session running? The app
holds what it reads, says so in the log, and delivers when you press Start.
The log announces when capture is live and heartbeats every couple of
minutes when nothing changes, so a silent log always means something is
wrong.

Three things to know while it runs:

- **Don't scroll the loot tab.** New lines are told from old by where they
  sit on the scroll stream; wheel-scrolling the tab makes old lines look
  new, so the app realigns and treats everything visible as already counted.
- **Burst loot can undercount.** If more lines land between two samples than
  the window shows, whatever scrolled straight past is missed, silently.
  Fine for gathering — a node every few seconds; grinding is out of scope
  for v1.
- **Silver is recognized and deliberately not sent** — gather sessions count
  items, not currency.

### Watching your silver balance

Optional, and off until you pick a rectangle for it. **One rectangle, aimed at
the Central Market panel** — the figure it shows there (`Warehouse Balance`) is
the same silver the warehouse panel shows, so either would do, and the market
one is the one this app reads.

That is not arbitrary. The warehouse panel draws its balance beside the
Withdraw button, and that button's hover overlay physically covers the last
group of digits — the app read `23,975,827` for `23,975,827,939` three times
running in testing. A crop like that contains a complete, well-formed, *wrong*
number, which nothing downstream can detect: the strict shape passes it (a
truncated grouped number is still grouped) and re-reading passes it (it repeats
while the overlay is up). The market panel has nothing that hovers over its
figure, so the fix is to read the panel that does not have the problem.

**Open the Central Market in-game first**, then from the tray icon: **Watched
regions → Marketplace silver**. The picker photographs the game as it is *right
now*, so with the panel closed there is nothing to drag a rectangle around. If
the game closed the panel when you tabbed away, press Esc and the app offers to
pick on the live screen after a three-second countdown instead.

**Include the words `Warehouse Balance` in the rectangle, along with the figure
and nothing else.** The label is what tells the app that the number is your
balance rather than any number the interface happened to draw in that spot — an
item tooltip's `Market Price: 69,000,000,000 Silver` is a perfectly well-formed
number, and without the label it would be recorded as your silver. If the crop
comes back with the label missing, or with text in it besides the label and the
figure, the app refuses it and says so: something is drawn over the rectangle.

So: label and figure inside, a few pixels of margin around them so the
recognizer has room, and buttons and counters outside.

It rides the same **Start watching** toggle and the same capture as the loot
log, because a second capture session would double what the compositor does per
tick. **Watching is all or nothing**: there is no silver-only mode and no
per-region switch. To stop watching something, remove its region.

**Watched regions** lists both rectangles with the size and position each is
set to, or `not picked yet`. Clicking one picks it again, and each has its own
**Forget**. Forgetting the loot log is allowed too — watching then stops until
you pick one again, because the single capture is aimed at it.

**A confirmed figure is sent to the site**, where it becomes your liquid silver
on the sheet, the goal bar, the dashboard and the `/assets` series — the same
number a press writes, so nothing about it is a separate kind of entry. It goes
only when it *differs* from the figure the site already has, and never more than
once every couple of minutes; a balance is account state, so no gather session
needs to be running. Unpaired, the app reads and logs exactly as below and sends
nothing.

Three things the log tells apart:

- nothing at all — no panel is open over the rectangle, so nothing was read
- read but not confirmed, with the reason — a shape the app will not stand
  behind, or readings that never agreed
- confirmed at a value, and every couple of minutes after that, that it is
  still reading the same one — then `sent` when the site takes it, or `sent …
  the site already had it` when the figure had not moved

A figure the app is not sure of is never confirmed, and it is strict on
purpose: a balance has no register behind it the way a loot line does, so
`1,000` misread as `1,00` would land silently and stay. So the app takes the
one number in the crop, requires it grouped in threes, requires three readings
to agree, and refuses everything else — including a crop with two numbers in
it. When it refuses, the figure you already have stands.

**Windows OCR does not read balances.** It reads comma-grouped numbers as
letters (0 of 1,332 read correctly in the app's own bake-off), so with *Read
with Windows OCR* ticked the silver rectangle is skipped and the log says so,
rather than spending passes to confirm nothing.

On Windows 11 the app asks the OS to skip the yellow "this window is being
captured" border and usually may. On Windows 10 that API does not exist: the
border around the game window is unavoidable there, same as with OBS.

### Built to sit beside a running game

- Capture is the same compositor path OBS uses — window capture, its least
  invasive mode — but sampled, not streamed: the app drains the frame pool
  twice a second and the compositor skips it entirely in between.
- The region is cropped on the GPU — only the chat-sized rectangle ever
  crosses to the CPU, never the whole window.
- OCR reads the median-stabilized image at half the capture pace, and only
  when that image actually changed — a still scene costs a few milliseconds
  of arithmetic per tick and no OCR at all.
- The silver rectangle is gated the opposite way round, on **stillness**:
  two consecutive frames near-identical means a panel is up and worth a look,
  anything else is the moving world and is dropped before any reading. With no
  panel open that is one sampled diff over a few thousand pixels per tick and
  nothing else, and one still picture is read at most six times however long it
  sits there — so the cost is a short burst when you open your warehouse, not a
  standing charge. A pass over a digit-sized crop measures ~65 ms warm against
  the chat region's ~340 ms.
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
