NEUTRON M693 HARDWARE STUDIO 2.5.0 ULTIMATE
===========================================

Мета програми — повністю замінити штатну REDRAGON M693-RGB / OemDrv.exe для щоденного
налаштування M693, працюючи напряму з onboard HID/EEPROM, і додати функції, яких у штатній
програмі немає.

ПІДТВЕРДЖЕНО НА ВАШІЙ M693
---------------------------
- VID 25A7, PID FA7C у режимі 2.4 GHz;
- Windows split-HID: command FF02 / Feature=17 + response FF01 / Input=17;
- читання/запис onboard EEPROM профілю;
- battery / charging;
- 7 фізичних кнопок + 16-slot button matrix;
- macro references, DPI, RGB, sensor settings;
- current onboard profile command 0x0E.

ГОЛОВНЕ НОВЕ У 2.5 ULTIMATE
---------------------------
1. Повноцінний «Макроси PRO»:
   - 16 macro slots;
   - до 70 подій у slot;
   - mouse / keyboard / modifier / multimedia events;
   - delay для кожної події;
   - режими Repeat N / HOLD / Toggle / Until any key;
   - редагування існуючих onboard-макросів;
   - створення нового макросу та прив'язка до вибраної фізичної кнопки;
   - semantic read-back замість помилкового порівняння всього 0x180-byte tail.

2. Keyboard / Multimedia / DPI Lock прямо на вкладці «Кнопки»:
   - звичайна клавіша;
   - один або два modifiers + клавіша;
   - multimedia / browser / volume / calculator / explorer actions;
   - combo-запис у JM03 area 0x100 + slot*0x20 з checksum/read-back;
   - friendly DPI Lock для підтвердженого діапазону.

3. Профілі:
   - JSON Profile V2 з profile bytes + macros + combos + Palette Lab metadata;
   - local profile library;
   - import / export / RAW backup;
   - onboard profile selector 1..4;
   - команда перемикання 0x0F є guarded/experimental: після команди обов'язково перевіряється 0x0E та перечитується EEPROM.

4. Безпека запису:
   - нічого не пишеться до явного «ЗАСТОСУВАТИ»;
   - automatic backup перед Apply;
   - diff-write для профілю;
   - checksum + semantic read-back для macros/combos;
   - stale unreferenced pending slots не пишуться випадково;
   - unknown/rare records зберігаються byte-for-byte, якщо користувач їх явно не редагує.

5. UI:
   - темний custom navigation;
   - адаптивний header/status bar;
   - AutoScroll на довгих сторінках;
   - owner-drawn dark ComboBox/DataGridView;
   - коротша вкладка «Макроси PRO»;
   - виправлені вузькі елементи Keyboard/Combo;
   - default HOLD у Macro Studio одразу блокує неактуальне поле Repeat N.

ГАРЯЧІ КЛАВІШІ
--------------
F5      — пересканувати M693
Ctrl+R  — прочитати з миші
Ctrl+S  — застосувати підготовлені зміни
Ctrl+E  — експорт профілю
Ctrl+Z  — скасувати локальні зміни

ПЕРШИЙ ЗАПУСК
-------------
1. Розпакуйте архів у НОВУ папку.
2. Запустіть BUILD_WINDOWS.bat.
3. Повністю закрийте штатний OemDrv.exe через tray.
4. Запустіть NeutronM693HardwareStudio.exe.
5. Зачекайте автопошуку. F5 / «Пересканувати» лишається резервним ручним режимом.
6. Перевірте «Огляд»: пристрій, батарея, profile index, повний decoded profile.
7. Перед першим новим записом створіть окремий RAW backup.
8. Змінюйте потрібне й лише після перевірки pending state натискайте «ЗАСТОСУВАТИ».

ЩО З ОРИГІНАЛЬНОЮ ПРОГРАМОЮ
----------------------------
OemDrv.exe та InitSetup.dll НЕ є runtime-залежностями Hardware Studio. Протокол відтворений через
Windows HID/SetupAPI. У ReferenceOriginal збережені лише довідкові Cfg.ini/text.xml та дозволені
локальні assets із наданого користувачем пакета для parity-аудиту.

RGB / PALETTE LAB
-----------------
Реальний підтверджений onboard RGB block M693 містить один mode + один base RGB + speed + brightness.
Тому Palette Lab з кількома кольорами наразі зберігається у Neutron Profile V2 та показує preview,
але програма НЕ робить небезпечне постійне перезаписування EEPROM за таймером. Для справжньої live
multi-color палітри потрібна окрема підтверджена RAM/live RGB command; поки її не знайдено, Hardware
Studio не вигадує протокол.

ЧЕСНІ НЕЗАВЕРШЕНІ ДІЛЯНКИ
-------------------------
- точні friendly encodings для RGB ON/OFF / RGB Switch button actions ще не підтверджені;
- firmware updater навмисно не активовано без повного update protocol;
- command 0x0F для onboard profile switching підтверджений у близьких JM03/Nordic implementation,
  але на M693 його треба вважати experimental до вашого першого успішного тесту.

Детальніше:
- FEATURE_MATRIX_UA.txt
- ORIGINAL_PARITY_AUDIT_UA.txt
- RE_PROTOCOL_NOTES_UA.md
- TEST_PLAN_UA.txt
