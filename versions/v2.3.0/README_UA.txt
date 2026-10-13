NEUTRON M693 HARDWARE STUDIO 2.3.0 PRO
=========================================

Ця версія побудована навколо реального vendor HID протоколу REDRAGON TRIDENT M693/JM03.
Вона НЕ вимагає перепризначати кнопки на Back/Forward і не використовує RawInput як основу
для програмування миші.

ПІДТВЕРДЖЕНО НА ВАШІЙ M693
---------------------------
VID 25A7, PID FA7C (2.4 GHz), command collection FF02 Feature=17,
response collection FF01 Input=17. Профіль EEPROM, заряд, кнопки, DPI, RGB і macro refs
реально читаються з миші.

ГОЛОВНЕ У 2.3.0 PRO
-------------------
1. Виправлено запис Smooth Macro.
   v2.2 намагалася порівнювати весь macro slot 0x180 байт. JM03/OemDrv фактично працює
   з двома змістовними блоками: ім'я та event-block. Хвіст slot firmware може залишати або
   нормалізувати, через що виникало помилкове "Read-back macro slot ... не збігся".
   2.3 пише та перевіряє лише змістовні блоки і не зануляє приватний хвіст slot.

2. Захист від "зайвого slot 0".
   На Apply пишуться лише pending macro slots, які реально referenced поточним профілем.
   Старі/випадкові pending записи пропускаються і логуються.

3. Виправлено backup snapshot.
   Список extra macro slots копіюється до очищення pending dictionary, тому backup справді
   включає саме ті slot-и, які збираємося змінювати.

4. Smooth Macro PRO.
   - Left / Right click;
   - Min / Typical / Max;
   - Smoothness;
   - 5..35 кліків у патерні (до 70 event-ів, межа формату JM03);
   - HOLD FE — працює тільки поки кнопка затиснута;
   - графік кривої інтервалів;
   - якщо в мишці вже є справжній left/right macro, 2.3 намагається взяти його press/release
     event bytes як hardware template замість сліпого хардкоду.

5. Адаптивніший інтерфейс.
   - кнопкова сторінка має окремий вертикальний/horizontal scroll у лівій і правій частинах;
   - вкладки змінюють ширину при resize;
   - DPI/RGB/Sensor/Profile/Windows/RAW сторінки мають AutoScroll;
   - правий Smooth Macro panel перебудовує ширину полів;
   - Dashboard text має обидві прокрутки і більше місця.

6. Dashboard тепер показує повний технічний звіт:
   - polling, debounce, LOD, sleep;
   - Motion Sync / Angle Snapping / Ripple / LED while moving;
   - усі 8 DPI + кольори;
   - усі 7 фізичних кнопок;
   - RGB mode/base color;
   - referenced macro slots, checksum, events, dominant output;
   - pending зміни і кількість змінених profile bytes.

7. Neutron Palette Lab.
   Можна зібрати 2..8 кольорів, бачити gradient preview, вибрати сцену і період та зберегти
   цю сцену разом із .m693profile.json. Перший колір можна одним кліком перенести у onboard RGB.

   ВАЖЛИВО: відомий onboard RGB блок M693 0xA0..0xA6 має тільки один базовий RGB колір +
   firmware effect. Довільна 2/5-кольорова палітра не поміщається в цей блок. Програма НЕ
   анімує її частими EEPROM-записами, бо це було б неправильним для ресурсу пам'яті. Для
   справжньої кастомної onboard палітри потрібна окрема RAM/firmware команда, якщо вона існує.

ПЕРШИЙ ТЕСТ ПІСЛЯ ОНОВЛЕННЯ
----------------------------
1. Розпакувати у НОВУ папку.
2. BUILD_WINDOWS.bat.
3. Закрити OemDrv.exe через tray.
4. Запустити програму — M693 має знайтися автоматично.
5. Натиснути "Прочитати" (якщо профіль не прочитався автоматично).
6. На вкладці "Кнопки" вибрати Бокова кнопка 4.
7. Створити Smooth Macro.
8. Перевірити Preview і графік.
9. Натиснути "ЗАСТОСУВАТИ".
10. Після успіху перевірити фізично: затиснути кнопку 4 -> кліки, відпустити -> stop.

Якщо запис зупиниться, надішліть папку Logs і останній AUTO_BEFORE_APPLY backup із папки Backups.
Там уже буде достатньо даних, щоб порівняти target/read-back по конкретних адресах.
