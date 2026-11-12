@echo off
chcp 65001 >nul
setlocal

rem acadPaletteSetXDATA: per-user installation, administrator rights are not required.
rem Copies AppData\Roaming from the archive into %APPDATA% of the current user.

set "SOURCE=%~dp0AppData\Roaming\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle"
set "TARGET=%APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle"
if not exist "%SOURCE%\PackageContents.xml" (
    echo Не найдена папка AppData рядом с install.cmd. Распакуйте архив полностью.
    goto :fail
)

echo Установка acadPaletteSetXDATA в %TARGET% ...
rem /MIR replaces the previous version of the bundle and removes its stale files; other plugins are not touched.
robocopy "%SOURCE%" "%TARGET%" /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if errorlevel 8 (
    echo Ошибка копирования. Закройте AutoCAD и запустите install.cmd снова.
    goto :fail
)

rem Files of a downloaded archive are marked as coming from the internet; .NET may refuse to load such a DLL.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath $env:TARGET -Recurse -File | Unblock-File" >nul 2>&1

echo.
echo Готово.
echo   AutoCAD 2021: %TARGET%
echo Запустите AutoCAD 2021: меню и вкладка ленты Bobrov включают и выключают панель XDATA.
echo Подробности - в файле INSTALL.md.
pause
exit /b 0

:fail
pause
exit /b 1
