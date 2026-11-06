@echo off
setlocal
set "ELECTRON_RUN_AS_NODE="
set "DOXIE_LAUNCH_EXE=%~dp0DoxieOS.exe"
if exist "%DOXIE_LAUNCH_EXE%" goto launch
set "DOXIE_LAUNCH_EXE=%~dp0release\DoxieOS-win32-x64\DoxieOS.exe"
if exist "%DOXIE_LAUNCH_EXE%" goto launch
set "DOXIE_LAUNCH_EXE=%~dp0release\DoxieOS-win32-arm64\DoxieOS.exe"
if exist "%DOXIE_LAUNCH_EXE%" goto launch

set "DOXIE_LAUNCH_EXE=%~dp0node_modules\electron\dist\electron.exe"
if not exist "%DOXIE_LAUNCH_EXE%" goto missing
if not exist "%~dp0node_modules\.bin\vite.cmd" goto missing
pushd "%~dp0."
if errorlevel 1 exit /b 1
call "%~dp0node_modules\.bin\vite.cmd" build
if errorlevel 1 goto build_failed
start "" "%DOXIE_LAUNCH_EXE%" "%~dp0."
set "DOXIE_LAUNCH_RESULT=%ERRORLEVEL%"
popd
exit /b %DOXIE_LAUNCH_RESULT%

:launch
for %%F in ("%DOXIE_LAUNCH_EXE%") do start "" /D "%%~dpF." "%%~fF"
exit /b %ERRORLEVEL%

:build_failed
popd
echo DoxieOS could not build the local interface. See the messages above.
pause
exit /b 1

:missing
echo DoxieOS is missing. Extract the portable package or follow the setup in README.md.
pause
exit /b 1
