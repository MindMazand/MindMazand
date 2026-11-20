@echo off
setlocal enabledelayedexpansion
rem ===================================================================
rem  Mazandasht - Windows x64 Release build
rem
rem  Prerequisites (install once):
rem      setup-windows-prerequisites.bat
rem
rem  Then run this file. It does four things:
rem      1. msbuild app\Mazandasht.sln  (x64, Release)
rem      2. python tools\verify_windows_build.py  (checks the output)
rem      3. ISCC installer\Mazandasht_Setup.iss (if Inno Setup 6 is present)
rem      4. prints where the results are
rem ===================================================================
set ROOT=%~dp0
if "%ROOT:~-1%"=="\" set ROOT=%ROOT:~0,-1%

rem MSBuild is not on PATH in a normal command window, so look for it through
rem vswhere (which Visual Studio installs) before falling back to PATH.
set "MSBUILD="
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
  for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"`) do set "MSBUILD=%%i"
)
if not defined MSBUILD (
  for /f "delims=" %%i in ('where msbuild 2^>nul') do if not defined MSBUILD set "MSBUILD=%%i"
)
if not defined MSBUILD (
  echo ERROR: MSBuild not found.
  echo        Install the prerequisites first:  setup-windows-prerequisites.bat
  echo        (Visual Studio 2022 Build Tools with the ".NET desktop build tools" workload)
  exit /b 2
)

echo [1/4] Restoring and building x64 Release...
"%MSBUILD%" "%ROOT%\app\Mazandasht.sln" /restore /v:minimal ^
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
set "PYTHON="
where py >nul 2>nul && set "PYTHON=py"
if not defined PYTHON where python >nul 2>nul && set "PYTHON=python"
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
set "ISCC="
if defined ISCC_PATH set "ISCC=%ISCC_PATH%"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
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
echo.
rem Keep the window open when this file was started by double-clicking.
pause
endlocal
