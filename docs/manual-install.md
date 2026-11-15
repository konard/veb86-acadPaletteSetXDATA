# Ручная установка и запуск AutoCAD с чертежом

## Меню и лента удалены (задача #53)

В задаче #39 плагин добавлял в строку меню меню **Bobrov** и на ленту вкладку **Bobrov** с
кнопками «Включить»/«Выключить» (команды `XDATAPALETTE_ON` / `XDATAPALETTE_OFF`) и при первой
загрузке включал строку меню (`MENUBAR=1`). По решению задачи #53 всё, что связано с меню и
лентой AutoCAD, из модуля убрано:

- меню **Bobrov**, вкладка ленты **Bobrov**, их значки и установка (`BobrovUi`,
  `BobrovMenuInstaller`, `BobrovRibbon`, `BobrovInterface`, `BobrovInstallScheduler`);
- команды `XDATAPALETTE_ON` и `XDATAPALETTE_OFF` (в `PackageContents.xml` их тоже нет);
- изменение `MENUBAR` и отслеживание смены рабочего пространства (`WSCURRENT`);
- ссылка выпускаемой DLL на `AdWindows` (API ленты).

Панелью управляет только команда командной строки **`XDATAPALETTE`** — переключатель:
показывает панель «Свойства XDATA» (при первом вызове создаёт её) или скрывает открытую.
Кнопки меню и ленты создаёт отдельный модуль меню AutoCAD 2020–2024; его кнопка вызывает
`XDATAPALETTE`. Техническое задание на этот модуль — [menu-module-spec.md](menu-module-spec.md).

## Запуск AutoCAD с чертежом (задача #47)

Симптом: при запуске AutoCAD с открытием чертежа (двойной щелчок по DWG, `acad.exe чертёж.dwg`)
в командной строке мерцало, иногда по кругу, «Выберите объекты», и чертёж не открывался, пока
запрос не отменяли правой кнопкой мыши.

Причина. С задачи #39 пакет загружает плагин при запуске AutoCAD (`LoadOnAutoCADStartup`), а
меню, `MENUBAR`, вкладка ленты и сообщение о загрузке выполнялись на первом `Application.Idle`.
`Idle` возникает всякий раз, когда очередь сообщений пуста, — в том числе пока стартовый чертёж
ещё открывается и пока команда, сценарий или LISP (`acaddoc.lsp`, `S::STARTUP`) ждут ввода.
Изменение строки меню через COM и `MENUBAR` в этот момент вмешивалось в активную команду.
Кроме того, изменение строки меню и `MENUBAR` вызывает `WSCURRENT`, а `WSCURRENT` снова
запускал полную переустановку меню на следующем `Idle`, — переустановка могла идти по кругу
(мерцание). До задачи #39 плагин загружали вручную `NETLOAD` в уже открытом чертеже,
поэтому ошибки не было.

Исправление (задача #47):

- Вся работа при запуске (сообщение о загрузке, меню, `MENUBAR`, вкладка ленты) ждёт `Idle`,
  на котором есть активный чертёж, ни один чертёж не открывается (между `DocumentCreateStarted` и
  `DocumentCreated` / `DocumentCreationCanceled`), `Application.IsQuiescent` и
  `Editor.IsQuiescent` истинны, а `Document.CommandInProgress` пуст (`StartupReadiness`, Core).
  До этого плагин ничего не меняет и ничего не пишет в командную строку.
- `BobrovInstallScheduler` (Core) игнорирует `WSCURRENT`, вызванный самой установкой; после
  `WSCURRENT` меню и вкладка только проверяются (`BobrovMenuInstaller.IsInstalled`,
  `BobrovRibbon.Find`) и добавляются, если их нет. Повторная проверка ничего не меняет,
  поэтому цикла нет.
- `MENUBAR=1` устанавливается один раз за сеанс и только если строка меню скрыта.

С задачи #53 меню, `MENUBAR` и вкладки ленты нет, `BobrovInstallScheduler` удалён: при запуске
плагин только выводит сообщение о загрузке, и оно по-прежнему ждёт «тихого» `Idle`
(`StartupReadiness`).

Диагностика: при переменной окружения `XDATAPALETTE_TRACE=1` (по умолчанию выключена) решения
записываются в `%TEMP%\acadPaletteSetXDATA-trace.log` и в вывод отладчика (DebugView) —
чего ждёт плагин («чертёж открывается», «выполняется команда …») перед сообщением о загрузке.
В командную строку трассировка не пишет.

## Ручная установка без установщика

Каждый Release содержит архив `acadPaletteSetXDATA-manual-install-v<версия>.zip`
(по образцу `Teplov-Print-manual-install-v<версия>.zip` из
[TeplovPrint](https://github.com/veb86/TeplovPrint-AutoCAD)):

```text
AppData/Roaming/Autodesk/ApplicationPlugins/acadPaletteSetXDATA.bundle/PackageContents.xml
AppData/Roaming/Autodesk/ApplicationPlugins/acadPaletteSetXDATA.bundle/Contents/AutoCAD2021-2024/acadPaletteSetXDATA.dll
INSTALL.md
install.cmd
uninstall.cmd
LICENSE
release-manifest.txt
```

`install.cmd` копирует `acadPaletteSetXDATA.bundle` в
`%APPDATA%\Autodesk\ApplicationPlugins` без прав администратора и снимает отметку
«загружено из интернета»; AutoCAD загружает пакет автоматически при запуске.
Тот же результат даёт копирование папки `AppData` из архива в `%USERPROFILE%`.
Пошаговая инструкция для пользователя — [installer/manual/INSTALL.md](../installer/manual/INSTALL.md)
(входит в архив).

[PackageContents.xml](../installer/acadPaletteSetXDATA.bundle/PackageContents.xml):

- `RuntimeRequirements OS="Win64" SeriesMin="R23.1" SeriesMax="R24.3"` — AutoCAD 2020–2024
  x64 с .NET Framework 4.8; основная проверенная цель — AutoCAD 2021. AutoCAD 2025 и новее
  (.NET 8) пакет не загружают.
- `LoadOnAutoCADStartup="True"` — плагин загружается при запуске, команда `XDATAPALETTE`
  доступна сразу; список `Commands` позволяет загрузить плагин и по команде.
- `AppVersion` при упаковке заменяется номером выпуска (`v0.0.N` → `0.0.N`).

Архив `acadPaletteSetXDATA.zip` с одной DLL для `NETLOAD` сохранён без изменений.

## Проверки

| Что | Где |
| --- | --- |
| Запуск: ожидание «тихого» `Idle` (задача #47) | `tests/AcadPaletteSetXData.Core.Tests/StartupSchedulingTests.cs` |
| Сообщение о загрузке не пишется, пока открывается чертёж или идёт команда (задача #47); в сообщении нет меню и ленты Bobrov (задача #53) | `tests/AcadPaletteSetXData.AutoCAD.Tests/PaletteApplicationTests.cs` |
| Зарегистрирована только команда `XDATAPALETTE`, она переключает одну панель (задача #53) | `PaletteControllerTests.cs`, `tests/AcadPaletteSetXData.AutoCAD.Tests/PaletteApplicationTests.cs` |
| В выпускаемой DLL есть `XDATAPALETTE`, нет обработчиков `XDATAPALETTE_ON` / `_OFF`, типов `Bobrov*` и ссылки на `AdWindows` (задача #53) | `experiments/AcadPaletteSetXData.RegistrationSmoke` |
| Состав архива ручной установки, `PackageContents.xml` (только `XDATAPALETTE`, без `XDATAPALETTE_ON` / `_OFF`), CRLF, `release-manifest.txt`, публикация двух ZIP | `experiments/Test-AcadPaletteSetXDataRelease.ps1` |
| `install.cmd` / `uninstall.cmd` на временном `%APPDATA%` (Windows CI) | `experiments/Test-AcadPaletteSetXDataManualInstall.ps1` |

Автозагрузку пакета можно проверить только в установленном AutoCAD —
[сценарий ручной проверки](autocad-verification.md#ручная-установка-задача-39-без-меню-и-ленты-задача-53),
запуск с чертежом — [задача #47](autocad-verification.md#запуск-autocad-с-чертежом-задача-47).
