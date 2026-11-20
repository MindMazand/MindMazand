@echo off
setlocal enabledelayedexpansion
rem ===================================================================
rem  Mazandasht - Windows x64 Release build
rem  Run this on Windows with Visual Studio 2022 (.NET desktop development)
rem  and, for the installer, Inno Setup 6.
rem ===================================================================
set ROOT=%~dp0
if "%ROOT:~-1%"=="\" set ROOT=%ROOT:~0,-1%

where msbuild >nul 2>nul
if errorlevel 1 (
  echo ERROR: MSBuild not found. Install Visual Studio 2022 with ".NET desktop development".
  exit /b 2
)

echo [1/4] Restoring and building x64 Release...
msbuild "%ROOT%\app\Mazandasht.sln" /restore /v:minimal ^
  /p:Configuration=Release /p:Platform=x64
if errorlevel 1 (
  echo BUILD FAILED. Review the MSBuild output; do not deploy.
  exit /b 1
)

set OUT=%ROOT%\app\Mazandasht.Desktop\bin\x64\Release
if not exist "%OUT%\Mazandasht.Desktop.exe" (
  echo BUILD FAILED: %OUT%\Mazandasht.Desktop.exe was not produced.
  exit /b 1
)

echo [2/4] Verifying the build output...
set PYTHON=
where py >nul 2>nul && set PYTHON=py
if not defined PYTHON (
  where python >nul 2>nul && set PYTHON=python
)
if defined PYTHON (
  %PYTHON% "%ROOT%\tools\verify_windows_build.py" "%OUT%"
  if errorlevel 1 (
    echo VERIFICATION FAILED. Do not ship this build.
    exit /b 1
  )
) else (
  echo WARN: Python not found; the output verification was SKIPPED.
  echo       Run tools\verify_windows_build.py manually before shipping.
)

echo [3/4] Building the installer...
set ISCC=
if defined ISCC_PATH set ISCC=%ISCC_PATH%
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if defined ISCC (
  "%ISCC%" "%ROOT%\installer\Mazandasht_Setup.iss"
  if errorlevel 1 (
    echo INSTALLER BUILD FAILED. The application build itself succeeded.
    exit /b 1
  )
) else (
  echo WARN: Inno Setup 6 not found; the installer was NOT built.
  echo       Install Inno Setup 6 and re-run this script, or set ISCC_PATH.
)

echo [4/4] Done.
echo   Application output: %OUT%
if defined ISCC echo   Installer:        %ROOT%\installer\Output\Mazandasht_Setup.exe
echo.
echo REMINDER: run Mazandasht.Desktop.exe once on the target Windows version
echo (Windows 8.1 Update x64 or newer) before delivering it.
endlocal
