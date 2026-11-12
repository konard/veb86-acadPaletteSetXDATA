# Автоматическая сборка и Release AutoCAD 2021

[build-release.yml](../.github/workflows/build-release.yml) при каждом `push` в
`main` собирает **acadPaletteSetXDATA.dll** для **AutoCAD 2021 / .NET Framework 4.8 / x64**,
проверяет результат и публикует GitHub Release с двумя архивами:

- **acadPaletteSetXDATA.zip** — ровно один файл `acadPaletteSetXDATA.dll` в корне (для `NETLOAD`);
- **acadPaletteSetXDATA-manual-install-v<версия>.zip** (задача #39) — ручная установка без
  установщика и без прав администратора: папка
  `AppData/Roaming/Autodesk/ApplicationPlugins/acadPaletteSetXDATA.bundle` с
  `PackageContents.xml` и той же DLL в `Contents/AutoCAD2021-2024`, а также `INSTALL.md`,
  `install.cmd`, `uninstall.cmd`, `LICENSE` и `release-manifest.txt` (версия, commit, SHA-256 файлов).
  [Состав и устройство](bobrov-menu-ribbon.md#ручная-установка-без-установщика).

`src/acadPaletteSetXDATA` компилирует общие исходники Core, AutoCAD и UI,
включая XAML и словари тем, в одну сборку. В ней зарегистрированы
`ExtensionApplication`, `CommandClass` и команда `XDATAPALETTE`.
После распаковки ZIP добавьте папку в `TRUSTEDPATHS` и выполните `NETLOAD`
для **`acadPaletteSetXDATA.dll`** в AutoCAD 2021. Командная строка подтверждает
загрузку и подсказывает команду; `XDATAPALETTE` показывает/скрывает панель.
Плагин создаёт панель только при вызове команды. Если при загрузке нет активной
DWG, сообщение выводится один раз после появления чертежа.

Отдельные проекты Core/UI/AutoCAD остаются доступны для разработки и дополнительных
версий AutoCAD; их сборки проверяются `ci.yml`. Они не требуются рядом с DLL из ZIP.

API получается из официального NuGet-пакета [AutoCAD.NET 24.0.0](https://www.nuget.org/packages/AutoCAD.NET/24.0.0)
с точной версией `[24.0.0]`. `PrivateAssets="all"` и `ExcludeAssets="runtime"`
оставляют API только для компиляции. `AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll`
не хранятся в Git и не включаются в комплект. Любая дополнительная DLL в
выходной папке блокирует упаковку.

## Запуск и версия

После слияния workflow в `main` цикл выполняется автоматически:

```text
push main → NuGet restore → Release / x64 → проверки → acadPaletteSetXDATA.zip
          + acadPaletteSetXDATA-manual-install-v0.0.N.zip → проверка install.cmd/uninstall.cmd
          → draft Release с обоими ZIP → публикация готового Release
```

Ручной запуск: **Actions → Build and release acadPaletteSetXDATA → Run workflow → main**.
В GitHub этот пункт появляется после добавления workflow в основную ветку.
Ручной запуск на другой ветке проверяет сборку, но не публикует Release.
Pull request тоже собирает DLL и ZIP, запускает проверки и сохраняет артефакт
`acadPaletteSetXDATA-release` без создания Release.

Версия формируется как `v0.0.${{ github.run_number }}`, например `v0.0.1`.
[Номер запуска GitHub Actions](https://docs.github.com/en/actions/reference/workflows-and-actions/variables)
увеличивается автоматически; запуски PR и неудачные сборки могут оставлять
пропуски в нумерации. Менять версию вручную перед коммитом не требуется.
`Version` сборки получает тот же номер без `v`; CLR/file version закреплены
как `0.0.0.0`, чтобы большой номер запуска не превышал лимит компонента версии.
Описание Release содержит версию, полный commit SHA, дату сборки UTC,
`Release / x64` и `AutoCAD 2021 / .NET Framework 4.8`.

## Надёжность и повторный запуск

Ошибка restore выдаёт отдельное понятное сообщение о необходимых пакетах и
подключении к NuGet вместе с исходным логом. Ошибка сборки или проверки
завершает build job; зависимый release job не запускается.
Проверки читают метаданные именно выпускаемой DLL: регистрацию приложения/команды,
наличие реализации панели и WPF-ресурсов, отсутствие зависимости от отдельных DLL
плагина. С этапа 10 TZXDATA проверка печатает ссылки DLL («Assembly references: …») и завершается
ошибкой, если среди них есть сборка, отличная от .NET Framework/WPF (`mscorlib`, `System*`, `WindowsBase`,
`PresentationCore`, `PresentationFramework`, `Microsoft.CSharp`) и AutoCAD (`AcMgd`, `AcDbMgd`, `AcCoreMgd`,
`AdWindows`): новая зависимость не попадёт в выпуск незамеченной. Windows запускает полный WPF smoke-сценарий с этой сборкой и сохраняет
рендеры в `release-wpf-renders`. Упаковка проверяет управляемую x64-сборку
и сохраняет только нужную DLL. Нативный `NETLOAD` требует установленного AutoCAD;
[сценарий ручной проверки](autocad-verification.md).

Публикация использует `GITHUB_TOKEN` с `contents: write` только в release job.
[GitHub CLI создаёт draft и загружает оба ZIP](https://cli.github.com/manual/gh_release_create);
публичным Release становится только после успешной загрузки. Сбой загрузки
оставляет неопубликованный draft. **Re-run failed jobs** повторяет публикацию
с прежней версией: draft дополняется обоими ZIP, уже завершённый Release того же
commit SHA сохраняется. Совпадение версии с другим SHA или уже опубликованный Release,
в котором нет ровно этих двух архивов нужного размера, приводит к ошибке вместо замены
существующего выпуска.
Новый ручной запуск получает новый номер версии.

## Локальная проверка

Нужны .NET SDK 8 или новее и PowerShell 7 (`pwsh`); AutoCAD для сборки не нужен.

```powershell
dotnet restore src/acadPaletteSetXDATA/acadPaletteSetXDATA.csproj --source https://api.nuget.org/v3/index.json
dotnet build src/acadPaletteSetXDATA/acadPaletteSetXDATA.csproj --no-restore -c Release -p:PlatformTarget=x64 -p:Version=0.0.1
pwsh -NoProfile -File experiments/Test-AcadPaletteSetXDataRelease.ps1
pwsh -NoProfile -File scripts/New-AcadPaletteSetXDataPackage.ps1 -BuildDirectory src/acadPaletteSetXDATA/bin/Release/net48 -ZipPath artifacts/release/acadPaletteSetXDATA.zip -ManualInstallZipPath artifacts/release/acadPaletteSetXDATA-manual-install-v0.0.1.zip -Version v0.0.1
# Только Windows: install.cmd/uninstall.cmd на временном %APPDATA%, профиль пользователя не меняется.
pwsh -NoProfile -File experiments/Test-AcadPaletteSetXDataManualInstall.ps1 -ManualInstallZipPath artifacts/release/acadPaletteSetXDATA-manual-install-v0.0.1.zip
```

С задачи #39 проверка выпуска также собирает архив ручной установки и проверяет его точный
состав, совпадение DLL, `PackageContents.xml` (`AppVersion` выпуска, `Win64`, `R24.0`–`R24.3`,
`LoadOnAutoCADStartup`, существующий `ModuleName`, команды `XDATAPALETTE*`), окончания строк CRLF
в `.cmd`, SHA-256 в `release-manifest.txt`, отказ при неверной версии или DLL Autodesk в сборке,
а также создание, возобновление и проверку Release с двумя архивами.
`Test-AcadPaletteSetXDataManualInstall.ps1` на Windows CI запускает `install.cmd` из пути с пробелом,
повторную установку поверх старой версии (удаление лишних файлов, снятие `Zone.Identifier`),
`uninstall.cmd` и проверяет, что другие пакеты в `ApplicationPlugins` не затрагиваются.

Проверка задачи #13 до переименования завершалась ошибкой
`The required acadPaletteSetXDATA build target is missing.`
Теперь она проверяет регистрацию `XDATAPALETTE` в выпускаемой DLL и точное имя сборки `acadPaletteSetXDATA`
(простое переименование чужой DLL не проходит), отсутствующую/повреждённую DLL, отказ при попадании
каждой из трёх DLL Autodesk, x64/.NET Framework 4.8, содержимое ZIP и идентичность DLL, порядок
создания draft/загрузки/публикации, сбои GitHub CLI и повторные попытки.
Изолированный restore с пустым локальным NuGet feed воспроизводит ошибку
недоступных пакетов без запросов в сеть.
GitHub CLI в тестах подменён; тесты не создают настоящие Release.
На Windows CI выполняются те же проверки с .NET SDK 8. Локальные логи
сохраняются в `experiments/logs/` (исключены из Git).

Регрессия задачи #15 воспроизводится проверкой метаданных прежней DLL:
`Release DLL is missing AutoCAD ExtensionApplication registration.`
Прежний ZIP содержал только сборочную основу без команды. Теперь выпускается
полная реализация плагина, а не отдельный набор DLL с другим именем точки загрузки.
