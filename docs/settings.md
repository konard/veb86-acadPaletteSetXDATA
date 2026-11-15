# Общие настройки панели и скрытая группа `SYSTEM` (задача #48)

## Скрытая группа `SYSTEM`

Параметры группы схемы **`SYSTEM`** (поле `group` параметра, раздел 4.1) — служебные данные: по
умолчанию вкладка «Свойства» их не показывает. Скрываются:

- строки параметров группы `SYSTEM` — и из шаблона объекта, и сохранённые в XDATA вне шаблона
  (в группе «Дополнительные» их тоже нет);
- сама группа `SYSTEM` в VST;
- пункты меню «+ Параметр».

Имя группы сравнивается без учёта регистра и пробелов по краям: `SYSTEM`, `system`, ` System `.
Значения в XDATA не меняются: скрытие касается только отображения. Остальные группы, «Не описаны в
схеме (N)» и «Прежний формат» показываются как раньше.

Пример схемы с тремя параметрами `SYSTEM` — [examples/issue48-system-group-schema.json](../examples/issue48-system-group-schema.json).

| Скрыта (по умолчанию) | Показана |
| --- | --- |
| ![SYSTEM скрыта](screenshots/issue48-system-hidden.png) | ![SYSTEM показана](screenshots/issue48-system-shown.png) |

## Окно «Настройки панели XDATA»

Кнопка «⚙» справа на панели управления вкладки «Свойства» → «Настройки…». Кнопка доступна всегда;
пункты схемы («Редактор схемы…», «Импорт JSON…», «Экспорт JSON…») без AutoCAD недоступны, как раньше.

| Тёмная тема | Светлая тема |
| --- | --- |
| ![Настройки, тёмная тема](screenshots/issue48-settings-dark.png) | ![Настройки, светлая тема](screenshots/issue48-settings-light.png) |

- Флажок **«Показывать скрытую группу SYSTEM»** (по умолчанию снят).
- «ОК» (Enter) применяет флажок: VST перестраивается сразу, без нового выделения, и настройки
  записываются в файл. «Отмена» (Esc) и крестик ничего не меняют.
- Окно модальное (`Application.ShowModalWindow` в AutoCAD) и открывается в теме панели
  (`COLORTHEME`).

### Файл настроек

`%APPDATA%\AcadPaletteSetXData\settings.json` — свой у каждого пользователя Windows:

```json
{ "showSystemGroup": true }
```

- Файл читается при создании панели. Нет файла, ошибка JSON или неверный тип значения — настройки
  по умолчанию; панель открывается в любом случае.
- Запись идёт через временный файл `settings.json.tmp`. Если папка недоступна для записи, в командной
  строке AutoCAD появляется сообщение, а настройка действует до закрытия AutoCAD.
- Неизвестные свойства файла (из новых версий плагина) сохраняются при записи.

## Фон пустого списка в тёмной теме

Без выделения VST вкладки «Свойства» и дерево вкладки «XDATA» неактивны (`IsEnabled = false`).
Стандартные шаблоны WPF `ListView` и `TreeView` для этого состояния рисуют фон системным цветом
`SystemColors.ControlBrush`, поэтому в тёмной теме AutoCAD поле было белым. Стили `ThemedListView` и
`ThemedTreeView` (`Themes/Controls.xaml`) рисуют фон цветом темы `PaletteBackground` в любом состоянии.
Для `ListView` шаблон сохраняет `GridView.GridViewScrollViewerStyleKey`, поэтому заголовки колонок
работают как раньше.

| До (тёмная тема) | После (тёмная тема) | После (светлая тема) |
| --- | --- | --- |
| ![До](screenshots/issue48-empty-before.png) | ![После](screenshots/issue48-empty-properties-dark.png) | ![После, светлая](screenshots/issue48-empty-properties-light.png) |

## Файлы

| Файл | Что делает |
| --- | --- |
| `src/AcadPaletteSetXData.Core/PaletteSettings.cs` | Настройки, `IsSystemGroup`, JSON, чтение и запись файла. |
| `src/AcadPaletteSetXData.Core/PropertyGrid.cs` | `IsHidden`: VST и «+ Параметр» пропускают группу `SYSTEM`; изменение настройки перестраивает VST. |
| `src/AcadPaletteSetXData.UI/SettingsWindow.xaml(.cs)` | Окно настроек: черновик, «ОК»/«Отмена», тема. |
| `src/AcadPaletteSetXData.UI/PropertyGridView.xaml(.cs)` | Пункт «Настройки…» меню «⚙». |
| `src/AcadPaletteSetXData.UI/Themes/Controls.xaml` | `ThemedListView` и `ThemedTreeView`: фон темы в неактивном состоянии. |
| `src/AcadPaletteSetXData.AutoCAD/AutoCadPaletteHost.cs` | Чтение и запись `settings.json`, `ShowModalWindow`. |

## Проверка

- `SystemGroupTests` (Linux CI): скрытие и показ группы в VST и в «+ Параметр», регистр имени группы,
  JSON и битые файлы, черновик окна, запись и чтение файла.
- WPF smoke (Windows CI, net48 и net8): фон пустых VST и дерева XDATA — шаблон и пиксели рендера в тёмной и
  светлой темах; меню «⚙» без AutoCAD и с ним; окно настроек в `ShowDialog` — флажок, «ОК», «Отмена»,
  тема и владелец окна; строки `SYSTEM` до и после. Рендеры `issue48-*.png`.
- Ручная проверка в AutoCAD — [autocad-verification.md](autocad-verification.md#скрытая-группа-system-и-настройки-задача-48).
