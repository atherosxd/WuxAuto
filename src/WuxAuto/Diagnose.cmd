@echo off
rem Starts WuxAuto and records why it fails to start: the .NET runtime's own error text
rem (it prints to stderr, which a double-click throws away), system facts, and whether
rem it starts with some runtime features switched off. Result: diagnose.txt, opened in Notepad.
setlocal
set "APP=%~dp0WuxAuto.exe"
set "OUT=%~dp0diagnose.txt"
(echo.) > "%OUT%" 2>nul || set "OUT=%TEMP%\WuxAuto-diagnose.txt"

> "%OUT%" echo WuxAuto diagnose %DATE% %TIME%
>> "%OUT%" echo App: %APP%
>> "%OUT%" echo.
>> "%OUT%" echo === System ===
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v ProductName >> "%OUT%" 2>&1
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v DisplayVersion >> "%OUT%" 2>&1
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v CurrentBuild >> "%OUT%" 2>&1
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v UBR >> "%OUT%" 2>&1
reg query "HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0" /v ProcessorNameString >> "%OUT%" 2>&1
>> "%OUT%" echo PROCESSOR_ARCHITECTURE=%PROCESSOR_ARCHITECTURE% PROCESSOR_IDENTIFIER=%PROCESSOR_IDENTIFIER%
>> "%OUT%" echo.
>> "%OUT%" echo === System DLLs ===
for %%D in (mfplat.dll mf.dll mfreadwrite.dll d3d11.dll dxgi.dll xmllite.dll icu.dll Windows.Media.Ocr.dll) do (
    if exist "%SystemRoot%\System32\%%D" (>> "%OUT%" echo found   %%D) else (>> "%OUT%" echo MISSING %%D)
)
>> "%OUT%" echo.
>> "%OUT%" echo === .NET environment variables ===
set DOTNET_ >> "%OUT%" 2>nul
set COMPlus_ >> "%OUT%" 2>nul
>> "%OUT%" echo.

echo Starting WuxAuto. If its window opens, close it to finish.
call :attempt "1 normal"
if "%CODE%"=="0" goto done
if "%CODE%"=="1" goto done
set DOTNET_EnableWriteXorExecute=0
call :attempt "2 write-xor-execute off"
if "%CODE%"=="0" goto done
if "%CODE%"=="1" goto done
set DOTNET_TieredCompilation=0
set DOTNET_TC_QuickJitForLoops=0
call :attempt "3 also tiered JIT off"
if "%CODE%"=="0" goto done
if "%CODE%"=="1" goto done
set DOTNET_EnableHWIntrinsic=0
set DOTNET_gcConcurrent=0
call :attempt "4 also CPU intrinsics and background GC off"

:done
>> "%OUT%" echo.
>> "%OUT%" echo === Newest app reports (%LOCALAPPDATA%\WuxAuto\logs) ===
call :newest crash
call :newest startup_check
echo Done. Send this file: %OUT%
start "" notepad "%OUT%"
exit /b 0

:attempt
>> "%OUT%" echo === Attempt %~1 ===
"%APP%" 2>> "%OUT%"
set "CODE=%ERRORLEVEL%"
>> "%OUT%" echo exit code %CODE% (0 = closed normally, 1 = app showed its own error, other = crash)
>> "%OUT%" echo.
exit /b %CODE%

:newest
for /f "delims=" %%F in ('dir /b /o-d "%LOCALAPPDATA%\WuxAuto\logs\%~1_*.txt" 2^>nul') do (
    >> "%OUT%" echo --- %%F ---
    type "%LOCALAPPDATA%\WuxAuto\logs\%%F" >> "%OUT%" 2>&1
    >> "%OUT%" echo.
    exit /b 0
)
>> "%OUT%" echo (no %~1 report)
exit /b 0
