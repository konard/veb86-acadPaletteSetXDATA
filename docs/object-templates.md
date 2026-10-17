# Шаблоны на объектах (TZXDATA, этап 7)

Этап 7 из [TZXDATA](../TZXDATA.md) назначает шаблон схемы объектам: кнопка «Шаблон ▾» / «Назначить
шаблон» панели и команда `VELEC_TEMPLATE_APPLY`. Шаблон записывается во **все** объекты выделения одной
транзакцией через `XDataWriter` ([xdata-v2.md](xdata-v2.md)) — один шаг Undo; объект без `DEVICE_ID`
получает новый GUID (раздел 6.4), значения вне нового шаблона сохраняются (раздел 6.6). Умолчания
шаблона показываются в VST с `○` и не пишутся, пока их не зафиксировать. Сторонние DLL не нужны.

| Файл | Назначение |
| --- | --- |
| `src/AcadPaletteSetXData.Core/ParameterSchema.cs` | `TemplatesFor` — шаблоны, чьи `EntityTypes` принимают все типы выделения; `SchemaTemplate.Accepts`, `DisplayName` («Caption (Id)»). |
| `src/AcadPaletteSetXData.Core/SelectionEdit.cs` | `SelectionEdit.SetTemplate(id)` (пустой `id` — «Без шаблона»), `SelectionEdit.CommitDefaults()`. |
| `src/AcadPaletteSetXData.Core/XDataPatchV2.cs` | Запись `TEMPLATE` и `DEVICE_ID`, проверка `EntityTypes`, запись недостающих умолчаний шаблона и `Persist`-результатов. |
| `src/AcadPaletteSetXData.Core/CalculatedStrings.cs` | `ObjectCalculation.MissingDefaults()` — параметры шаблона, показанные с `○`, в порядке шаблона. |
| `src/AcadPaletteSetXData.Core/PropertyGrid.cs` | `AvailableTemplates`, `HasTemplate`, `HasDefaults`, `AssignTemplate`, `CommitDefaults` и их команды. |
| `src/AcadPaletteSetXData.Core/TemplateApply.cs` | Запрос команды: нумерованный список шаблонов, разбор ответа (Id или номер, `-`), число изменяемых объектов. |
| `src/AcadPaletteSetXData.AutoCAD/TemplateCommands.cs` | Команда `VELEC_TEMPLATE_APPLY`. |
| `src/AcadPaletteSetXData.AutoCAD/AutoCadXDataWriteTransaction.cs` | Транзакция по `ObjectId` выбранных командой объектов (без зависимости от `PICKFIRST`). |
| `src/AcadPaletteSetXData.UI/PropertyGridView.xaml(.cs)` | Кнопка-индикатор «▾ Шаблон», кнопка «Назначить шаблон» и меню шаблонов. |
| `tests/AcadPaletteSetXData.Core.Tests/ObjectTemplatesTests.cs` | Тесты этапа 7 (таблица ниже). |
| `experiments/AcadPaletteSetXData.WpfSmoke/Program.cs` | WPF smoke меню шаблонов, назначения, смены, снятия и фиксации умолчаний; рендеры `tzxdata-stage7-*.png`. |
| `examples/tzxdata-stage7-templates.lsp` | Объекты для ручной проверки в AutoCAD и печать их `BobrovXDATA`. |

## Меню «Шаблон ▾» (раздел 8.4)

Индикатор шаблона на панели управления — кнопка: «▾ Электродвигатель» (`Caption` шаблона), «▾ Разное»
при разных шаблонах, «▾ нет» без шаблона. Кнопка «Назначить шаблон» в строке сообщения над VST (видна, когда в выделении
есть объект без данных `BobrovXDATA`) открывает то же меню:

| Пункт | Действие |
| --- | --- |
| `Caption (Id)` каждого подходящего шаблона | Назначает шаблон всему выделению; текущий общий шаблон отмечен флажком. |
| «Нет шаблонов для выбранных объектов» / «Только чтение» | Недоступный пункт, когда назначать нечего или выделение не редактируется. |
| «Без шаблона» | Снимает `TEMPLATE` у всех объектов; доступен, если шаблон есть хотя бы у одного. |
| «Зафиксировать значения по умолчанию» | Записывает умолчания шаблона, показанные с `○`; доступен, если такие есть. |

В меню только шаблоны, чьи `EntityTypes` (раздел 4.4) принимают **все** типы выделения (`INSERT` и
`LINE` вместе — только шаблоны без `EntityTypes` или с обоими типами). Если выделение только для
чтения (v1, версия новее v2, предпросмотр без писателя), пункты недоступны.

## Назначение и смена шаблона (разделы 6.4, 6.6)

```text
«Шаблон ▾» → PropertyGridViewModel.AssignTemplate(id) / VELEC_TEMPLATE_APPLY → SelectionEdit.SetTemplate(id)
  → XDataWriter.WriteSelection (одна транзакция на всё выделение)
  → для каждого объекта: DEVICE_ID, если нет (новый GUID), TEMPLATE = id, пересчёт Persist
  → перечитывание выделения → VST по новому шаблону
```

- `DEVICE_ID` и `TEMPLATE` записываются первыми после заголовка `(1001 BobrovXDATA)(1070 . 2)`;
  у объекта, где `DEVICE_ID` уже есть, он не меняется — и при смене, и при снятии шаблона.
- **Значения не теряются.** Смена шаблона меняет только `TEMPLATE`: значения параметров, которых нет в
  новом шаблоне, остаются в XDATA и показываются в группе «Дополнительные»; значения вне схемы,
  таблицы и чужие RegApp тоже сохраняются. «Без шаблона» снимает только `TEMPLATE`.
- **Умолчания шаблона** (раздел 6.2) не записываются при назначении: VST показывает их с `○` и
  подписью «по умолчанию»; умолчание шаблона важнее умолчания параметра. Формулы считаются по ним сразу.
- **Одна транзакция.** Если объект не подходит по `EntityTypes`, имеет формат v1 или находится на
  заблокированном слое, запись отменяется для всего выделения — «Ошибка записи: …» на панели или
  «Шаблон не назначен: …» в командной строке. Объекты, где шаблон уже назначен, не переписываются.
- В AutoCAD одна запись — один шаг Undo (`XDATAPALETTEEDIT`, «Изменение XDATA»), отмена — `U`.

## «Зафиксировать значения по умолчанию»

Пункт меню записывает в каждый объект выделения параметры его шаблона, которые не сохранены и показаны
с `○`: скаляры с умолчанием (шаблона или параметра) и `Persist`-результаты `CalculatedString` без
ошибки. Сохранённые значения, значения вне шаблона и чужие RegApp не меняются; запись — одна
транзакция, после неё строки показывают `●`. Объекты без шаблона и с уже сохранёнными значениями не
меняются.

## Команда `VELEC_TEMPLATE_APPLY` (раздел 5.3)

```text
Команда: VELEC_TEMPLATE_APPLY
Объекты для назначения шаблона: (рамка или PICKFIRST)
  1. Устройство (DEVICE)
  2. Электродвигатель (MOTOR) *
  -. Без шаблона
Шаблон (Id или номер, - — без шаблона): 1
Шаблон DEVICE назначен объектам: 3. Отмена — одной командой U.
```

- Список — шаблоны, подходящие всем выбранным типам, в порядке схемы; `*` — текущий общий шаблон,
  `-` — только если шаблон есть хотя бы у одного объекта. Ответ — `Id` (без учёта регистра) или
  номер; неизвестный ответ повторяет запрос, Esc или пустой ответ — выход без изменений.
- Ответ строкой, а не ключевыми словами: `Id` шаблона может не быть допустимым ключевым словом AutoCAD.
- Без схемы в чертеже: «В чертеже нет схемы параметров … выполните VELEC_SCHEMA_IMPORT.»; без
  подходящих шаблонов — список типов выделения. Если менять нечего — «уже назначен; чертёж не изменён».
- Запись — тот же `SelectionEdit.SetTemplate` и `XDataWriter`, что у панели; панель после команды
  перечитывает выделение (`CommandEnded`). Фиксация умолчаний — только из меню панели.

## Тесты (`ObjectTemplatesTests`)

| Тест | Проверка |
| --- | --- |
| `AssigningATemplateToAGroupIsOneTransactionAndCreatesDeviceIds` | Назначение группе — одна транзакция, у каждого объекта свой `DEVICE_ID`, `DEVICE_ID` и `TEMPLATE` первыми, чужие RegApp целы, умолчания шаблона с `○` не записаны, повтор ничего не пишет. |
| `ChangingTheTemplateKeepsDeviceIdAndValuesOutsideTheNewTemplate` | Смена шаблона сохраняет `DEVICE_ID` и все значения (вне нового шаблона — «Дополнительные»); «Без шаблона» снимает только `TEMPLATE`. |
| `MixedTemplatesAreShownAsVariousAndUnifiedByOneAssignment` | Разные шаблоны — «Разное», одно назначение выравнивает. |
| `EntityTypesFilterTheTemplatesAndAWrongTypeCancelsTheGroup` | Фильтр `EntityTypes` в меню (`LINE` + `INSERT` — пусто); неподходящий объект отменяет запись всей группы. |
| `CommittingDefaultsWritesOnlyTheMissingTemplateDefaultsInOneTransaction` | Фиксация пишет только недостающие умолчания (шаблона над параметром) одной транзакцией; объект без шаблона, таблицы и значения вне шаблона не меняются. |
| `CommittingDefaultsWritesMissingPersistResultsOfTheTemplate` | Фиксация пишет недостающий `Persist`-результат (`ЩУ1.3`), сохранённый не меняет. |
| `MissingDefaultsFollowTheTemplateOrder` | Порядок фиксируемых параметров — порядок шаблона. |
| `ReadOnlySelectionsDoNotAssignTemplates` | Выделение только для чтения не назначает шаблон и не фиксирует умолчания. |
| `TheCommandPromptOffersTheTemplatesOfTheSelectedTypesByIdOrNumber` | Список `VELEC_TEMPLATE_APPLY` с `*` и `-`, ответ по Id или номеру, число изменяемых объектов. |
| `TheCommandAssignsTheChosenTemplateToTheWholeSelectionInOneTransaction` | Команда пишет всё выделение одной транзакцией через `XDataWriter`, значения вне шаблона сохраняются. |

## Проверка

```bash
DOTNET_ROLL_FORWARD=Major dotnet test tests/AcadPaletteSetXData.Core.Tests -c Release
# Windows: рендеры в каталог
dotnet run --project experiments/AcadPaletteSetXData.WpfSmoke -c Release -f net48 -- artifacts/screenshots/net48
```

WPF smoke (CI, net48 и net8) в настоящем `PropertyGridView` для трёх объектов без данных проверяет:
кнопку «Назначить шаблон»; меню только с шаблонами `INSERT`; назначение `MOTOR` одной записью с тремя
разными `DEVICE_ID`; умолчание шаблона с `○` без записи; флажок текущего шаблона в меню индикатора;
фиксацию умолчаний (`●`); смену на `DEVICE` с теми же `DEVICE_ID` и значением в «Дополнительные»;
«Без шаблона» с сохранением значений. RegistrationSmoke проверяет, что `VELEC_TEMPLATE_APPLY`
зарегистрирована в выпускной DLL. Ручная проверка в AutoCAD —
[autocad-verification.md](autocad-verification.md#tzxdata-этап-7-шаблоны-на-объектах), рендеры —
[screenshots](screenshots/README.md).

## Ограничения этапа

- Undo/Redo и запрос команды в живом AutoCAD CI не проверяет — только ручной сценарий.
- Фиксация умолчаний не пишет таблицы шаблона (у `Table` нет умолчания) и `Persist`-формулы с ошибкой.
- Рендер меню собран из двух изображений (палитра и всплывающее меню): WPF рисует `ContextMenu` в
  отдельном окне.
- Создание и правка шаблонов — редактор схемы (этап 8); перенос старых форматов — `VELEC_MIGRATE` (этап 9, [migration-check.md](migration-check.md)).
