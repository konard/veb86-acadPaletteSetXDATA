# Проба задачи #61: спецификации и соединения как `Table`, пронумерованные параметры и тип `Repeat`

`Issue61Probe` строит VST настоящего Core (`SelectionViewModel`, `PropertyGridViewModel`, `ObjectValuesCache`)
по трём схемам примеров и применяет операции `SelectionEdit` через `XDataPatch`:

- [examples/issue61-table-schema.json](../../examples/issue61-table-schema.json) — вариант A (`Table`);
- [examples/issue61-numbered-schema.json](../../examples/issue61-numbered-schema.json) — вариант C
  (`spec_N_pos`, `spec_N_Name`, `spec_N_col`, `conn_N_*`, N = 1..5);
- [examples/issue61-repeat-schema.json](../../examples/issue61-repeat-schema.json) — вариант B, выбранный в задаче:
  тип `Repeat` (`spec`, `conn`), записи «Спецификация №1», «Спецификация №2», …, операции `AddItem`,
  `RemoveLastItem` и `DeleteItem` (удаление из середины со сдвигом номеров).

Каждый факт, на который опирается [docs/repeating-parameters.md](../../docs/repeating-parameters.md), — отдельная
проверка `OK`/`FAIL`; при любом `FAIL` код выхода 1. AutoCAD не нужен.

```bash
dotnet run --project experiments/issue61/Issue61Probe -c Release -- examples
```

С установленным только .NET 10 добавьте `DOTNET_ROLL_FORWARD=Major`. Проба запускается в CI (job `core-tests`).
