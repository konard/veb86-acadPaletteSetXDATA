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
- `BobrovInterface` (AutoCAD) — при загрузке откладывает создание меню и вкладки до первого
  `Idle`: пакет автозагрузки загружает плагин до готовности главного окна. Если лента ещё не
  создана или закрыта (`RIBBONCLOSE`), вкладка добавляется по `ComponentManager.ItemInitialized`.
  Смена рабочего пространства (`WSCURRENT`) перезагружает меню и ленту из CUIX, поэтому
  меню и вкладка добавляются снова.

Принятые решения:

- Меню и вкладка не сохраняются в CUIX пользователя: плагин добавляет их при каждой загрузке,
  а удаление плагина не оставляет следов в настройках интерфейса.
- В AutoCAD 2021 строка меню по умолчанию скрыта, поэтому при первой загрузке в сеансе
  плагин устанавливает `MENUBAR=1` (как TeplovPrint). Скрыть строку меню можно командой
  `MENUBAR` → `0`; вкладка ленты **Bobrov** останется доступной.
- Ошибки построения меню или ленты не мешают загрузке плагина: причина выводится в командную
  строку, команды `XDATAPALETTE*` продолжают работать.

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

- `RuntimeRequirements OS="Win64" SeriesMin="R24.0" SeriesMax="R24.3"` — AutoCAD 2021–2024
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
| `XDATAPALETTE_ON` / `_OFF` / переключатель на одной панели | `PaletteControllerTests.cs`, `tests/AcadPaletteSetXData.AutoCAD.Tests/PaletteApplicationTests.cs` |
| Регистрация `XDATAPALETTE_ON` / `_OFF` в выпускаемой DLL | `experiments/AcadPaletteSetXData.RegistrationSmoke` |
| Вкладка ленты с настоящим `AdWindows.dll` (Windows CI): структура, команды кнопок, рендер | `experiments/AcadPaletteSetXData.RibbonSmoke` |
| Состав архива ручной установки, `PackageContents.xml`, CRLF, `release-manifest.txt`, публикация двух ZIP | `experiments/Test-AcadPaletteSetXDataRelease.ps1` |
| `install.cmd` / `uninstall.cmd` на временном `%APPDATA%` (Windows CI) | `experiments/Test-AcadPaletteSetXDataManualInstall.ps1` |

Строку меню и автозагрузку пакета можно проверить только в установленном AutoCAD —
[сценарий ручной проверки](autocad-verification.md#меню-и-вкладка-bobrov-ручная-установка-задача-39).
