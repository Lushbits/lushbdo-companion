# Lushbdo Companion

A Windows tray app that watches Black Desert Online's loot log with passive
screen capture and OCR, and feeds what you gather into your running session on
[lushbdo.com](https://lushbdo.com) — replacing hand-typing counts mid-run.

**The site is the product; this app is the typing you no longer do.** Sessions
start and stop on the site, the register lives on the site, misreads are
resolved on the site. The app pairs once, then sits in the tray.

## Status

Milestone (a) — the pipe. The app pairs with a token, keeps a live log, and can
send a synthetic test batch to the site's ingest. Screen capture and OCR are the
next milestones; see the issues.

- [x] **(a) The pipe** — tray app, token pairing, live log, test batch, update notice
- [ ] **(b) Eyes** — drag-a-rectangle region pick, `Windows.Graphics.Capture` + `Windows.Media.Ocr` over it
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
