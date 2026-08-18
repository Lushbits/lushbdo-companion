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

## Versioning

`<Version>` in the csproj; a release is tag `v<version>` + GitHub Release with
the published exe attached. `UpdateChecker` compares the running version to the
newest release and shows a notice — it never self-updates.

## Working loop

Issues #1–#3 are the milestone roadmap ((b) eyes, (c) dedup + live sending,
(d) polish). Milestone (b) logs OCR lines and deliberately does not send —
without (c)'s scroll dedup, sending double-counts. Real line shapes are
enumerated from (b)'s live logging before (c) parses them.
