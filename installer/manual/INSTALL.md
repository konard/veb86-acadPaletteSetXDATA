# acadPaletteSetXDATA — ручная установка (AutoCAD 2021)

Архив `acadPaletteSetXDATA-manual-install-v<версия>.zip` предназначен для установки
**без установщика и без прав администратора**: все файлы размещаются только в профиле
текущего пользователя (`C:\Users\<имя>\AppData\Roaming`, он же `%APPDATA%`).
AutoCAD загружает плагин автоматически при запуске.

## Состав архива

| Путь в архиве | Назначение | Куда устанавливается |
| --- | --- | --- |
| `AppData\Roaming\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle\` | пакет автозагрузки AutoCAD (`PackageContents.xml` + DLL) | `%APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle\` |
| `…\acadPaletteSetXDATA.bundle\Contents\AutoCAD2021-2024\acadPaletteSetXDATA.dll` | плагин для **AutoCAD 2021** x64 (.NET Framework 4.8) | — |
| `install.cmd` | копирует пакет в профиль пользователя | — |
| `uninstall.cmd` | удаляет пакет из профиля пользователя | — |
| `release-manifest.txt` | версия, commit и SHA-256 файлов архива | — |
| `INSTALL.md`, `LICENSE` | эта инструкция и лицензия | — |

Плагин состоит из одной DLL; библиотеки AutoCAD (`AcMgd.dll`, `AcDbMgd.dll`, `AcCoreMgd.dll`,
`AdWindows.dll`) предоставляет сам AutoCAD и в архив не входят.

## 1. Распаковка

1. Закройте AutoCAD.
2. Если плагин раньше загружался вручную через `NETLOAD` из `acadPaletteSetXDATA.zip`
   или через автозагрузку `APPLOAD`, уберите эту DLL из «Автозагрузки», чтобы AutoCAD
   не загрузил две копии плагина.
3. Windows может пометить скачанные файлы как полученные из интернета. Перед распаковкой
   откройте свойства ZIP-архива и установите флажок **«Разблокировать»**, иначе .NET может
   отказаться загружать DLL (`install.cmd` дополнительно снимает эту отметку).
4. Распакуйте архив в любую временную папку.

## 2. Копирование файлов в профиль пользователя

**Вариант А — автоматически.** Запустите `install.cmd` двойным щелчком (права администратора
не нужны). Скрипт скопирует папку `acadPaletteSetXDATA.bundle` в
`%APPDATA%\Autodesk\ApplicationPlugins` и заменит предыдущую версию.

**Вариант Б — вручную.**

1. Нажмите `Win+R`, введите `%USERPROFILE%` и нажмите Enter — откроется `C:\Users\<имя>`.
2. Скопируйте туда папку `AppData` из архива и подтвердите слияние папок.

После копирования должны существовать файлы:

```text
%APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle\PackageContents.xml
%APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle\Contents\AutoCAD2021-2024\acadPaletteSetXDATA.dll
```

## 3. AutoCAD 2021

AutoCAD автоматически загружает пакеты из папки пользователя
`%APPDATA%\Autodesk\ApplicationPlugins`, поэтому достаточно запустить AutoCAD.

1. Запустите AutoCAD 2021.
2. Если появится предупреждение безопасности о загрузке `acadPaletteSetXDATA.dll`, выберите
   **«Всегда загружать»** либо добавьте папку в доверенные пути (команда `OPTIONS` →
   «Файлы» → «Надёжные папки» или системная переменная `TRUSTEDPATHS`):

   ```text
   %APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle\...
   ```

3. В командной строке появится сообщение `acadPaletteSetXDATA загружено…`.
4. Откройте меню **Bobrov → Включить палитру XDATA** или вкладку ленты
   **Bobrov → Палитра XDATA → Включить**. Кнопки **Выключить** скрывают панель.
   Команды: `XDATAPALETTE_ON`, `XDATAPALETTE_OFF`, `XDATAPALETTE` (показать/скрыть).

При загрузке плагин включает строку меню (`MENUBAR` = 1), так как в AutoCAD 2021 она по
умолчанию скрыта; скрыть её снова можно командой `MENUBAR` → `0`, вкладка ленты **Bobrov**
останется доступна. Меню и вкладка не сохраняются в CUIX: плагин добавляет их при каждом
запуске и после смены рабочего пространства.

Если плагин не загрузился автоматически, выполните `NETLOAD` и выберите
`%APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle\Contents\AutoCAD2021-2024\acadPaletteSetXDATA.dll`.
В поле имени файла можно вставить путь целиком — `%APPDATA%` будет раскрыт.

`PackageContents.xml` разрешает загрузку в AutoCAD 2021–2024 (`R24.0`–`R24.3`, .NET Framework 4.8);
проверенной целевой версией остаётся AutoCAD 2021. AutoCAD 2025 и новее работают на .NET 8
и этот пакет не загружают.

## 4. Обновление

1. Закройте AutoCAD.
2. Распакуйте новую версию архива и запустите `install.cmd` (или повторите шаг 2 с заменой файлов).

## 5. Удаление

1. Закройте AutoCAD.
2. Запустите `uninstall.cmd` или удалите вручную папку:

   ```text
   %APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle
   ```

Данные XDATA и схемы параметров хранятся в чертежах и при удалении плагина сохраняются.
