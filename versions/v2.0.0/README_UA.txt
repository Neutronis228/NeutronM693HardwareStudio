NEUTRON M693 HARDWARE STUDIO 2.0.0
=================================

Ця версія принципово інша за попередні DirectHID/RawInput тести.
Вона НЕ ловить XButton/Back/Forward для роботи макросу і НЕ запускає фоновий Windows-клікер.

Вона працює як заміна частини офіційної Redragon OemDrv.exe:
  Windows HID command interface -> EEPROM миші -> onboard profile / macro memory.

Підтверджено з наданої вами офіційної програми:
  VID  = 0x25A7
  PID  = 0xFA7B (кабель)
  PID2 = 0xFA7C (2.4G)
  Appdir/controller family = JM03
  Sensor = 0x3104

ВАЖЛИВО
-------
1. Нічого у старому Redragon профілі перед запуском міняти НЕ треба.
2. Перед читанням/Apply закрийте офіційну Redragon програму OemDrv.exe ПОВНІСТЮ через tray.
   Інакше дві програми можуть одночасно читати response report 0x09.
3. Спочатку натисніть "Знайти M693". Програма сама перебирає HID-колекції й шукає
   vendor command interface з 17-byte Feature Report 0x08, а НЕ звичайний mouse interface.
4. Після підключення програма читає onboard EEPROM і показує:
   - 7 фізичних кнопок M693 та їх поточні призначення;
   - повну 16-slot button matrix;
   - macro name/mode для вже прив'язаних макросів;
   - DPI stages та кольори;
   - RGB mode/color/speed/brightness;
   - polling rate;
   - debounce;
   - LOD;
   - sleep;
   - motion sync / angle snapping / ripple / LED-off-moving.

SMOOTH CLICK MACRO
------------------
1. У таблиці виберіть фізичну кнопку, наприклад #4.
   АБО клікніть поле "Вибір кнопки натисканням" і натисніть кнопку.
   Якщо на ній уже стоїть H_Clicking/H_building, capture намагається визначити її
   за поточним onboard macro pattern (потік L/R clicks), не змінюючи профіль.
2. Виберіть Лівий або Правий клік.
3. Вкажіть Min / Typical / Max, наприклад 10 / 45 / 150 ms.
4. Плавність, наприклад 88%.
5. Натисніть "Створити й прив'язати...".
6. Перевірте таблицю.
7. Лише після цього натисніть "ЗАСТОСУВАТИ В МИШКУ".

Макрос записується у firmware-compatible macro slot:
  base 0x300 + slot * 0x180
  name block = 31 bytes
  event block = count + 5-byte events + checksum
  mouse down = 84 <button-mask> 00
  mouse up   = 44 <button-mask> 00
  delay      = uint16 big-endian

Для прив'язки використовується action:
  06 <macro-slot> FE
де FE (254) = HOLD / repeat while the physical mouse button is held.

Патерн має 29 кліків (58 firmware events), потім мишка повторює його циклічно.
Плавні інтервали генеруються ще ДО запису в мишку. Runtime random generator у firmware
не підтверджений, тому onboard-поведінка — це циклічний плавний патерн, а не випадкове
число, яке Windows обчислює на кожному кліку.

BACKUP / ПРОФІЛІ
----------------
- "Експорт профілю" -> *.m693profile.json
- "Імпорт профілю" завантажує його лише в редактор, НЕ пише одразу в мишку.
- перед кожним Apply програма автоматично створює Backups\AUTO_BEFORE_APPLY_*.m693profile.json
- записуються лише змінені bytes.
- після запису робиться read-back verification.

ЯКЩО НЕ ЗНАЙДЕ HID
------------------
Відкрийте вкладку "HID лог / Діагностика" і надішліть весь текст M693_HID.log.
Тепер лог показує КОЖНУ HID collection з VID/PID, UsagePage, Input/Feature length і path.
Це дозволить точно побачити Col02/Col07/MI_01 на вашому конкретному підключенні.

ЗБІРКА
------
Запустіть BUILD_WINDOWS.bat.
Після BUILD OK запускайте NeutronM693HardwareStudio.exe або RUN.bat.

Це experimental reverse-engineered hardware tool. Автоматичний backup увімкнений навмисно.
