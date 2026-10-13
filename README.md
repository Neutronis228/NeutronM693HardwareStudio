## 2.6.3 — Macro Recovery development build (13 Oct 2026)

**Windows CI: passed.** [Compilation + parser + startup smoke tests](https://github.com/Neutronis228/NeutronM693HardwareStudio/actions/runs/38410448116). Download the CI-built EXE from the Actions artifact. The full source is on branch `development/v2.6.3-macro-recovery`.

**New:** Choose a physical mouse button, then choose an existing, checksum-valid onboard macro from a new dropdown. The app prepares a **profile-only** binding; it does not rewrite the selected macro's events. This is separate from recording/writing a new macro.

**Still open:** On real M693 hardware the macro-event write command is ACKed but event bytes do not persist. Do **not** treat a protocol ACK as a successful macro write. New-macro writing remains experimental; read-back is mandatory and alternate EEPROM offsets are not guessed. Binding an existing macro to another button still needs a real-device behavior check.

**Safety:** The personal ZIP may contain device pictures supplied by the mouse owner; proprietary Redragon imagery is not part of the MIT-licensed repository. Do not redistribute the vendor installer or artwork.

---

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
