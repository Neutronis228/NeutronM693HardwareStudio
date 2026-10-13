## Latest Windows-tested development build — 2.6.3 (not hardware-stable)

**Windows CI SUCCESS:** compiled EXE, protocol self-test, and no-device UI smoke test. [View build](https://github.com/Neutronis228/NeutronM693HardwareStudio/actions/runs/38410448116).

- **Source and version history:** [development/v2.6.3-macro-recovery](https://github.com/Neutronis228/NeutronM693HardwareStudio/tree/development/v2.6.3-macro-recovery) and [draft PR #3](https://github.com/Neutronis228/NeutronM693HardwareStudio/pull/3).
- **New:** choose a checksum-verified **existing mouse macro** and assign it to a physical button without rewriting the macro bytes.
- **Unresolved:** writing new macro EVENT/NAME regions still ACKs but **fails EEPROM read-back**, even for a 2-event macro. Do not repeat experimental writes. A short verified official OemDrv USB capture is needed to compare commands. [Investigation and safe capture procedure](https://github.com/Neutronis228/NeutronM693HardwareStudio/blob/development/v2.6.3-macro-recovery/docs/MACRO_WRITE_RESEARCH_UA.md).
- User-defined 2–8-color palette is still a local preview, **not** confirmed onboard RGB hardware animation.
- The development EXE is *not digitally signed* and has **not** passed the physical M693 macro-write acceptance test.

# Neutron M693 Hardware Studio

> **Unofficial community application** for configuring the Redragon TRIDENT M693-RGB mouse on Windows. Development is experimental; **do not assume all builds are stable**.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Windows](https://img.shields.io/badge/platform-Windows-0078d4)](#build-and-run)

**Автор / author:** [Neutronis228](https://github.com/Neutronis228)  
**Ліцензія / License:** MIT (source code; retain copyright notice).  
**Увага:** проєкт не пов'язаний із Redragon. Redragon trademarks, vendor executables and original graphics are *not* covered by our MIT license.

## Українською

**Neutron M693 Hardware Studio** — незалежна програма для керування мишкою Redragon M693-RGB без потреби запускати штатний OemDrv. Мета — прочитати та змінювати onboard-профілі, кнопки, макроси, DPI, параметри сенсора й RGB через HID; додати діагностику, резервні копії та розширений редактор макросів.

### Основні можливості, над якими працюємо

- Автоматичне та ручне визначення USB/2.4 GHz M693, контроль з'єднання і заряду.
- Читання 7 фізичних кнопок та внутрішньої матриці призначень.
- Призначення натискань, DPI/сенсорних функцій, комбінацій клавіш і макросів.
- Onboard Smooth Macro: плавні інтервали та повторення при утриманні кнопки.
- Профілі, імпорт/експорт, резервні копії та перевірка даних після запису.
- RGB: штатні режими та редактор палітр для попереднього перегляду; довільні багатоколірні переходи **ще не підтверджені прошивкою**.
- Діагностика HID, зокрема RAW-протоколу.

### Підтверджена діагностика підключення

На випробуваному приймачі 2.4 GHz спостерігалися VID `25A7`, PID `FA7C` і розділені HID-колекції: **COL07 / FF02, feature 17 байт** для команд та **COL05 / FF01, input 17 байт** для відповідей. Це не гарантія підтримки кожної іншої мишки з подібним USB ID.

### Історія версій

| Версія | Вміст | Поточний статус |
|---|---|---|
| 1.0.0 | Smooth Clicker | Прототип |
| 1.1.0 | Direct HID | Експеримент |
| 1.2.0 | RAW input FIX2 | Експеримент |
| 2.0.0 | Початкова Hardware Studio | Експеримент |
| 2.1.0 | Інтерфейс/налаштування | HID-пошук не працював |
| 2.2.0 | Розділений HID command/response | Пошук, читання кнопок і заряду підтверджено |
| 2.3.0 | Новий запис макросів та UI | Є повідомлення про проблеми запуску |
| 2.4.0 | Додатковий UI, дії кнопок | Є повідомлення про проблеми запуску |
| 2.5.0 | Макроси PRO, keyboard/media, профілі V2 | **Є повідомлення про проблеми запуску; не позначати stable** |

### Збірка та діагностика

Проєкт використовує WinForms / .NET Framework 4.x. Для історичних версій запускайте `BUILD_WINDOWS.bat` на Windows або GitHub Actions Windows build після імпорту відповідних вихідних файлів.

Якщо EXE не відкривається, перевірте build log і **Windows Event Viewer → Windows Logs → Application** (події .NET Runtime / Application Error). При створенні Issue додайте Windows build, версію програми, спосіб підключення та журнал подій; приховайте серійні номери пристроїв.

Перед будь-яким записом збережіть оригінальний профіль. Не запускайте одночасно Redragon OemDrv і Neutron M693 Studio. Невідомі команди firmware не тестуйте без фізичного резервного пристрою.

## English

An experimental, independent Windows WinForms controller for the Redragon TRIDENT M693-RGB. Project goals: read/write supported onboard settings via vendor HID, manage buttons, macro patterns, DPI, sensor, RGB, profiles, battery status and backups. **Device discovery and readout were observed on the tested 2.4 GHz receiver; later releases require Windows startup and hardware verification.**

### Development status and contributing

This is a community work-in-progress, not a certified product or endorsed by the mouse manufacturer. Report bugs through [Issues](https://github.com/Neutronis228/NeutronM693HardwareStudio/issues) with reproducible steps and redacted logs. Contributions welcome under MIT, retaining attribution to the original author.

### Copyright / licensing

Copyright © 2026 **Neutronis228**. Original source code is distributed under the [MIT License](LICENSE). Redistributed copies must include the copyright and permission notice. Vendor binaries, configuration resources, and branded art may have different owners/licenses and should not be redistributed under MIT without permission.
