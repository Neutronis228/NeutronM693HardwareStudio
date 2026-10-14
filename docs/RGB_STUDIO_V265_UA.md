# RGB Studio 2.6.5 — hardware and Palette Lab

## Verified OEM metadata
The user-supplied 2022 Redragon M693 `Cfg.ini` and English `text.xml` contain seven effect names:
1. Steady
2. Breathing
3. Streaming
4. Neon
5. Single color flow
6. Colorful breathing
7. OFF

Their settings are serialized as one `0xA0–0xA6` hardware block (effect, R, G, B, speed, brightness, checksum). **No firmware protocol allowing arbitrary 2–8 chosen colors as one hardware animation is confirmed.** The OEM's built-in Colorful breathing mode cycles its own predefined firmware colors; it does not accept the app's full arbitrary palette.

## New in 2.6.5
- Color presets: red, orange, green, cyan, blue, purple, white; all use existing EEPROM profile writes with read-back when Apply is pressed.
- Choosing a selected (not necessarily first) Palette Lab color prepares that color as the M693's one base RGB color.
- OFF is automatically changed to Steady when a new color is chosen, to avoid apparently invisible Apply results.
- Local Palette Lab adds Edit color and Up/Down ordering.
- RGB controls are stacked and vertically scroll on narrow windows; no forced horizontal viewport minimum.
- Macro serializer from hardware-validated v2.6.4 deliberately unchanged.

## Safety
Color selection is only a pending change until the user clicks **Apply**. No continuous EEPROM writes are used. Save RAW backup before writing.

### Real hardware acceptance test
On the real M693, set Steady + preset purple, Apply, see LEDs change and perform Read profile. Then test selected palette row 2, Apply and read back the new base RGB; repeat for one stock multi-color effect if desired. **Windows CI cannot confirm LED behavior or arbitrary palette animation.**

Windows compile, protocol self-test and UI smoke: [SUCCESS](https://github.com/Neutronis228/NeutronM693HardwareStudio/actions/runs/38590390710).

Project owner: Neutronis228 · MIT source licensing. Redragon original artwork is not MIT licensed.
