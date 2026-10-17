# Миграция и проверка чертежа (TZXDATA, этап 9)

Этап 9 из [TZXDATA](../TZXDATA.md) — две команды для чертежей, созданных до формата v2:

- `VELEC_MIGRATE` переносит в XDATA v2 `ESMT_LEP_v1.0`, строки v1 `BobrovXDATA` (`ключ=значение`),
  XML `VisualTreeString` и прежний RegApp `SMARTLINE`. Поля сопоставляются по разделу `migration` схемы.
  Исходные данные удаляются только при ответе «Удалить».
- `VELEC_CHECK` проверяет чертёж: дубли `DEVICE_ID`, данные вне схемы, ключи ENUM, лимиты и результаты
  `Persist`. Затем предлагает создать новые GUID для копий и пересчитать формулы.

Обе команды читают весь чертёж или только выбранные объекты (PICKFIRST) и сначала выводят отчёт. Чертёж
меняется только после подтверждения: одна транзакция и один шаг Undo (П14). Сторонние DLL не нужны: обе
команды входят в `acadPaletteSetXDATA.dll`.

| Файл | Назначение |
| --- | --- |
| `src/AcadPaletteSetXData.Core/SchemaMigration.cs` | `MigrationMap` — раздел `migration` схемы (`fields`, `tables`, `rename`); `SchemaMigration` — перенос одного объекта (`Migrate` → `ObjectMigration`), поиск источников (`FindSources`), отчёт (`Report`). |
| `src/AcadPaletteSetXData.Core/DrawingCheck.cs` | `DrawingCheck` — проверка всех объектов (`Run` → `DrawingCheckReport`), исправление дублей и `Persist` (`Fix`), строки отчёта. |
| `src/AcadPaletteSetXData.Core/XDataWriter.cs` | `WriteEach` — разные изменения многих объектов одной транзакцией; ошибка в любом объекте отменяет всю запись. |
| `src/AcadPaletteSetXData.Core/XDataParser.cs`, `DrawingNames.cs` | Чтение `SMARTLINE` и копии `BobrovXDATA_V1`; константы `LegacyRegApp`, `EsmtRegApp`, `BackupRegApp` (П11). |
| `src/AcadPaletteSetXData.Core/SchemaValidator.cs` | Проверка раздела `migration` (`CheckMigration`, объект отчёта «Миграция»). |
| `src/AcadPaletteSetXData.Core/SchemaEditor.cs` | Переименование неиспользуемого параметра в редакторе схемы обновляет и таблицу соответствия. |
| `src/AcadPaletteSetXData.AutoCAD/AutoCadXDataWriteTransaction.cs` | Имя команды для Undo; удаление RegApp и XRecord, перенесённых в v2. |
| `src/AcadPaletteSetXData.AutoCAD/MigrationCommands.cs` | Команды `VELEC_MIGRATE` и `VELEC_CHECK`: обход чертежа, запросы, отчёт, запись. |
| `tests/AcadPaletteSetXData.Core.Tests/SchemaMigrationTests.cs`, `DrawingCheckTests.cs` | Тесты этапа 9 (таблицы ниже). |
| `experiments/AcadPaletteSetXData.MigrationReport/Program.cs` | Отчёты обеих команд без AutoCAD на объектах из `stage3-selection.lsp` и `tzxdata-stage9-migrate.lsp`; запускается в CI. |
| `examples/tzxdata-stage9-migrate.lsp` | Объекты для ручной проверки в AutoCAD. |

## Команды

```text
VELEC_MIGRATE (UsePickSet, Redraw) → схема из кэша документа (без схемы — «выполните VELEC_SCHEMA_IMPORT»)
  объекты: PICKFIRST или весь чертёж (пространства, листы, блоки; без внешних ссылок)
  «Исходные данные [Сохранить/Удалить] <Сохранить>:»
  SchemaMigration.Migrate каждого объекта → отчёт (длиннее 24 строк — открывается текстовое окно)
  ничего не меняется → «Переносить нечего; чертёж не изменён.»
  «Выполнить перенос? [Да/Нет] <Нет>:» → XDataWriter.WriteEach → одна транзакция «Миграция XDATA»
  → «Перенесено объектов: N. Отмена — одной командой U.»

VELEC_CHECK (UsePickSet, Redraw) → схема из кэша (без схемы проверяются дубли DEVICE_ID, лимиты и прежние форматы)
  DrawingCheck.Run всех объектов → отчёт по видам замечаний и строка «Исправимо: …»
  есть что исправить → «Исправить [Дубли/Persist/Все/Нет] <Нет>:»
  → DrawingCheck.Fix → одна транзакция «Проверка XDATA» → «Исправлено объектов: N. Отмена — одной командой U.»
```

- Объекты на заблокированных слоях читаются, но не записываются. Отчёт перечисляет их в строке
  «Пропущены объекты на заблокированных слоях (N): …».
- Первая строка отчёта — «Объекты: весь чертёж (N).» или «Объекты: выбранные (N).».
- После записи кэш значений сбрасывается, и панель перечитывает выделение.

## Раздел `migration` схемы

Таблица соответствия хранится в схеме: в JSON (`VELEC_SCHEMA_IMPORT`/`EXPORT`) и в `NOD/VELEC/INFO`.
Поэтому код не меняется, когда меняются имена (П3, П11). В `examples/schema.json`:

```json
"migration": {
  "fields": { "Type": "Device_Type", "Number": "Device_Number", "Name": "NMO_Name",
              "Title": "Device_Title", "ProjectReference": "Project_Ref" },
  "tables": {
    "Material": { "table": "Materials",
                  "columns": { "Category": "Mat_Category", "Name": "Mat_Mark", "Count": "Mat_Count",
                               "IsInSpec": "Mat_InSpec", "Comment": "Mat_Comment" } }
  },
  "rename": {}
}
```

| Поле | Смысл |
| --- | --- |
| `fields` | Ключ прежнего формата → `Id` параметра. Русские ключи (`Тип`, `Номер`, `Название`, `Заголовок`, `Проект`) приводятся к английским так же, как в `XDataParser`. Ключ, которого нет в таблице, переносится в параметр с тем же `Id`, если он есть в схеме. |
| `tables` | Метка списка (`Material`/`Материал`, атрибуты `<Material>` в XML) → параметр `Table` и его колонки. |
| `rename` | `старый Id` → `новый Id` для значений v2 и ячеек таблиц. Других способов сменить `Id` хранимого параметра нет (П3). |

Цель не может быть системным `Id` (`DEVICE_ID`, `TEMPLATE`), `Table` (кроме `tables.*.table`) или
`CalculatedString`. Колонки должны входить в `Columns` таблицы. Ошибки раздела выводит проверка схемы
(раздел 4.5). Объект отчёта — «Миграция»:

| Код | Когда |
| --- | --- |
| `InvalidFieldType` | `migration`, `fields`, `tables`, `rename` или описание таблицы — не объект. |
| `UnknownReference` | Параметр-цель или `Table` не описаны в схеме. |
| `InvalidValue` | Цель — `Table` или `CalculatedString`; переименование в себя; цепочка переименований. Предупреждение — старый `Id` из `rename` ещё описан в схеме. |
| `Required` | У таблицы не задано `table`. |
| `InvalidColumn` | Колонка не входит в `Columns` таблицы. |
| `InvalidId`, `ReservedId` | Недопустимый или системный `Id` в `rename`. |
| `NotApplicable` | Предупреждение: неизвестное поле раздела или описания таблицы. |

## Правила переноса

- **Источники** читаются по порядку: секции XDATA в порядке записи (`ESMT_LEP_v1.0`, `SMARTLINE`,
  строки v1 `BobrovXDATA`), затем XML `VisualTreeString` в XRecord. При расхождении побеждает более
  поздний источник, а отчёт выводит предупреждение. Поле без параметра в схеме, список без таблицы и
  неизвестный элемент XML не переносятся: объект «перенесён не полностью».
- **Записанные значения v2 не перезаписываются** — расхождение с источником выводится как предупреждение.
- **ENUM:** подпись становится ключом справочника. Значение, которого нет в справочнике, записывается
  как есть (его потом покажет `VELEC_CHECK`); без `allowCustom` отчёт выводит предупреждение. Поэтому у
  `Mat_Category` в `examples/schema.json` задано `allowCustom`: категории материалов — свободный текст.
- **Прочие ошибки преобразования** (число, логическое значение) оставляют поле в источнике. Объект
  «перенесён не полностью», и его исходные данные сохраняются даже при «Удалить».
- **`DEVICE_ID`** создаётся у объекта с источниками. Результаты `Persist` пересчитываются.
- **Объект не изменяется**, если есть хотя бы одно из условий:
  - секция `BobrovXDATA` повторяется или список не закрыт;
  - XDATA более новой версии формата;
  - есть и строки v1, и их копия `BobrovXDATA_V1`;
  - строка длиннее 255 байт;
  - XDATA после переноса больше 16 КБ (и больше исходной).
- **«Сохранить»** оставляет `ESMT_LEP_v1.0`, `SMARTLINE` и XRecord на месте. Строки v1 переносятся в
  `BobrovXDATA_V1`, потому что секция `BobrovXDATA` теперь хранит v2.
- **«Удалить»** убирает исходные данные у полностью перенесённых объектов: секции `ESMT_LEP_v1.0`,
  `SMARTLINE`, `BobrovXDATA_V1` и XRecord с XML.
- **Типизированные пары `SMARTLINE`** (`(1070 . 2)`, данные v2 под прежним именем) копируются в
  `BobrovXDATA`; переименования из `rename` к ним тоже применяются.
- **Повторный перенос** ничего не меняет: значения источников уже записаны в v2 и не перезаписываются,
  `DEVICE_ID` есть, строки v1 уже в `BobrovXDATA_V1`.

`VELEC_CHECK` различает два случая. Если данные ещё не перенесены, отчёт советует «перенесите
командой VELEC_MIGRATE». Если данные перенесены и исходные сохранены, отчёт пишет «перенесено,
исходные данные сохранены (удаление — VELEC_MIGRATE «Удалить»)».

## Проверка чертежа

| Вид замечания | Исправление |
| --- | --- |
| Дубли `DEVICE_ID` | Объект с меньшим handle сохраняет `DEVICE_ID`, копии получают новые GUID («Дубли»/«Все»). |
| Данные вне схемы | Нет: `Id` переносится через `rename` (`VELEC_MIGRATE`) или описывается в схеме. |
| Ключи вне справочника, несоответствие типу, неизвестный тип, значения вне ограничений редактора | Нет: правка в VST. |
| Строки длиннее 255 байт, XDATA больше 16 КБ | Нет. |
| Устаревшие результаты `Persist` | Пересчёт формул («Persist»/«Все»). |
| Повреждённые данные, более новая версия формата | Нет: объект только для чтения. |
| Прежние форматы | `VELEC_MIGRATE`. |

## Отчёт

Объекты из `stage3-selection.lsp` и `tzxdata-stage9-migrate.lsp` без AutoCAD:
`experiments/AcadPaletteSetXData.MigrationReport` (CI, шаг «Migrate the stage 3 and stage 9 samples twice
and check the drawing»). В AutoCAD handle объектов будут другими.

```text
== VELEC_MIGRATE (Сохранить) ==
Объект 1F0 (Line): ESMT_LEP_v1.0
  перенесено: Device_Type, Device_Number, NMO_Name, Project_Ref, Materials
  создан DEVICE_ID
Объект 1F1 (Polyline): BobrovXDATA v1, XML VisualTreeString (XRecord BobrovXDATA)
  перенесено: Device_Number, Device_Type, NMO_Name, Materials
  создан DEVICE_ID
Объект 200 (Line): SMARTLINE
  перенесено: Device_Type, NMO_Name, Device_Number, Materials
  создан DEVICE_ID
Объектов: 6, с прежними данными: 3, изменяется: 3, без изменений: 3, с ошибками: 0, перенесено не полностью: 0.
Исходные данные сохраняются (строки v1 — в BobrovXDATA_V1).

== VST прежнего формата (stage3-selection.lsp) ==
1F0: полей 4, материалов 1 — те же значения в v2
1F1: полей 3, материалов 1 — те же значения в v2

== VELEC_MIGRATE повторно ==
Объектов: 6, с прежними данными: 3, изменяется: 0, без изменений: 6, с ошибками: 0, перенесено не полностью: 0.
Исходные данные сохраняются (строки v1 — в BobrovXDATA_V1).
Повторный перенос ничего не меняет.

== VELEC_CHECK ==
Дубли DEVICE_ID: 1
  {0B1C2D3E-0000-4000-8000-000000000001}: сохраняет 9F, новый GUID — 21A
Данные вне схемы: 1
  Объект 230, Old_Field: Id не описан в схеме, значение «x».
Прежние форматы (VELEC_MIGRATE): 3
  Объект 1F0: ESMT_LEP_v1.0: перенесено, исходные данные сохранены (удаление — VELEC_MIGRATE «Удалить»).
  Объект 1F1: XML VisualTreeString (XRecord BobrovXDATA): перенесено, исходные данные сохранены (удаление — VELEC_MIGRATE «Удалить»).
  Объект 200: SMARTLINE: перенесено, исходные данные сохранены (удаление — VELEC_MIGRATE «Удалить»).
Проверено объектов: 6; замечаний: 5, объектов с замечаниями: 5.
Исправимо: дубли DEVICE_ID — объектов 1, Persist — объектов 0.
```

С `--remove` (ответ «Удалить») у каждого изменяемого объекта появляется строка «исходные данные
удаляются». Повторный перенос показывает «с прежними данными: 0», а `VELEC_CHECK` — 2 замечания (дубль
и `Old_Field`). После «Исправить: Все» остаётся одно: `Old_Field`.

## Тесты (`SchemaMigrationTests`)

| Тест | Проверка |
| --- | --- |
| `Stage3SelectionShowsTheSameValuesAndMaterialsAfterMigration` | Приёмка: поля и материалы `stage3-selection.lsp` после переноса в v2 совпадают со значениями прежнего формата. |
| `Stage3ValuesAreShownInThePropertiesTab` | Перенесённые значения видны в VST как параметры схемы и строки таблицы `Materials`. |
| `RepeatedMigrationChangesNothing` | Приёмка: повторный перенос ничего не меняет (с сохранением и с удалением источников). |
| `SourcesAreKeptByDefaultAndV1StringsAreBackedUp` | Источники сохраняются; строки v1 — в `BobrovXDATA_V1`. |
| `TheFlagRemovesTheSourcesOfCompleteObjects` | «Удалить» убирает RegApp и XRecord полностью перенесённых объектов. |
| `KeptSourcesAreRemovedByALaterRunWithTheFlag` | Источники, сохранённые первым переносом, удаляет повторный перенос с «Удалить»; значения v2 не меняются. |
| `AnIncompleteObjectKeepsItsSourcesEvenWithTheFlag` | Ошибка преобразования оставляет источники и при «Удалить». |
| `StoredV2ValuesAreNeverOverwritten` | Значения v2 не перезаписываются, расхождение — предупреждение. |
| `LaterSourcesWinAndConflictsAreReported` | Порядок источников и предупреждения о расхождениях. |
| `EnumCaptionsBecomeKeysAndFreeTextNeedsAllowCustom` | Подписи ENUM → ключи; свободный текст — с предупреждением, без него при `allowCustom`. |
| `SmartlineStringsAndTypedPairsAreMigrated` | Строки `SMARTLINE` с русскими ключами и типизированные пары. |
| `RenameMovesStoredValuesAndTableCellsOnlyThroughTheMigration` | `rename` переносит значения и ячейки таблиц (П3). |
| `ObjectsBreakingTheLimitsAreLeftAsTheyAre` | Строки длиннее 255 байт и XDATA больше 16 КБ — объект не изменяется. |
| `DamagedOrAmbiguousDataIsNotMigrated` | Повторная или незакрытая секция, новая версия, v1 вместе с копией. |
| `ObjectsWithoutLegacyDataAreNotTouched` | Объекты без источников (пустые, посторонние RegApp и XRecord, v2) не меняются. |
| `TheWholeDrawingIsWrittenInOneTransactionAndMigratedRecordsAreRemoved` | `WriteEach` — одна транзакция (П14), удаление перенесённых XRecord. |
| `ValidatorChecksTheMigrationSection` | Коды проверки раздела `migration`. |
| `TheMigrationSectionIsStoredInTheNodWithTheSchema` | Раздел `migration` сохраняется в NOD вместе со схемой; `rename` `Old_Field` → `Device_Class`. |
| `TheMapSkipsWrongEntriesAndFollowsRenamedParameters` | Неверные записи таблицы пропускаются, русские ключи; переименование параметра в редакторе обновляет таблицу. |

## Тесты (`DrawingCheckTests`)

| Тест | Проверка |
| --- | --- |
| `ACleanDrawingGivesOneLine` | Чертёж без замечаний — одна строка отчёта. |
| `TheSmallerHandleKeepsADuplicatedDeviceIdAndTheCopiesGetNewGuids` | Объект с меньшим handle сохраняет `DEVICE_ID`, копии получают новые GUID. |
| `DataOutsideTheSchemaAndTheLimitsAreReported` | Данные вне схемы, ключи ENUM, несоответствие типу, повреждённый `DEVICE_ID`, строки длиннее 255 байт, прежний формат. |
| `LargeXDataIsReported` | XDATA больше 16 КБ. |
| `StalePersistResultsAreRecalculatedOverTheDrawing` | Устаревшие результаты `Persist` и их пересчёт. |
| `ADrawingWithoutASchemaIsCheckedForTheLimitsAndLegacyData` | Без схемы проверяются лимиты, дубли и прежние форматы. |
| `KeptSourcesOfMigratedObjectsAreToldFromObjectsToMigrate` | Сохранённые источники перенесённых объектов не путаются с данными для переноса. |
| `WriteEachRemovesRecordsAndRejectsForeignHandles` | `WriteEach` удаляет XRecord и отклоняет чужие handle. |
| `TheLegacyViewReadsEsmtSmartlineAndTheBackupButNotTypedPairs` | Блок «Прежний формат» VST читает `ESMT_LEP_v1.0`, `SMARTLINE` и копию v1, но не типизированные пары. |

## Проверка

```bash
DOTNET_ROLL_FORWARD=Major dotnet test tests/AcadPaletteSetXData.Core.Tests -c Release
DOTNET_ROLL_FORWARD=Major dotnet run --project experiments/AcadPaletteSetXData.MigrationReport -c Release -- examples/schema.json
DOTNET_ROLL_FORWARD=Major dotnet run --project experiments/AcadPaletteSetXData.MigrationReport -c Release -- --remove examples/schema.json
```

Эксперимент завершается с кодом 1 в трёх случаях: в схеме есть ошибки; значения прежнего формата
не совпадают с v2; повторный перенос что-то меняет. RegistrationSmoke (Windows CI) проверяет, что
`VELEC_MIGRATE` и `VELEC_CHECK` зарегистрированы в выпускной DLL. Ручная проверка в AutoCAD —
[autocad-verification.md](autocad-verification.md#tzxdata-этап-9-миграция-и-проверка-чертежа).

## Ограничения этапа

- Обход чертежа, запросы, блокировка слоёв и Undo в живом AutoCAD CI не проверяет — только ручной сценарий.
- Объекты на заблокированных слоях пропускаются; их нужно разблокировать и запустить команду снова.
- Объекты внешних ссылок не читаются.
- `DEVICE_ID` при копировании объектов автоматически не обновляется — копии находит `VELEC_CHECK`.
- Имя `SMARTLINE` снова есть в коде: константа `DrawingNames.LegacyRegApp` (П11), используется только для
  чтения и переноса. Ещё оно встречается в комментариях, тестах и примерах миграции. Поэтому критерий
  этапа 0 «`grep SMARTLINE` по коду пуст» теперь означает: пуст, кроме данных прежнего формата.
