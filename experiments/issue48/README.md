# Задача #48: белый фон пустого VST в тёмной теме

## Причина

Без выделения `ListView` вкладки «Свойства» (`IsEnabled="{Binding IsEnabled}"`) и `TreeView` вкладки
«XDATA» (`IsEnabled="{Binding HasSelection}"`) неактивны. Стандартные шаблоны WPF (Aero2) для
`IsEnabled = false` заменяют фон рамки на `SystemColors.ControlBrush` — `#F4F4F4` в рендере, — поэтому
`Background="{DynamicResource PaletteBackground}"` элемента в этом состоянии не действует.

## Проверка по рендерам CI

```bash
python3 -I experiments/issue48/png_pixels.py docs/screenshots/issue48-empty-before.png
python3 -I experiments/issue48/png_pixels.py docs/screenshots/issue48-empty-properties-dark.png
```

| Рендер | Точки в пустом VST |
| --- | --- |
| До — `tzxdata-stage6-empty.png`, [CI 87fcb72](https://github.com/konard/veb86-acadPaletteSetXDATA/actions/runs/43684344921) | `#F4F4F4` (системный цвет) |
| После — [CI 63df5a5](https://github.com/konard/veb86-acadPaletteSetXDATA/actions/runs/43685434423) | `#3D4451` (`PaletteBackground` тёмной темы) |

Та же проверка — `CheckBackground` в `experiments/AcadPaletteSetXData.WpfSmoke` (шаблонная рамка `Bd` и
пиксели рендера в тёмной и светлой темах, VST и дерево XDATA). На старом шаблоне она падает:
пиксели `#F4F4F4` не равны цвету темы.
