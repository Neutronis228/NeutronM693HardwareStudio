NEUTRON M693 HARDWARE STUDIO 2.2.0 AUTO HID
====================================

Це вже не RawInput/Back/Forward-клікер. Програма працює напряму з vendor HID command
interface REDRAGON TRIDENT M693 і читає/пише onboard EEPROM та macro memory.

ПІДТВЕРДЖЕНО З ВАШОГО ОРИГІНАЛЬНОГО REDRAGON M693-RGB
------------------------------------------------------
VID  = 0x25A7
PID  = 0xFA7B (USB cable)
PID2 = 0xFA7C (2.4 GHz receiver)
Controller family / Appdir = JM03
Sensor = 0x3104
Command Feature Report = 0x08, 17 bytes
Response Input Report = 0x09

2.2.0 AUTO HID — ЩО Є
----------------
1. Стильний темний інтерфейс у стилі Redragon + зображення саме M693 із вашого пакета.
2. Dashboard:
   - підключений VID/PID та HID collection;
   - режим USB / 2.4 GHz;
   - заряд акумулятора у %;
   - індикатор заряджання;
   - індекс onboard profile, якщо firmware відповідає;
   - короткий зведений список всіх фізичних кнопок і їх призначень.
3. Всі 7 фізичних кнопок M693 + повна 16-slot button matrix.
4. Вибір кнопки натисканням: якщо firmware уже перетворює фізичну кнопку у macro burst,
   програма зіставляє результат із тим, що прочитано з onboard profile/macro slot.
5. Звичайні апаратні призначення:
   - Left / Right / Middle;
   - Back / Forward;
   - DPI Loop / DPI+ / DPI-;
   - Polling rate switch;
   - Wheel left/right/up/down;
   - Fire key (speed 3..255, repeat 0..3; 0 = while held);
   - Disable.
6. Smooth Click Macro:
   - Left або Right;
   - Min / Typical / Max interval;
   - Smoothness;
   - firmware HOLD mode FE — повторювати тільки поки кнопка затиснута;
   - виконується самою мишкою, Windows background clicker не потрібен.
7. DPI:
   - кількість stages;
   - активний stage;
   - значення DPI;
   - колір кожного stage.
8. RGB:
   - 7 режимів;
   - колір;
   - speed;
   - brightness;
   - LED-off-while-moving.
9. Sensor / performance:
   - polling 125/250/500/1000 Hz;
   - debounce;
   - LOD;
   - sleep;
   - Motion Sync;
   - Angle Snapping;
   - Ripple Control.
10. Local Profile Library:
   - Save named profile;
   - Load;
   - Delete;
   - profile містить onboard bytes + потрібні macro slots;
   - окремі Export/Import JSON лишились.
11. Windows / Pointer settings як у класичному OemDrv:
   - cursor speed;
   - double-click time;
   - wheel lines;
   - Enhance pointer precision / acceleration.
   Ця вкладка окрема: це налаштування Windows, а не EEPROM миші.
12. Advanced / RAW:
   - raw action bytes Type/Arg1/Arg2 для кожної фізичної кнопки;
   - відомі action type;
   - таблиця 16 macro slots;
   - читання всіх macro slots;
   - unknown action не знищується автоматично.
13. Safety:
   - backup перед Apply;
   - запис тільки змінених byte ranges;
   - read-back verification;
   - захист: хоча б одна фізична кнопка має залишитися Left click;
   - double-confirm Factory Reset + backup перед reset.

ЩО ЩЕ НЕ НАЗИВАЮ 100% ПАРИТЕТОМ З ОФІЦІЙНОЮ ПРОГРАМОЮ
-------------------------------------------------------
- Firmware updater офіційної програми ще не відтворений і навмисно не викликається.
- Частина рідкісних button actions (деякі multimedia/keyboard/RGB/profile-switch функції)
  ще не має дружнього GUI, бо їхні exact hw-коди треба підтвердити саме на M693.
  Вони не губляться: поточні raw bytes читаються/зберігаються, а Advanced/RAW дозволяє
  їх бачити й редагувати експертно.
- Повний загальний Macro Editor (довільні keyboard/media sequences з GUI) буде наступним
  шаром; зараз підтримано читання macro slots і наш onboard Smooth Click generator.

Тобто 2.2.0 AUTO HID уже покриває основні реальні налаштування миші та значно виходить за
рамки попереднього клікера, але я не буду неправдиво називати її 100% копією OemDrv,
доки ми не перевіримо firmware-відповіді на вашій конкретній M693.

ЧОМУ АРХІВ НЕ ВАЖИТЬ ДЕСЯТКИ МЕГАБАЙТ
--------------------------------------
Розмір не показує можливості. Hardware Studio використовує .NET Framework, WinForms,
Windows hid.dll/setupapi.dll і не тягне всередині власний runtime/UI engine.
Оригінальний OemDrv.exe містить native GUI/skin engine та інші службові частини.
У 2.2.0 AUTO HID додані лише корисні оригінальні графічні assets з вашого архіву (mouse/icon/
battery images), а OemDrv.exe та InitSetup.dll НЕ вбудовуються — наша програма працює
напряму з M693 і не залежить від запущеної офіційної утиліти.


АВТОМАТИЧНЕ ПІДКЛЮЧЕННЯ У 2.2.0
----------------------------------
Програма сама сканує M693 при запуску і далі раз на 2.5 секунди, доки миша не буде підключена.
Кнопка «Пересканувати» лишається як ручний резервний режим.

У Windows JM03 command protocol складається з двох HID collections: FF02 Feature=17 для TX report 0x08 і FF01 Input=17 для RX report 0x09. Саме це виправлено після живого логу M693.

ПЕРШИЙ ЗАПУСК
-------------
1. Розпакувати в НОВУ папку.
2. Запустити BUILD_WINDOWS.bat.
3. Має бути BUILD OK.
4. Повністю закрити OemDrv.exe через tray.
5. Запустити NeutronM693HardwareStudio.exe.
6. Натиснути «Знайти M693».
7. Натиснути «Прочитати з миші».
8. Спочатку нічого не Apply — перевірити Dashboard, Battery, Buttons, DPI, RGB, Sensor.
9. Зберегти RAW backup і named profile.
10. Лише потім тестувати зміну однієї кнопки або Smooth Macro.

ВАЖЛИВО
-------
Не запускайте одночасно OemDrv.exe і Hardware Studio під час читання/Apply: обидві програми
можуть читати один response report 0x09 з command HID interface.

Якщо HID не знайдений або battery/profile command не відповідає, відкрийте вкладку
«HID лог / Діагностика» та надішліть M693_HID.log. Саме цей лог потрібен для точного FIX.
