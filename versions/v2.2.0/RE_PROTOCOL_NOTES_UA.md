# ВАЖЛИВЕ УТОЧНЕННЯ ДЛЯ WINDOWS — v2.2.0

Живий лог конкретної M693 показав, що Windows розбиває vendor-протокол JM03 на дві top-level HID collections одного `MI_01`:

- `COL07`: Usage Page `FF02`, `FeatureReportByteLength=17`, `Input=0` — сюди надсилається Feature report `0x08`;
- `COL05`: Usage Page `FF01`, `InputReportByteLength=17`, `Feature=0` — звідси читається response report `0x09`.

Тому старий критерій `Feature=17 AND Input>=17` для ОДНІЄЇ collection був принципово неправильним. v2.2.0 відкриває дві handles, зв'язує їх за VID/PID + MI/group path і тільки після цього робить EEPROM probe.

---

# Redragon M693-RGB — reverse-engineering notes (2.2.0 AUTO HID)

Основа: файли з наданого користувачем встановленого пакета `REDRAGON M693-RGB.zip`.

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

`OemDrv.exe` — native PE32 x86. У ньому присутні імпорти/рядки, пов'язані з
`HidD_SetFeature`, `HidD_GetHidGuid`, `HidD_GetPreparsedData`, `HidD_GetAttributes`,
`HidP_GetCaps`, SetupAPI enumeration, `GetPower`, `GetCurProfile`, `SetCurProfile`,
`ReadEEPROM`, `WriteEEPROM`.

Це підтверджує, що конфігурування миші йде через стандартний Windows HID stack,
а не через окремий proprietary kernel driver.

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

Використані/закладені команди:

- `03` — link/connect status
- `04` — battery / charging state
- `07` — write EEPROM
- `08` — read EEPROM
- `09` — factory reset
- `0E` — current profile query

`07/08` є основою профільного редактора. `04` використовується Dashboard для показу
заряду. `09` винесено за подвійне підтвердження й backup. `0E` показується лише якщо
конкретна firmware повертає коректну відповідь.

## Profile map

- `0x00` polling pair
- `0x02` DPI count pair
- `0x04` active DPI pair
- `0x0A` LOD pair
- `0x0C..0x2B` 8 DPI records × 4 bytes
- `0x2C..0x4B` 8 DPI colors × 4 bytes
- `0x4C..0x53` DPI effect (read/preserve)
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

Final `Kx_1` field maps physical controls to one-based matrix positions:

- button 1 -> slot 0
- button 2 -> slot 1
- button 3 -> slot 2
- button 4 -> slot 4 (`K4_1 ... 0x05`)
- button 5 -> slot 3 (`K5_1 ... 0x04`)
- button 6 -> slot 9 (`... 0x0A`)
- button 7 -> slot 10 (`... 0x0B`)

## Verified / cross-checked action records

```text
00 00 00  Disabled
01 01 00  Left
01 02 00  Right
01 04 00  Middle
01 08 00  Back
01 10 00  Forward
02 01 00  DPI Loop
02 02 00  DPI +
02 03 00  DPI -
03 01 00  Wheel Left
03 02 00  Wheel Right
04 xx yy  Fire (interval/repeat)
05 00 00  key combo container
06 mm rr  Macro slot/mode
07 00 00  Polling-rate switch
0A xx 00  DPI Lock family
0B 01 00  Wheel Up
0B 02 00  Wheel Down
```

Unknown existing records are preserved exactly unless the user explicitly changes that slot.

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

Mouse events used by Smooth Macro:

```text
press   = 0x84, button_mask, 0x00
release = 0x44, button_mask, 0x00
```

button mask:

- `01` left
- `02` right
- `04` middle
- `08` back
- `10` forward

## Safety design

Neutron M693 Hardware Studio does not blindly write a guessed full image:

1. reads current bytes first;
2. keeps unknown bytes unchanged;
3. creates a JSON/raw backup before Apply;
4. writes only byte ranges that differ;
5. reads back and verifies after write;
6. prevents Apply if no physical button remains mapped to plain Left Click;
7. Factory Reset requires two confirmations and a pre-reset backup.

## Visual assets

2.2.0 AUTO HID includes only a small set of visual resources copied from the user-provided
original Redragon installation (`mouse_nr.png`, `mouse_led.png`, battery icons, `appico.ico`)
to give the replacement utility a recognisable M693 UI. `OemDrv.exe`, `InitSetup.dll` and
other vendor binaries are not linked or loaded by Hardware Studio.

## Cross-checks during RE

The JM03-style packet shape and EEPROM/button/macro concepts were cross-checked against public
reverse-engineered tools for related controller families, including `VitaminDB/ardor-mouse`
and `LeadSun/lamzu-cfg`. The supplied M693 `Cfg.ini` and observed M693 binary remain the primary
source for M693-specific VID/PID, button-slot mapping, sensor/range tables and UI capabilities.
