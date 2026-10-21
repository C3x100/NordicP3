@echo off
REM Builds dist\NordicP3.exe: one 32-bit file with the .NET runtime bundled.
REM Needs the free .NET 8 SDK on the BUILD machine only. The PCs that run
REM NordicP3.exe need nothing except the DiCE driver.

cd /d "%~dp0"

REM The .NET *runtime* also provides dotnet.exe, so check for an actual SDK.
set "HAS_SDK="
for /f "delims=" %%s in ('dotnet --list-sdks 2^>nul') do set "HAS_SDK=1"
if not defined HAS_SDK (
    echo.
    echo No .NET SDK found. This PC only has the .NET runtime, which can run
    echo programs but cannot build them.
    echo.
    echo Install the .NET 8 SDK ^(x64 Windows installer^) from:
    echo   https://dotnet.microsoft.com/download/dotnet/8.0
    echo Pick the "SDK" column, not "Runtime". Then close this window and run build.bat again.
    echo.
    pause
    exit /b 1
)

if exist dist\NordicP3.exe del /q dist\NordicP3.exe

dotnet publish NordicP3.csproj -c Release -o dist
if errorlevel 1 goto failed
if not exist dist\NordicP3.exe goto failed

echo.
echo Built: %~dp0dist\NordicP3.exe
pause
exit /b 0

:failed
echo.
echo Build failed. Copy the error text above and send it over.
pause
exit /b 1
