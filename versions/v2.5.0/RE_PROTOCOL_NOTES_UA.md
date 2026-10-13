# ВАЖЛИВЕ УТОЧНЕННЯ ДЛЯ WINDOWS — v2.5.0 ULTIMATE

Живий лог конкретної M693 показав, що Windows розбиває vendor-протокол JM03 на дві top-level HID collections одного `MI_01`:

- `COL07`: Usage Page `FF02`, `FeatureReportByteLength=17`, `Input=0` — сюди надсилається Feature report `0x08`;
- `COL05`: Usage Page `FF01`, `InputReportByteLength=17`, `Feature=0` — звідси читається response report `0x09`.

Тому старий критерій `Feature=17 AND Input>=17` для ОДНІЄЇ collection був принципово неправильним. v2.2+ відкриває дві handles, зв'язує їх за VID/PID + MI/group path і тільки після цього робить EEPROM probe.

---

# Redragon M693-RGB — reverse-engineering notes (2.5.0 ULTIMATE)

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
- `0F` — set active onboard profile (guarded/experimental on M693; після запису обов'язково перевіряється через 0E)

`07/08` є основою профільного редактора. `04` використовується Dashboard для показу
заряду. `09` винесено за подвійне підтвердження й backup. `0E` читає active profile.
`0F` додано як guarded profile switch за cross-check із близькими JM03/Nordic implementations:
операція вважається успішною тільки якщо наступний `0E` повернув очікуваний index.

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


## Keyboard / multimedia combo area (2.5)

Для action type `05` використовується окрема область combo records:

```text
combo_base = 0x100
combo_slot = 0x20 bytes
address = 0x100 + button_slot * 0x20
```

Логічний record:

```text
[count][3-byte event x count][checksum]
```

У Hardware Studio події формуються явно (press/release), тому `count` = фактична кількість
3-byte events. Максимум у friendly editor — 6 подій.

Event flags cross-checked on the same controller family:

```text
0x80 = press
0x40 = release
0x01 = keyboard usage
0x02 = consumer/media usage
no type flag + B1 modifier mask = modifier event
```

Keyboard/consumer usage зберігається little-endian у B1/B2. При Apply combo пишеться окремо,
перевіряється checksum/read-back і лише після цього profile button record type 05 вважається
підготовленим.

## Macro Studio (2.5)

2.5 додає повний friendly editor усіх 16 macro slots: mouse/keyboard/modifier/multimedia events,
затримки, reorder/delete, і режими `Repeat N`, `HOLD=FE`, `Toggle=FD`, `Until-key=FF`.
Explicitly edited/imported macro slot може бути записаний навіть якщо він тимчасово не referenced
button matrix; випадковий stale pending slot без explicit write — не пишеться.

## Profile V2 / onboard profiles (2.5)

Neutron profile file V2 зберігає profile bytes + macro slots + combo slots + Palette Lab metadata.
Import V1 лишається сумісним. Onboard selector 1..4 використовує `0x0F` тільки після перевірки
відсутності pending changes; після switch обов'язково читається `0x0E` і весь active EEPROM profile.

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

2.3.0 PRO includes only a small set of visual resources copied from the user-provided
original Redragon installation (`mouse_nr.png`, `mouse_led.png`, battery icons, `appico.ico`)
to give the replacement utility a recognisable M693 UI. `OemDrv.exe`, `InitSetup.dll` and
other vendor binaries are not linked or loaded by Hardware Studio.

## Cross-checks during RE

The JM03-style packet shape and EEPROM/button/macro concepts were cross-checked against public
reverse-engineered tools for related controller families, including `VitaminDB/ardor-mouse`
and `LeadSun/lamzu-cfg`. The supplied M693 `Cfg.ini` and observed M693 binary remain the primary
source for M693-specific VID/PID, button-slot mapping, sensor/range tables and UI capabilities.


## 2.3.0 — важлива поправка macro write/read-back

Попередня 2.2 формувала повний 0x180-byte образ macro slot і намагалася занулити/перевірити
весь хвіст. Це не відповідає поведінці JM03/OemDrv-подібних утиліт: логічно записуються
два variable-length блоки:

1. `slot_base + 0`: `name_len + name`;
2. `slot_base + 31`: `event_count + events + checksum`.

Байти після checksum не є частиною поточного macro record і firmware може зберігати/
нормалізувати їх по-своєму. Тому 2.3:

- НЕ зануляє весь 0x180 slot;
- пише лише name block та event block;
- read-back порівнює тільки ці meaningful ranges;
- додатково парсить checksum/event count;
- pending slot пишеться лише якщо поточний profile реально посилається на нього.

Це прямо усуває false negative `Read-back macro slot ... не збігся`, який виник під час
першого реального Apply на M693.

Smooth Macro також намагається знайти вже існуючий onboard macro з тим самим dominant
mouse output і копіює з нього 3-byte press/release event template. Це зменшує залежність від
хардкодженого припущення про event flags конкретної firmware revision.


## 2.5.0 ULTIMATE — Macro Studio / combo / profile pass

- custom dark navigation replaces the native white WinForms tab strip;
- pending-change state is explicit and Apply is disabled when there is nothing to write;
- Ctrl+Z discards local editor changes without touching the mouse;
- full Macro Studio edits 16 macro slots and up to 70 events per macro;
- friendly keyboard/modifier/media combo editor uses the 0x100 combo region;
- Profile V2 carries macros/combos/palette metadata;
- guarded active profile switch adds 0x0F + mandatory 0x0E verification;
- Double-left, Double-right and Triple-left are generated as onboard macro actions;
- rare unknown original actions remain byte-preserved until their exact encoding is confirmed.
