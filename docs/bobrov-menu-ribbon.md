# Меню и вкладка ленты «Bobrov», ручная установка (задача #39)

## Меню и лента

После загрузки плагина в AutoCAD 2021 появляются:

- меню **Bobrov** в строке меню с пунктами **«Включить палитру XDATA»** и
  **«Выключить палитру XDATA»**;
- вкладка ленты **Bobrov** с панелью **«Палитра XDATA»** и большими кнопками
  **«Включить»** и **«Выключить»**.

![Вкладка ленты Bobrov](screenshots/issue39-bobrov-ribbon.png)

Кнопки и пункты меню запускают команды:

| Команда | Действие |
| --- | --- |
| `XDATAPALETTE_ON` | показывает панель «Свойства XDATA» (создаёт её при первом вызове) |
| `XDATAPALETTE_OFF` | скрывает панель; если панель ещё не создавалась, ничего не делает |
| `XDATAPALETTE` | как и раньше, переключает панель (показать/скрыть) |

Повторное нажатие «Включить» не скрывает панель, а «Выключить» не открывает её —
в отличие от переключателя `XDATAPALETTE`. Макрос кнопок `^C^C_XDATAPALETTE_ON`
прерывает текущую команду, как обычные пункты меню AutoCAD.

### Как это устроено

- `BobrovUi` (Core) — общие названия, подписи, подсказки и макросы меню и ленты.
- `BobrovMenuInstaller` (Core) — меню через позднее связывание COM-объектов
  `Application.MenuGroups` / `Application.MenuBar` (как в TeplovPrint): меню создаётся в
  группе `ACAD` (или в первой группе, если `ACAD` нет), пункты пересоздаются при повторной
  загрузке без дублирования, затем меню вставляется в конец строки меню.
  Interop-сборки AutoCAD не нужны; в выпускаемой DLL по-прежнему только ссылки на
  `AcMgd`, `AcDbMgd`, `AcCoreMgd`, `AdWindows` и .NET Framework.
- `BobrovRibbon` (AutoCAD) — вкладка `RibbonTab` из `AdWindows.dll` с векторными значками
  (палитра с зелёной галочкой или красным крестиком), чётких при любом масштабе.
- `BobrovInterface` (AutoCAD) — при загрузке откладывает создание меню и вкладки до «тихого»
  `Idle` (задача #47, см. ниже): пакет автозагрузки загружает плагин до готовности главного окна
  и до открытия стартового чертежа. Если лента ещё не создана или закрыта (`RIBBONCLOSE`),
  вкладка добавляется после `ComponentManager.ItemInitialized` на следующем «тихом» `Idle`.
  Смена рабочего пространства (`WSCURRENT`) перезагружает меню и ленту из CUIX, поэтому
  меню и вкладка проверяются снова и добавляются, только если их нет.

Принятые решения:

- Меню и вкладка не сохраняются в CUIX пользователя: плагин добавляет их при каждой загрузке,
  а удаление плагина не оставляет следов в настройках интерфейса.
- В AutoCAD 2021 строка меню по умолчанию скрыта, поэтому при первой загрузке в сеансе
  плагин устанавливает `MENUBAR=1` (как TeplovPrint) — только если строка меню скрыта (`MENUBAR=0`). Скрыть строку меню можно командой
  `MENUBAR` → `0`; вкладка ленты **Bobrov** останется доступной.
- Ошибки построения меню или ленты не мешают загрузке плагина: причина выводится в командную
  строку, команды `XDATAPALETTE*` продолжают работать.

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

Исправление:

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

Диагностика: при переменной окружения `XDATAPALETTE_TRACE=1` (по умолчанию выключена) решения
записываются в `%TEMP%\acadPaletteSetXDATA-trace.log` и в вывод отладчика (DebugView) —
чего ждёт плагин («чертёж открывается», «выполняется команда …»), когда добавлено меню,
когда пропущен собственный `WSCURRENT`. В командную строку трассировка не пишет.

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
- `LoadOnAutoCADStartup="True"` — плагин загружается при запуске, поэтому меню и вкладка
  **Bobrov** доступны сразу; список `Commands` позволяет загрузить плагин и по команде.
- `AppVersion` при упаковке заменяется номером выпуска (`v0.0.N` → `0.0.N`).

Архив `acadPaletteSetXDATA.zip` с одной DLL для `NETLOAD` сохранён без изменений.

## Проверки

| Что | Где |
| --- | --- |
| Подписи, макросы и построение меню (поддельные COM-объекты, повторная загрузка, ошибки) | `tests/AcadPaletteSetXData.Core.Tests/BobrovUiTests.cs` |
| Запуск: ожидание «тихого» `Idle`, игнорирование собственного `WSCURRENT`, отсутствие цикла переустановки (задача #47) | `tests/AcadPaletteSetXData.Core.Tests/StartupSchedulingTests.cs` |
| Сообщение о загрузке не пишется, пока открывается чертёж или идёт команда (задача #47) | `tests/AcadPaletteSetXData.AutoCAD.Tests/PaletteApplicationTests.cs` |
| `XDATAPALETTE_ON` / `_OFF` / переключатель на одной панели | `PaletteControllerTests.cs`, `tests/AcadPaletteSetXData.AutoCAD.Tests/PaletteApplicationTests.cs` |
| Регистрация `XDATAPALETTE_ON` / `_OFF` в выпускаемой DLL | `experiments/AcadPaletteSetXData.RegistrationSmoke` |
| Вкладка ленты с настоящим `AdWindows.dll` (Windows CI): структура, команды кнопок, рендер | `experiments/AcadPaletteSetXData.RibbonSmoke` |
| Состав архива ручной установки, `PackageContents.xml`, CRLF, `release-manifest.txt`, публикация двух ZIP | `experiments/Test-AcadPaletteSetXDataRelease.ps1` |
| `install.cmd` / `uninstall.cmd` на временном `%APPDATA%` (Windows CI) | `experiments/Test-AcadPaletteSetXDataManualInstall.ps1` |

Строку меню и автозагрузку пакета можно проверить только в установленном AutoCAD —
[сценарий ручной проверки](autocad-verification.md#меню-и-вкладка-bobrov-ручная-установка-задача-39),
запуск с чертежом — [задача #47](autocad-verification.md#запуск-autocad-с-чертежом-задача-47).
