# M693 OEM macro write — 2.6.4 analysis and validation

**Experimental until read-back is verified on a real Redragon M693-RGB.**

## What changed (supported by disassembly of user-provided 2022 OemDrv.exe)

The previous Neutron writer incorrectly issued a short ASCII NAME transaction at the slot base and a separate EVENTS transaction at `base + 0x1F`. In the original native x86 code:

- `0x410960` constructs an image whose first `0x20` bytes are macro header and whose subsequent data are five-byte events.
- `0x410ECC..0x410EF5` determines macro name length, caps at 15 UTF-16 code units, and copies **UTF-16LE** name into the first 31 bytes.
- `0x410E5C..0x410E8A` adds the event checksum and up to 3 bytes padding to the serialized image.
- `0x410F03..0x410FA8` computes address `(slot*3+6)<<7` = `0x300 + slot*0x180` and calls the common EEPROM writer with the **complete contiguous image**, length `0x20+5*event_count+1`.
- `0x4507F0` emits consecutive ten-byte command `0x07` packets and the final partial packet. It does not jump directly to `base+0x1F`.

The new writer reconstructs the contiguous stream, encodes UTF-16LE names, keeps checksum read-back, and does not claim success from HID ACK alone.

## First physical test — simplest two-event macro

1. Close all previous Neutron instances (the build script will no longer fail with CS0016 even if one remains open).
2. Run `BUILD_WINDOWS.bat`, `RUN_WINDOWS.bat`, back up the M693's onboard profile.
3. Choose a spare slot and a **nonessential** button; preserve a working left-click.
4. Create an intentionally short macro, name `TEST1`, events `84 01 00 / delay 20ms` and `44 01 00 / delay 20ms`. Select HOLD if desired.
5. Apply **once** only. Examine `Logs/M693_HID.log` for `MACRO_OEM_STREAM`, `TX 08 07`, then read-back.
6. If mismatch remains, **do not retry**. Send the exact trace with serial numbers and unrelated system identifiers removed, and a backup if safe to share.
7. Only after the two-event test is confirmed should a 10–150ms Smooth Hold macro be attempted.

This is an implementation correction justified by the original OEM control flow but **not a confirmed hardware fix**. An alternate firmware branch may still need additional controls; a short USBPcap recording of one successful OEM two-event macro write would resolve the remaining uncertainty.

## Important: RGB multi-color

The 2022 OEM profile RGB block `0xA0..0xA6` contains a single base color, mode, speed, brightness and checksum. The custom 2–8-color Palette Lab **only previews** colors; no multi-color live firmware command has been confirmed, and EEPROM should not be rewritten frequently.
