# Автоматическая сборка и Release AutoCAD 2021

[build-release.yml](../.github/workflows/build-release.yml) при каждом `push` в
`main` собирает **AutoCADHttp.dll** для **AutoCAD 2021 / .NET Framework 4.8 / x64**,
проверяет результат и публикует GitHub Release с **AutoCADHttp.zip**.
ZIP содержит ровно один файл `AutoCADHttp.dll` в корне.

`src/AutoCADHttp` — отдельная минимальная сборочная основа из задачи #11.
Она проверяет ссылки на три сборки AutoCAD API при компиляции; HTTP-сервера
и команд AutoCAD в ней нет. Существующий плагин панели `AcadPaletteSetXData`
собирается и проверяется прежним `ci.yml`, его три DLL не входят в этот ZIP.

API получается из официального NuGet-пакета [AutoCAD.NET 24.0.0](https://www.nuget.org/packages/AutoCAD.NET/24.0.0)
с точной версией `[24.0.0]`. `PrivateAssets="all"` и `ExcludeAssets="runtime"`
оставляют API только для компиляции. `AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll`
не хранятся в Git и не включаются в комплект. Любая дополнительная DLL в
выходной папке блокирует упаковку.

## Запуск и версия

После слияния workflow в `main` цикл выполняется автоматически:

```text
push main → NuGet restore → Release / x64 → проверки → AutoCADHttp.zip
          → draft Release с ZIP → публикация готового Release
```

Ручной запуск: **Actions → Build and release AutoCADHttp → Run workflow → main**.
В GitHub этот пункт появляется после добавления workflow в основную ветку.
Ручной запуск на другой ветке проверяет сборку, но не публикует Release.
Pull request тоже собирает DLL и ZIP, запускает проверки и сохраняет артефакт
`AutoCADHttp-release` без создания Release.

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
Упаковка проверяет управляемую x64-сборку и сохраняет только нужную DLL.

Публикация использует `GITHUB_TOKEN` с `contents: write` только в release job.
[GitHub CLI создаёт draft и загружает ZIP](https://cli.github.com/manual/gh_release_create);
публичным Release становится только после успешной загрузки. Сбой загрузки
оставляет неопубликованный draft. **Re-run failed jobs** повторяет публикацию
с прежней версией: draft дополняется ZIP, уже завершённый Release того же
commit SHA сохраняется. Совпадение версии с другим SHA или неполный уже
опубликованный Release приводит к ошибке вместо замены существующего выпуска.
Новый ручной запуск получает новый номер версии.

## Локальная проверка

Нужны .NET SDK 8 или новее и PowerShell 7 (`pwsh`); AutoCAD для сборки не нужен.

```powershell
dotnet restore src/AutoCADHttp/AutoCADHttp.csproj --source https://api.nuget.org/v3/index.json
dotnet build src/AutoCADHttp/AutoCADHttp.csproj --no-restore -c Release -p:PlatformTarget=x64 -p:Version=0.0.1
pwsh -NoProfile -File experiments/Test-AutoCADHttpRelease.ps1
pwsh -NoProfile -File scripts/New-AutoCADHttpPackage.ps1 -BuildDirectory src/AutoCADHttp/bin/Release/net48 -ZipPath artifacts/release/AutoCADHttp.zip
```

Проверка сначала воспроизводила отсутствие `build-release.yml`.
Теперь она проверяет отсутствующую/повреждённую DLL, отказ при попадании
каждой из трёх DLL Autodesk, x64/.NET Framework 4.8, содержимое ZIP и идентичность DLL, порядок
создания draft/загрузки/публикации, сбои GitHub CLI и повторные попытки.
Изолированный restore с пустым локальным NuGet feed воспроизводит ошибку
недоступных пакетов без запросов в сеть.
GitHub CLI в тестах подменён; тесты не создают настоящие Release.
На Windows CI выполняются те же проверки с .NET SDK 8. Локальные логи
сохраняются в `experiments/logs/` (исключены из Git).
