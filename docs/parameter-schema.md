# Схема параметров BobrovXDATA: модель и `schema.json` (TZXDATA, этап 1)

Этап 1 из [TZXDATA](../TZXDATA.md) реализован в `Core` и не зависит от AutoCAD/WPF.
Код компилируется в единую DLL `net48` и в `netstandard2.0` для `net8`; внешние
библиотеки JSON не используются (`System.Text.Json` недоступна в `net48` без лишних DLL).

| Файл | Назначение |
| --- | --- |
| `src/AcadPaletteSetXData.Core/ParameterSchema.cs` | Модель: `ParameterSchema`, `SchemaParameter`, `ParameterEditor`, `SchemaEnum`, `SchemaEnumItem`, `SchemaTemplate`, `TemplateParameter`. |
| `src/AcadPaletteSetXData.Core/ParameterTypes.cs` | Реестр типов `ParameterTypeRegistry` и интерфейс `IParameterType` (раздел 11), разбор формата `CalculatedString`. |
| `src/AcadPaletteSetXData.Core/SchemaValidator.cs` | Проверки раздела 4.5, список ошибок «объект — поле — причина». |
| `src/AcadPaletteSetXData.Core/ParameterSchemaJson.cs` | Чтение/запись `schema.json`. |
| `src/AcadPaletteSetXData.Core/Json.cs` | Строгий JSON-DOM (RFC 8259) с ограничениями глубины 64 и размера 16 MiB. |
| `examples/schema.json` | Пример: шаблоны DEVICE, CABLE, TRAY, MOTOR; справочники Voltage, Phase; таблица материалов. |

## Использование

```csharp
var result = ParameterSchemaJson.LoadFile("schema.json");   // чтение + проверка
foreach (var issue in result.Issues) Console.WriteLine(issue); // «Параметр Power — default — ожидается число»
if (!result.HasErrors) ParameterSchemaJson.WriteFile("schema.json", result.Schema!);
```

`Read` только отображает JSON на модель (синтаксис и типы полей), `Load` дополнительно
вызывает `SchemaValidator`. Предупреждения не блокируют запись схемы, ошибки — блокируют
(в NOD на этапе 2).

## Формат

Формат и имена полей — раздел 5.1 ТЗ: `format: "BobrovXDATA.schema"`, `version: 1`,
списки `parameters`, `enums`, `templates`. Имена полей JSON — camelCase и **чувствительны
к регистру** (`id`, `editor.maxLength`); поле с другим регистром (`Id`) сохраняется как
неизвестное с предупреждением `FieldNameCase`. Значения `Id` регистр не учитывают (4.1).

| Объект | Поля |
| --- | --- |
| Параметр | `id`, `type`, `caption`, `description`, `group`, `default`, `enum`, `format`, `persist`, `columns`, `groupBy`, `key`, `editor` |
| `editor` | `unit`, `precision`, `min`, `max`, `maxLength`, `readOnly`, `multiline`, `allowCustom`, `vstColumn` (8.2) |
| Справочник | `id`, `caption`, `items[]` из `{ key, caption }` |
| Шаблон | `id`, `caption`, `entityTypes[]`, `parameters[]` — строка `"Id"` или `{ "id": ..., "default": ... }` |
| `migration` (корень, этап 9) | `fields` — ключ прежнего формата → `Id`; `tables` — метка списка → `{ "table": Id Table, "columns": { ключ → колонка } }`; `rename` — `старый Id` → `новый Id` |

Раздел `migration` — таблица соответствия для `VELEC_MIGRATE`. Он хранится в `Extra` корня, поэтому
round-trip без потерь сохраняет его в JSON и в `NOD/VELEC/INFO`. Правила переноса —
[migration-check.md](migration-check.md#раздел-migration-схемы).

## Round-trip без потерь

- Все поля модели nullable: отсутствующее поле не появляется при записи.
- Неизвестные поля каждого уровня (корень, параметр, `editor`, справочник, элемент,
  шаблон, ссылка шаблона) хранятся в `Extra` в исходном порядке и записываются после известных.
- Известное поле неверного JSON-типа (`"caption": 5`) не теряется: ошибка `InvalidFieldType`,
  значение остаётся в `Extra` и записывается обратно.
- `default` хранится как JSON-значение с исходным текстом числа (`2.50`, `1e2`).
- Числа пишутся с точкой независимо от культуры (`InvariantCulture`), файл — UTF-8 без BOM
  (BOM при чтении допускается), перевод строки LF.
- Запись канонична и детерминирована: известные поля в порядке таблицы выше, отступ
  2 пробела, объект/массив из скаляров — в одну строку, если она не длиннее 100 символов.
  Повторная запись канонического файла даёт те же байты; `examples/schema.json` хранится
  в канонической форме. Отсутствующие списки `parameters`/`enums`/`templates`/`items`
  записываются как `[]`.

## Проверки (раздел 4.5)

Каждая проблема — `SchemaIssue` с кодом, важностью, объектом (`Параметр Power`,
`Справочник Phase / элемент A`, `Шаблон MOTOR`, `Параметр #3` при отсутствии `id`), полем и причиной.

| Код | Важность | Условие |
| --- | --- | --- |
| `InvalidJson` | ошибка | синтаксис JSON (со строкой и столбцом), повтор поля, корень не объект |
| `InvalidFieldType` | ошибка | известное поле неверного типа; значение сохраняется |
| `FieldNameCase` | предупреждение | имя поля отличается от известного только регистром |
| `Required` | ошибка | нет `format`, `version`, `id`, `caption`, `type`, `enum` (Enum), `format` (CalculatedString), `columns` (Table), ключа элемента, `id` ссылки шаблона |
| `UnsupportedFormat` | ошибка | `format` не `BobrovXDATA.schema` |
| `UnsupportedVersion` | ошибка / предупреждение | `version < 1` / версия новее поддерживаемой |
| `InvalidId` | ошибка | `id` не соответствует `[A-Za-z_][A-Za-z0-9_]{0,63}` |
| `ReservedId` | ошибка | параметр `DEVICE_ID`, `TEMPLATE`, `FMT` (без учёта регистра) |
| `DuplicateId` | ошибка | повтор `id` в разделе (без учёта регистра) или ключа в справочнике (с учётом регистра — ключ хранится в XDATA как есть) |
| `UnknownType` | предупреждение | тип не зарегистрирован; параметр сохраняется и будет только для чтения (раздел 11) |
| `UnknownReference` | ошибка | `enum`, `@@[Id]`, `columns`, `groupBy`/`key` вне колонок, параметр шаблона |
| `DuplicateReference` | ошибка | повтор колонки таблицы или параметра шаблона |
| `NotApplicable` | предупреждение | поле не относится к типу (например, `format` у `String`, `editor.precision` у `Integer`) |
| `InvalidDefault` | ошибка | `default` параметра или шаблона не соответствует типу, `min`/`max`, `maxLength`, 255 байтам UTF-8, 32-битному `Integer` или отсутствует в справочнике (кроме `allowCustom`); у `Table` значения по умолчанию нет |
| `InvalidEditor` | ошибка | `precision` вне 0–15, `maxLength` вне 1–255, `min > max`, `vstColumn` вне 1–4 |
| `InvalidFormat` | ошибка | незакрытая/неверная ссылка `@@[`, `:key` не у `Enum`, подстановка `Table` |
| `CyclicFormat` | ошибка | цикл между `CalculatedString`, включая ссылку на себя: `A → B → A` |
| `InvalidColumn` | ошибка | колонка `Table` — `Table` или `CalculatedString` с `persist`; `groupBy` не `Enum` |
| `InvalidValue` | ошибка | ключ справочника с пробелами по краям или длиннее 255 байт UTF-8; пустая строка в `entityTypes`; в `migration` — цель `Table`/`CalculatedString`, переименование в себя, цепочка переименований (предупреждение — старый `Id` из `rename` ещё описан в схеме) |

Ошибки раздела `migration` выводятся под объектом «Миграция». Помимо `InvalidValue`, это коды
`InvalidFieldType` (не объект), `UnknownReference` (параметр не описан), `Required` (нет `table`),
`InvalidColumn` (колонка не из `Columns`), `InvalidId`/`ReservedId` (в `rename`) и `NotApplicable`
(неизвестное поле) — [migration-check.md](migration-check.md#раздел-migration-схемы).

Новый тип параметра регистрируется кодом: реализация `IParameterType` добавляется
в `ParameterTypeRegistry` и передаётся в `Load`/`SchemaValidator`. Разбор XDATA,
форматирование и редактор VST добавляются в интерфейс на следующих этапах.

## Проверка

```sh
dotnet test tests/AcadPaletteSetXData.Core.Tests -c Release
dotnet run --project experiments/AcadPaletteSetXData.SchemaCheck -c Release -- examples/schema.json
dotnet run --project experiments/AcadPaletteSetXData.SchemaCheck -c Release -- --format my-schema.json
```

Тесты (`JsonTests`, `ParameterSchemaJsonTests`, `SchemaValidatorTests`) проверяют побайтовый
round-trip примера, round-trip неканонического файла с неизвестными полями на всех уровнях,
сохранение полей неверного типа, культуру `ru-RU`, ошибки синтаксиса JSON и каждый код
проверки схемы (тест `EveryValidationCodeIsCovered` не даёт добавить код без теста).
`SchemaCheck` выводит замечания и с `--format` переписывает файл в канонической форме;
код возврата 1 при ошибках. CI проверяет им `examples/schema.json`.
