# Производительность и кэш (TZXDATA, этап 10)

Реализация раздела 10 [TZXDATA](../TZXDATA.md) и его проверки: нагрузочный тест 1000 объектов × 50
параметров, счётчики разборов, трассировка `XDATAPALETTE_TRACE=1` и ручной сценарий AutoCAD 2021
([autocad-verification.md](autocad-verification.md#tzxdata-этап-10-производительность-и-выпуск)).

## Критерии раздела 10

| П. | Требование | Реализация | Проверка |
| --- | --- | --- | --- |
| 10.1 | Кэш схемы на документ; сброс при изменении `VELEC`, импорте, Undo/Redo, смене документа | `SchemaCache` (счётчик `LoadCount`) и `AutoCadSchemaCaches`: один кэш на `Document`. Сброс происходит при `ObjectModified/Erased/Reappended/Unappended` объектов `NOD/VELEC` и самого NOD, после `U/UNDO/REDO/MREDO`, при `VELEC_SCHEMA_IMPORT` и в редакторе схемы, при закрытии документа. Палитра читает кэш активного документа | `SchemaNodWriterTests` (кэш схемы и `Invalidate`); `LoadTests.ANewSchemaParsesTheSelectionOnceAgain` (`LoadCount` 2); строки трассировки «Кэш схемы загружен/сброшен» |
| 10.2 | Кэш значений `ObjectId → ObjectValues`, XDATA разбирается один раз, LRU ≈ 10 000, запись инвалидирует только изменённые | `ObjectValuesCache`: ключ — handle, LRU на `DefaultCapacity = 10000`. Запись хранит значения v2 и дерево XDATA (`Parse`). Новый снимок с теми же XDATA, типом объекта и той же схемой берётся из кэша без разбора. `XDataWriter` вызывает `Invalidate` только для записанных handle. Undo/Redo и внешние команды дают новые XDATA, и объект разбирается заново при следующем чтении после `CommandEnded`. Новая схема очищает весь кэш | `LoadTests`: повторное выделение, запись одного объекта (+1), внешнее изменение и его Undo (+1, +1), LRU на 12 000 объектов; кэш значений в `XDataV2Tests` |
| 10.3 | VST читает только кэш | `SelectionViewModel.Apply(snapshots, error, cache)` берёт значения через `cache.Get`, а дерево вкладки «XDATA» и строки прежнего формата — через `cache.Parse`. `PropertyGridViewModel` строится по `SelectionViewModel.Values`; поиск параметра идёт по словарю | `LoadTests.ThousandObjects…` (счётчики не растут) |
| 10.4 | Объединение O(объекты × параметры) без обращения к базе | Сравнение значений идёт по строкам схемы в одном проходе. «⊞/⊟», раскрытие и фильтр перестраивают только видимые строки | `LoadTests.TheMergeDoesNotReadTheDrawing`: после «⊟», «⊞» и раскрытия группы источник прочитан один раз |
| 10.5 | Виртуализация VST | `ListView` с `VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling` ([PropertyGridView.xaml](../src/AcadPaletteSetXData.UI/PropertyGridView.xaml)) | WPF smoke (CI, net48 и net8): контейнеров меньше, чем строк таблицы из 400+ строк |
| 10.6 | 1000 × 50: VST ≤ 300 мс после чтения снимков; повторный выбор без разбора XDATA; трассировка `XDATAPALETTE_TRACE=1` | `PropertyGridViewModel.BuildTime/BuildSummary`, счётчики `ParseCount` и `TreeParseCount`; строки трассировки ниже | `LoadTests`, `PropertyGridTests.ThousandObjectsWithFiftyParametersAreBuiltWithinTheBudget`, эксперимент `LoadReport` в CI, ручной сценарий |

## Нагрузочные данные

Схема [examples/tzxdata-stage10-load-schema.json](../examples/tzxdata-stage10-load-schema.json) получается
скриптом [experiments/scripts/gen-load-schema.py](../experiments/scripts/gen-load-schema.py) и проверяется
`SchemaCheck` в CI. Она содержит:

- шаблон `LOAD` «Нагрузочный объект» для `Line`;
- 48 параметров `L01…L48` по кругу `String`, `Double` (м, 2 знака), `Integer`, `Boolean`, `Enum LoadPhase`,
  в группах «Общие», «Электрика», «Кабель» и других;
- `L49` типа `CalculatedString` `@@[L01].@@[L03]`;
- `L50` типа `Table` с колонками `LMat_Mark` и `LMat_Count`.

Объекты одни и те же в тестах ([LoadData.cs](../tests/AcadPaletteSetXData.Core.Tests/LoadData.cs)) и в AutoCAD
([examples/tzxdata-stage10-load.lsp](../examples/tzxdata-stage10-load.lsp)): handle `1000 + n`, значение
параметра `i` зависит от `(i − 1) mod 5`:

| `(i − 1) mod 5` | Значение |
| --- | --- |
| 0 | `Щит-n mod 3` |
| 1 | `n × 0.5` |
| 2 | `i` |
| 3 | `n mod 2` |
| 4 | ключ `A/B/C` |

В таблице `L50` две строки, `M0/1` и `M1/2`. Строки VST получаются разными: «Разное» и общие значения.

## Счётчики

- `SchemaCache.LoadCount` — сколько раз схема прочитана из NOD (JSON в тестах).
- `ObjectValuesCache.ParseCount` — сколько раз разобраны значения XDATA v2 (`XDataV2.Read`).
- `ObjectValuesCache.TreeParseCount` — сколько раз `XDataParser` построил дерево XDATA для вкладки «XDATA»
  и прежних форматов. До этапа 10 дерево строилось заново при каждом выделении. Теперь оно хранится в
  записи кэша вместе со значениями. `Parse` возвращает копию `ParsedEntityData` с общим деревом и своими
  предупреждениями, поэтому предупреждения выделения (дубли `DEVICE_ID`, формулы) не остаются в кэше.

Повторное выделение того же набора — новые снимки с теми же XDATA — не меняет ни один счётчик.

## Трассировка

`XDATAPALETTE_TRACE=1` в окружении до запуска AutoCAD. После выбора в командной строке:

```text
[XDATAPALETTE] Кэш схемы загружен: записей N.
[XDATAPALETTE] Прочитано объектов: 1000; T мс; кэш значений: 1000, разборов XDATA: 1000.
[XDATAPALETTE] VST: объектов 1000, строк 57, видимых 57; T мс. Разборов XDATA: 1000, деревьев XDATA: 1000.
```

Первая строка — чтение снимков под блокировкой документа вместе с разбором в кэш значений. Вторая —
построение VST после чтения: это время и проверяет критерий 300 мс. При повторном выборе числа разборов
остаются прежними. После записи одного объекта каждое увеличивается на 1. После сброса кэша схемы
выводится «Кэш схемы сброшен: <причина>.», и следующий выбор разбирает объекты заново.

## Отчёт без AutoCAD

```sh
dotnet run --project experiments/AcadPaletteSetXData.LoadReport -c Release -- examples/tzxdata-stage10-load-schema.json
```

Отчёт выполняется в CI (шаг «Load report of 1000 objects × 50 parameters (TZXDATA 10.6)»). Порядок:

1. Прогрев JIT на 50 других объектах.
2. Три первых выделения с пустым кэшем значений. Перед каждым выполняется `GC.Collect`: мусор от
   создания снимков относится к чтению чертежа.
3. Пять повторных выделений новыми снимками.
4. Запись одного объекта.
5. LRU на 12 000 объектах.

Отчёт выводит паузы сборщика мусора за каждый шаг и возвращает 1, если:

- лучшее построение VST дольше 300 мс;
- повторный выбор разбирает XDATA;
- запись разбирает больше одного объекта;
- в кэше больше 10 000 объектов;
- схема загружена больше одного раза.

Пример на загруженной машине Linux (load average ≈ 3, .NET 8, Release):

```text
== Первое выделение (кэш значений пуст), 3 раза ==
#3: 474.1 мс: разбор XDATA v2 145.8 мс (разборов 3050), деревьев XDATA 3050; GC: 7/2/0, паузы 116.0 мс
    VST: объектов 1000, строк 57, видимых 57; 171.2 мс.
OK: VST ≤ 300 мс (лучшее 171.2)
== Повторное выделение того же набора (новые снапшоты, те же значения) ==
#5: 87.6 мс, из них VST 48.9 мс; разборов XDATA 3050, деревьев 3050; GC: 2/1/0, паузы 31.2 мс
OK: VST ≤ 300 мс (лучшее 48.9)
OK: выделение целиком ≤ 300 мс (лучшее 87.6)
OK: XDATA не разбирается повторно
== Запись одного объекта ==
OK: разобран заново только записанный объект
== Кэш значений: LRU ==
OK: не больше 10000 объектов
OK: схема прочитана один раз
```

Счётчики включают 50 объектов прогрева. Время сильно зависит от загрузки машины, а основная часть
разброса — паузы GC. Поэтому тесты и отчёт проверяют лучшее из нескольких построений. На свободной
машине первое построение VST занимает около 120 мс, повторное — около 50–60 мс. В CI (`ubuntu-latest`,
запуск 39041629315) первое построение заняло 39.2 мс, повторное — 34.2 мс, повторный выбор целиком —
68.1 мс.

## Профилирование

```sh
dotnet tool install -g dotnet-trace
dotnet build experiments/AcadPaletteSetXData.LoadReport -c Release
dotnet-trace collect --format speedscope -o experiments/logs/cold.nettrace -- \
  dotnet experiments/AcadPaletteSetXData.LoadReport/bin/Release/net8.0/AcadPaletteSetXData.LoadReport.dll --profile 30 --cold examples/tzxdata-stage10-load-schema.json
python3 experiments/scripts/speedscope-top.py experiments/logs/cold.speedscope.json 40
```

`--profile N` повторяет выделение N раз без вывода. С `--cold` каждый повтор начинается с пустого кэша
значений. `speedscope-top.py` печатает методы с наибольшим собственным и общим временем. По профилю
этапа 10 убраны:

- повторный разбор дерева XDATA при каждом выделении;
- создание строк подписей в `XDataParser`;
- пустые списки детей узлов;
- повторное форматирование одинаковых значений при объединении (`PropertyGrid.Scalar`).

## Выпуск

Release собирает одну `acadPaletteSetXDATA.dll` ([build-release.md](build-release.md)). Этап 10 не добавил
зависимостей. `RegistrationSmoke` печатает ссылки выпускаемой DLL («Assembly references: …») и падает, если
среди них есть что-то кроме сборок .NET Framework/WPF и AutoCAD (`AcMgd`, `AcDbMgd`, `AcCoreMgd`, `AdWindows`).

## Ограничения

- 300 мс проверяются в Core без WPF. Отрисовку видимых строк `ListView` измеряет только ручной сценарий
  в AutoCAD. Благодаря виртуализации она не зависит от числа объектов.
- Чтение снимков из чертежа (транзакция AutoCAD) в бюджет не входит: критерий считает время после чтения
  снимков. Время чтения выводит трассировка.
- Кэш значений сравнивает XDATA и XRecord снимка с сохранёнными. Чтение объекта из базы при каждом
  выборе остаётся, разбор — нет.
