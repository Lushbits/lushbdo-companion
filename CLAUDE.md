# CLAUDE.md

Windows tray companion for lushbdo.com: passive screen capture + OCR of Black
Desert Online's loot chat, fed into the site's gather sessions. The site repo
(`Lushbits/bdo`, private) owns the server side; its issue #581 is the parent
spec and records every assessment and ruled-out path.

## Non-negotiables

These are inherited from the platform and they are what keeps this tool in the
same ToS class as streaming software. No feature is worth bending them.

- **Passive, always.** Read pixels the way OBS does. Never send input, never
  touch the game's process or memory, never automate anything in the client.
- **No extracted game data ships in this repo or the binary.** Item names are
  matched server-side; the app sends raw OCR text and counts.
- **Never guess.** Anything ambiguous is the server's to hold and the member's
  to resolve on the site. The app does not correct, fuzzy-match or drop lines.
- **The site is the product.** Sessions start/stop on the site; the register
  and held lines live there. The app's only UI beyond pairing is its live log
  and (milestone b) the region picker.
- **Featherweight beside the game.** Gamers notice; no feature is worth frame
  drops. Capture is sampled (not streamed) and cropped on the GPU. The chat
  background is transparent by design (owner decision, #2), so raw pixels
  always change — each frame is text-keyed (bright core with dark outline
  within reach, the game's own text contract), and keying is the cheap work
  the app does on every frame so it can skip the expensive work: it answers
  "did the text change", and a frame whose text did not change is never read.
  Reading is not cheap — a PaddleOCR pass over the region costs ~340 ms wall,
  about 0.74 core-seconds, against Windows.Media.Ocr's 60 ms — so the gate is
  what carries the budget. A measured wolf-grind session ran OCR on a fifth of
  its ticks and averaged 29% of one core. Reading only the rows that changed
  would halve that again and was tried; it is unsound against the board and
  the reasons are on `FrameDelta`, which now lives with the eval harness.
  Steady state allocates nothing, and the process runs below normal priority
  so the game always wins the CPU.

## The contract

`POST {base}/gather/ingest`, `Authorization: Bearer <bdo_mk_…>` (minted at the
site's Settings → Devices, DPAPI-stored here). Payload
`{batchId, lines: [{name, count}]}` — counts are **increments**. The server's
idempotency ring makes redelivering a batchId safe; a fresh batch gets a fresh
client-minted id. `applied:false, reason:"no-session"` means buffer and retry
once a session runs; 401 means revoked — notify once and stop.

## Build

```sh
dotnet build src/LushbdoCompanion          # needs .NET 8 SDK
dotnet publish src/LushbdoCompanion -c Release   # the shippable single exe
```

Target framework is `net8.0-windows10.0.22621.0` with
`SupportedOSPlatformVersion` 10.0.19041.0 on purpose — the 19041 floor is what
makes `Windows.Graphics.Capture` and `Windows.Media.Ocr` reachable, and the
22621 target additionally projects the Windows 11 capture-border-off API
(runtime-guarded via `ApiInformation`, so the exe still runs on 19041).
Publish is self-contained single-file: users install nothing, so nothing here
may grow a dependency that breaks that (no installers, no runtime prereqs).
The recognizer is PaddleOCR PP-OCRv5 through ONNX Runtime, which is why the
exe went from 78 MB to 103 MB: the ONNX and Skia natives ride inside it via
`IncludeNativeLibrariesForSelfExtract`, and the four model files ride as
embedded resources that `OcrModels` unpacks to `%LOCALAPPDATA%` on first run.
A `models\` folder beside the download would be an install, so the csproj
undoes the copy RapidOcrNet's own targets make.

One asterisk on "no runtime prereqs", and it is stated rather than papered
over: `onnxruntime.dll` imports `MSVCP140`/`VCRUNTIME140`, so it needs the
Visual C++ 2015-2022 redistributable. Black Desert installs it, so any machine
that can run the game this app watches already has it — but a machine without
it falls back to `WindowsOcrReader` with the reason in the log rather than
failing to watch. `libSkiaSharp.dll` carries its own CRT and needs nothing.
Nothing else here may grow a native dependency without that same check
(`dumpbin /dependents`, or the PE import table): a prereq that fails on a
member's PC is worse than a recognizer that reads a little worse.

## Versioning

`<Version>` in the csproj; a release is tag `v<version>` + GitHub Release with
the published exe attached. `UpdateChecker` compares the running version to the
newest release and shows a notice — it never self-updates.

## Working loop

Issues #1–#3 are the milestone roadmap ((b) eyes, (c) dedup + live sending,
(d) polish); (b) and (c) have landed. The real line shapes, and the owner
decisions this build encodes — transparent background is the target, silver
is skipped app-side, nothing is sent on one frame's word — are recorded in
#2's comments. Dedup keys line identity on position in the scroll stream
(text-anchored, voted across OCR passes), and every ambiguity resolves the
same direction: a visible undercount, never a double count. Logic that can
run without Windows (parser, keyer, board) is link-compiled into
`src/LushbdoCompanion.Tests`; `dotnet test src/LushbdoCompanion.Tests` runs
it.

`src/LushbdoCompanion.Eval` is the offline harness #18 was opened for: it
replays trace-corpus snapshots through preprocessing variants and both
recognizers and prints counts, not vibes. Score it through the *site's*
`item_name_key` fold rather than exact spelling — bdo folds case, punctuation
and confusable glyphs, so `Magnetite ore` and `Gold Bar I,OOOG` already land
on the right item and counting them as errors overstates every engine. On the
2026-08-22 corpus (60 frames, ~1020 rows) Windows.Media.Ocr on keyed frames
fully read 550 rows and PaddleOCR on raw frames 963; the gap is mostly not
spelling but rows the OS recognizer returns no bracket pair for at all. The
misreads it *does* make are systematic rather than random — `Ancient Spirit
Dust` came back as `Ancient. Spirit. Oust` 1160 times in one session and
correctly zero times — which is why more frames and more voting could not
close it.
