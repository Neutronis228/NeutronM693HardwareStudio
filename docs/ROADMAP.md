# Roadmap and release status

## Hardware tested

The 2.4 GHz receiver on one Redragon M693 exposed VID 25A7 / PID FA7C, HID report pairs FF02/COL07 (TX) and FF01/COL05 (RX). Direct device discovery, battery and onboard profile reading were observed using the 2.2 series. This does not prove successful profile writing for every firmware variant.

## Priorities

1. **Windows start reliability** — reproduce 2.3–2.5 launch failure under a Windows CI smoke test; collect exception logs if needed.
2. **Preserve and publish history** — nine tagged experimental source snapshots v1.0.0–v2.5.0.
3. **Read-only hardware validation** — USB and 2.4 GHz; parser correctness and response checksums.
4. **Write path regression testing** — automatic backups and semantic read-back of macros, buttons and combo assignments. Test on physical hardware only after successful diagnostics.
5. **Layout and accessibility** — high-DPI, resizable panels, text scaling, scroll access and localization.
6. **Safe profile export/import** — non-destructive data handling, version format migration.
7. **RGB enhancements** — vendor presets and palette preview; RAM-only multi-color effects are an investigation, not a confirmed onboard capability.
8. **Installer, portable ZIP and signed release** — only after reproducible Windows builds and physical verification.

## Safety gates before stable release

- App launches and closes on Windows 10/11 without mouse attached.
- No crashes from HID detection, disconnect/reconnect and event stream.
- Read-only M693 data is correct and round trips without changes.
- Backups and dry-run diff work.
- Hardware writes are tested with manual rollback and no firmware erase path.
- No redistributed vendor binaries, trademarks or non-free assets falsely licensed under MIT.

## Attribution

Original project author: Neutronis228. Copyright (c) 2026 Neutronis228. MIT license applies to code contributed to this repository; independent vendor material has separate ownership.
