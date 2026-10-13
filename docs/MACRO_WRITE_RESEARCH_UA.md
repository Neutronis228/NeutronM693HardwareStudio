# M693 JM03: macro write read-back fails — findings and capture protocol

**Status: not fixed on hardware.** The official Redragon application can write these macros, but Neutron's `0x07` write calls to macro memory have ACKs without matching EEPROM read-back. Repeating identical writes is not a solution.

## Grounded observations from user's M693

- Device: receiver `25A7:FA7C`, separate TX `FF02/COL07`, RX `FF01/COL05`; basic profile writes work.
- Slot #4 base `0x0900`; a 40-event macro expected `0x28` at event count `0x091F`, but read `0x00`. The following bytes looked like stale/shifted prior events. This **does not justify guessing a new offset**.
- Even a 2-event macro failed NAME read-back (example `0x090A expected 0x31 actual 0x34`). Thus the 255-byte size hypothesis does **not** explain the general failure.
- The Windows application's HID ACK and successful profile write alone do not establish success for macro EEPROM writes.

## Version 2.6.3 safe workflow

A user can choose an **existing verified onboard macro** and bind it to a selected physical button by writing only its 4-byte profile mapping. No macro event region is changed. Only macros whose name/event block passed checksum validation are offered. New-macro writing remains experimental and requires an extra explicit confirmation; firmware results are checked by read-back.

## Evidence needed to implement a genuine fix

A short capture of a **successful** macro write made by the *official* Redragon software, with user permission. Wireshark and USBPcap can capture USB control transfers on Windows.

1. Back up M693 profile; do this on an expendable onboard profile if possible. Do not overwrite your only working macro.
2. Close Neutron and run Redragon OemDrv only.
3. Install [Wireshark](https://www.wireshark.org/) and [USBPcap](https://github.com/desowin/usbpcap/releases) from their official pages if you are comfortable using a kernel-level USB capture driver.
4. Capture the USBPcap root hub of the M693 receiver. Make a two-event left-down/left-up test macro in OemDrv (e.g. `M693TEST`), apply it successfully, stop capture immediately.
5. Export **only the relevant M693 control frames**; a full USB capture may contain unrelated private input/data, so do not publicly post the unfiltered file.
6. In Wireshark, inspect USB HID SET_REPORT command `bRequest=0x09` / `wValue=0x0308` for feature report 0x08 and look for any preceding profile-select, unlock, page/bank-switch, commit or delays. Compare the payload/addresses/order with the redacted Neutron `Logs/M693_HID.log`.

Never brute-force macro EEPROM addresses, never repeatedly write it hoping for eventual success, and do not claim a macro is installed until a semantic checksum-validated read-back succeeds.

## Custom RGB

Onboard RGB block `0xA0..0xA6` currently exposes one base RGB color and one firmware effect. The app's 2–8-color palette is a **local preview**, not proven onboard multi-color animation. A verified RAM-only lighting command would be needed. Rewriting EEPROM continuously to animate LEDs is not safe.
