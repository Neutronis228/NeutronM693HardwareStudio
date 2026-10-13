# Redragon M693-RGB — reverse-engineering notes

Основа: файли з наданого встановленого пакета `REDRAGON M693-RGB.zip`.

## Ідентифікація

`Cfg.ini`:

- `Title=REDRAGON M693-RGB`
- `Appdir=JM03`
- `VID=0x25A7`
- `PID=0xFA7B`
- `PID2=0xFA7C`
- `Sensor=0x3104`
- `Debounce=8`
- `MinDelay=1`

`OemDrv.exe` — native PE32 x86. Імпортує `HidD_SetFeature`, `HidD_GetHidGuid`,
`HidD_GetPreparsedData`, `HidD_GetAttributes`, `HidP_GetCaps` і SetupAPI enumeration.
Тобто для програмування onboard memory окремий proprietary kernel driver не потрібний.

## Command HID protocol

Vendor command interface має Feature Report 17 bytes.

Request:

```text
[08] [cmd] [00] [addr_hi] [addr_lo] [len] [data x 10] [checksum]
```

Response:

```text
[09] [cmd] [status] [addr_hi] [addr_lo] [len] [data x 10] [checksum]
```

Checksum:

```text
0x55 - sum(previous bytes) mod 256
```

Команди, які використовує Hardware Studio:

- `07` — write EEPROM
- `08` — read EEPROM

## Profile map

- `0x00` polling pair
- `0x02` DPI count pair
- `0x04` active DPI pair
- `0x0A` LOD pair
- `0x0C..0x2B` 8 DPI records × 4 bytes
- `0x2C..0x4B` 8 DPI colors × 4 bytes
- `0x60..0x9F` 16 button records × 4 bytes
- `0xA0..0xA6` RGB lighting block
- `0xA9` debounce pair
- `0xAB` motion sync pair
- `0xAD` sleep pair
- `0xAF` angle snap pair
- `0xB1` ripple pair
- `0xB3` LED-off-moving pair

Pair = `[value][0x55-value]`.
Button record = `[type][arg1][arg2][0x55-sum(first3)]`.

## M693 physical buttons from supplied Cfg.ini

The final Kx field is the one-based matrix position:

- button 1 -> slot 0
- button 2 -> slot 1
- button 3 -> slot 2
- button 4 -> slot 4 (`K4_1 ... 0x05`)
- button 5 -> slot 3 (`K5_1 ... 0x04`)
- button 6 -> slot 9 (`... 0x0A`)
- button 7 -> slot 10 (`... 0x0B`)

## Macro action / hold mode

Button action type `06` = macro:

```text
06 <macro_index> <mode>
```

Known mode bytes:

- `FE` / 254 = repeat while held
- `FF` / 255 = until another key is pressed
- `FD` / 253 = toggle
- other N = repeat N times

Macro storage:

```text
0x300 + macro_index * 0x180
```

Slot structure used here:

```text
byte 0       name length
byte 1..30   ASCII name / padding
byte 31      event count
events       5 bytes each
checksum     0x55 - sum(event-count + event bytes)
```

Each event:

```text
3-byte key/mouse event + uint16 delay_ms (big endian)
```

From the supplied official `OemDrv.exe` disassembly (`StMacro_To_HdMacro`):

```text
mouse press   = 0x84, button_mask, 0x00
mouse release = 0x44, button_mask, 0x00
```

where `button_mask` includes:

- `01` left
- `02` right
- `04` middle
- `08` back
- `10` forward

The official binary serializes the delay as high byte then low byte.

## Safety design

Neutron M693 Hardware Studio never blindly writes a whole guessed configuration:

1. reads current bytes first;
2. keeps unknown bytes unchanged;
3. creates a JSON/raw backup before Apply;
4. writes only byte ranges that differ;
5. reads back and verifies after write.

## External corroboration used during RE

Protocol layout and EEPROM map were cross-checked against public reverse-engineered JM03/CX-family projects:

- `VitaminDB/ardor-mouse` (MIT)
- `LeadSun/lamzu-cfg` (MIT/Apache-2.0)

No vendor binaries from Redragon are redistributed in this archive.
