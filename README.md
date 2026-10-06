# acadPaletteSetXDATA

Каркас WPF-панели управления свойствами и XDATA для AutoCAD, **этап 1** из [ТЗ](TZ.md).

Команда `XDATAPALETTE` показывает или скрывает единственный `PaletteSet`. Внутри —
вертикальные вкладки **«Свойства»**, **«XDATA»**, **«Команды»** справа. Панель можно
закреплять слева и справа, оставлять плавающей, сворачивать кнопкой автоскрытия
и закрывать. После закрытия команда снова показывает ту же панель и выбранную вкладку.
WPF-содержимое следует `COLORTHEME`: `0` — тёмная тема, `1` — светлая, в том числе
при изменении темы уже открытой панели.

Вкладки содержат заглушки для следующих этапов. Макет полей/материалов, обработка
выделения, чтение и запись XDATA, групповое редактирование и Undo/Redo предусмотрены
этапами 2–5 и пока не реализованы. Команда не изменяет DWG.

## Сборка

Нужны .NET SDK 8 или новее; для запуска — Windows и соответствующая версия AutoCAD.
Проекты используют опубликованные Autodesk пакеты API только для компиляции.

| Выходная сборка | API | Целевой AutoCAD |
| --- | --- | --- |
| `net48` | AutoCAD.NET 24.3.0 | AutoCAD 2024 (.NET Framework 4.8) |
| `net8.0-windows` | AutoCAD.NET 25.0.1 | AutoCAD 2025 (.NET 8) |

Совместимость версий описана в [документации Autodesk](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm).
Версии пакетов закреплены намеренно: AutoCAD.NET 25.0.2 уже предназначен для .NET 10.
Для обновлённых выпусков AutoCAD с .NET 10 требуется отдельная проверка совместимости;
этот этап сохраняет указанные в ТЗ цели .NET Framework 4.8 и .NET 8.

```sh
dotnet build src/AcadPaletteSetXData.AutoCAD -c Release
dotnet test tests/AcadPaletteSetXData.Core.Tests -c Release
```

Выход: `src/AcadPaletteSetXData.AutoCAD/bin/Release/<целевая платформа>/`.
Из этой папки скопируйте **все три** `AcadPaletteSetXData.*.dll` вместе в отдельную
папку плагина. Для .NET 8 сохраните рядом также `.deps.json`. DLL AutoCAD поставляются
самим AutoCAD, копировать их из NuGet или заменять ими установленные DLL не нужно.

## Загрузка

1. Добавьте папку плагина в доверенные расположения AutoCAD (`TRUSTEDPATHS`).
2. Выполните `NETLOAD` и выберите `AcadPaletteSetXData.AutoCAD.dll` нужной платформы.
3. Введите `XDATAPALETTE`. Повторный вызов скрывает панель.
4. Для закрепления перетащите заголовок к краю окна или воспользуйтесь меню панели.
   Кнопка автоскрытия сворачивает панель, кнопка закрытия скрывает её.

## Архитектура и проверка

- `Core`: ViewModel с `INotifyPropertyChanged`, модель вкладок, управление временем
  жизни панели; не зависит от AutoCAD/WPF.
- `UI`: WPF `UserControl`, привязки MVVM, вертикальные вкладки и словари тем.
- `AutoCAD`: регистрация команды и приложения, адаптер `PaletteSet`, события
  изменения `COLORTHEME`. Панель создаётся при первом вызове команды; при завершении
  приложения освобождается и отписывается от событий.
- `tests`: проверки ленивого создания, повторных вызовов, нативного закрытия,
  смены темы, освобождения ресурсов и привязанных свойств.

Проверки интерфейса запускаются на Windows, без AutoCAD, для обеих платформ:

```sh
dotnet build experiments/AcadPaletteSetXData.WpfSmoke -c Release
dotnet run --project experiments/AcadPaletteSetXData.WpfSmoke -c Release -f net8.0-windows --no-build -- artifacts/screenshots/net8
./experiments/AcadPaletteSetXData.WpfSmoke/bin/Release/net48/AcadPaletteSetXData.WpfSmoke.exe artifacts/screenshots/net48
```

Для интерактивного просмотра WPF-панели передайте `--interactive` вместо пути к PNG.
Smoke-проверка отображает настоящий `UserControl`, проверяет положение/геометрию
вкладок, переключение содержимого и привязку выбранной вкладки, смену ресурсов темы,
малый размер панели и освобождение ViewModel; сохраняет шесть PNG для каждой платформы.
CI выполняет эти проверки и публикует артефакты `wpf-renders`, `plugin-binaries`
и результаты unit-тестов. Это проверка WPF-содержимого; нативная рамка `PaletteSet`,
закрепление и автоскрытие проверяются в AutoCAD по [сценарию](docs/autocad-verification.md).
