# Рендеры WPF-панели

Изображения `stage1-<тема>-<вкладка>.png` созданы настоящим WPF `UserControl`
на Windows, а не отдельным макетом. Индексы вкладок: `0` — «Свойства»,
`1` — «XDATA», `2` — «Команды». Здесь сохранены рендеры .NET 8; тот же сценарий
успешно выполнен и на .NET Framework 4.8.

Источник: [CI, commit 378f034](https://github.com/konard/veb86-acadPaletteSetXDATA/actions/runs/37442186241),
артефакт `wpf-renders/net8`. Генератор: `experiments/AcadPaletteSetXData.WpfSmoke`.
Изображения показывают только содержимое палитры, без нативной рамки AutoCAD.

| Тёмная тема | Светлая тема |
| --- | --- |
| ![Свойства, тёмная тема](stage1-dark-0.png) | ![Свойства, светлая тема](stage1-light-0.png) |
| ![XDATA, тёмная тема](stage1-dark-1.png) | ![XDATA, светлая тема](stage1-light-1.png) |
| ![Команды, тёмная тема](stage1-dark-2.png) | ![Команды, светлая тема](stage1-light-2.png) |
