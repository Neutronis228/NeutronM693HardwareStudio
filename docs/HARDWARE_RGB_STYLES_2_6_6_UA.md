# RGB hardware styles (2.6.6) — Ukrainian release notes

## Confirmed working data path
Original Redragon TRIDENT M693-RGB receiver `25A7:FA7C`, the v2.6.4 macro writer was successfully tested by the device owner. v2.6.6 keeps the macro writer unchanged.

Review of user-supplied **2022 M693 OEM `Cfg.ini`, `en/text.xml` and `OemDrv.exe`** shows the main lighting EEPROM area `0xA0–0xA6` encodes:
`[firmware effect, red, green, blue, speed, brightness, checksum]`.

Seven firmware effect choices: Steady, Breathing, Streaming, Neon, Single color flow, Colorful breathing, Off.

This **does not provide proof of a volatile RGB streaming command** or a programmable multiple-color palette. In particular, the separate **2025 M693 LIT** installer is for a differently specified model and its unknown commands must not be sent to the original M693 solely because of a similar product name. Related third-party open-source Redragon mice support different USB protocols (different PID).

## Implemented in 2.6.6 (can be written to physical M693)
- Save, load and remove up to 64 named hardware RGB styles.
- A style consists of **one chosen RGB color, a real supported firmware effect, speed 1–10, and brightness 1–10**.
- Explicit **Apply hardware style** writes RGB with the existing EEPROM backup, diff-write and read-back path, and refuses to write unrelated pending changes to buttons, macros or DPI.
- Styles persist in `%LOCALAPPDATA%\NeutronM693HardwareStudio\hardware_rgb_styles.json`.
- Swatches and custom one-color RGB effects remain available.

## Not implemented
- Arbitrary 2–8-color *user-defined* crossfade/wave/step on the actual mouse. The palette animation is currently a preview. Stock `Colorful breathing` is an actual firmware effect, but its palette is predetermined by the firmware, not the user.
- No repetitive EEPROM writes to mimic real-time RGB: it risks flash wear and can interfere with mouse use.

## Real hardware test
1. Back up mouse settings.
2. Pick a single RGB color, set hardware effect (e.g. Breathing), brightness, speed.
3. Save it as `Test My Breathing`.
4. Choose that saved style and press **Apply hardware style**; approve backup/write.
5. Observe mouse LEDs physically, then select **Read** and check the RGB values.
6. Confirm M693 button #4 macro is still intact.
7. Try the stock `Colorful breathing` mode and note that the colors are chosen by firmware.
If any read-back verification fails, stop and keep the auto-backup. No need to retry repeatedly.

## Research direction for custom multi-color
Find an authentic **RAM-only LED command** via official OEM HID USB capture / MCU documentation or vendor SDK. Only introduce animation engine after verifying response and lack of EEPROM writes with an actual M693. Never brute-force unknown HID command opcodes.
