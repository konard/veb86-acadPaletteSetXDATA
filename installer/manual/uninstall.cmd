@echo off
chcp 65001 >nul
setlocal

rem acadPaletteSetXDATA: removes the per-user installation made by install.cmd.

set "TARGET=%APPDATA%\Autodesk\ApplicationPlugins\acadPaletteSetXDATA.bundle"
if not exist "%TARGET%" (
    echo acadPaletteSetXDATA не установлен: %TARGET%
    pause
    exit /b 0
)

rmdir /s /q "%TARGET%"
if exist "%TARGET%" (
    echo Не удалось удалить %TARGET%. Закройте AutoCAD и запустите uninstall.cmd снова.
    pause
    exit /b 1
)

echo Удалено: %TARGET%
echo Данные XDATA в чертежах сохраняются.
pause
exit /b 0
