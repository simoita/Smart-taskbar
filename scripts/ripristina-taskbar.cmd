@echo off
setlocal
echo Ripristino della taskbar di Windows...
taskkill /F /IM Nebula.exe >nul 2>&1
ping -n 3 127.0.0.1 >nul
if exist "%~dp0..\dist\Nebula.exe" (
    "%~dp0..\dist\Nebula.exe" --restore-taskbar
) else (
    echo Nebula.exe non trovato in dist. Esegui --restore-taskbar dalla cartella dell'app.
)
endlocal
