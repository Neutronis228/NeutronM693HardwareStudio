NEUTRON M693 HARDWARE STUDIO 2.4.0 COMPLETE
===========================================

Ціль цієї гілки — бути повноцінною заміною штатної REDRAGON M693-RGB програми для щоденного
налаштування миші, але з безпечнішим direct-HID підходом і додатковими Neutron-функціями.

ПІДТВЕРДЖЕНО НА ВАШІЙ M693
---------------------------
- VID 25A7, PID FA7C у 2.4 GHz режимі;
- command HID: FF02 / Feature=17;
- response HID: FF01 / Input=17;
- onboard EEPROM profile;
- battery/charging;
- кнопки, DPI, RGB, sensor settings і macro references.

ЩО НОВОГО У 2.4 COMPLETE
-------------------------
1. Новий адаптивний dark UI без білого штатного WinForms tab-strip.
2. Верхня панель поділена на живий статус і компактну сітку дій.
3. Нижній status bar завжди показує M693 / Battery / Profile / Pending changes.
4. Apply активний лише коли справді є зміни.
5. Кнопка "Скасувати зміни" повертає редактор до останнього прочитаного стану без запису в мишку.
6. Попередження при закритті програми з незаписаними змінами.
7. Overview перероблений у cards; батарея має власний темний meter.
8. Додана сторінка "Можливості" з прозорим станом parity зі штатним OemDrv.
9. Додано Double Left, Double Right і Triple Left як справжні onboard macro actions.
10. Збережені всі захисти 2.3: backup, diff-write, semantic macro read-back, unknown-byte preservation.

ГАРЯЧІ КЛАВІШІ
--------------
F5      — пересканувати M693
Ctrl+R  — прочитати з миші
Ctrl+S  — застосувати підготовлені зміни
Ctrl+E  — експорт профілю
Ctrl+Z  — скасувати локальні зміни

ПЕРШИЙ ЗАПУСК
-------------
1. Розпакувати в НОВУ папку.
2. Запустити BUILD_WINDOWS.bat.
3. Повністю закрити OemDrv.exe через tray.
4. Запустити NeutronM693HardwareStudio.exe.
5. M693 має знайтися автоматично; ручний F5/"Пересканувати" лишився як резерв.
6. Перевірити Огляд -> Battery / Profile / Buttons.
7. Змінити потрібні параметри.
8. Перед Apply перевірити жовтий pending-status.
9. Натиснути ЗАСТОСУВАТИ. Перед записом створиться backup, після запису — read-back verification.

ЩО З ОРИГІНАЛЬНОЮ ПРОГРАМОЮ
----------------------------
OemDrv.exe, InitSetup.dll та інші vendor binaries НЕ потрібні для запуску Hardware Studio.
У комплекті збережені тільки вибрані візуальні assets та ReferenceOriginal/Cfg.ini/text.xml для
сумісності/аудиту, отримані з наданого користувачем архіву.

ЧЕСНЕ ОБМЕЖЕННЯ
---------------
Основні щоденні функції оригінального софту вже закриті. Деякі рідкісні original actions
(Single key / Multimedia / RGB switch / Profile switch / DPI Lock) поки читаються й гарантовано
зберігаються byte-for-byte, але не всі мають окремий friendly editor. Firmware updater навмисно
не активовано до повного підтвердження update protocol. Див. ORIGINAL_PARITY_AUDIT_UA.txt.
