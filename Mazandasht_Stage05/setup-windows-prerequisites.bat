@echo off
setlocal enabledelayedexpansion
rem ===================================================================
rem  Mazandasht - Windows prerequisites installer
rem
rem  Installs only what is missing:
rem    1. MSBuild + .NET Framework 4.8 targeting pack
rem       (Visual Studio 2022 Build Tools, ".NET desktop build tools")
rem    2. Inno Setup 6                  (installer compiler)
rem    3. Python 3.11                   (output verification)
rem
rem  Needs administrator rights and an internet connection.
rem
rem  Usage:  setup-windows-prerequisites.bat
rem          setup-windows-prerequisites.bat --yes
rem ===================================================================

set "ASSUME_YES="
if /i "%~1"=="--yes" set "ASSUME_YES=1"

echo ============================================================
echo   Mazandasht - Windows prerequisites
echo ============================================================
echo.
echo This installs the tools needed to build Mazandasht on Windows.
echo It installs nothing else and removes nothing.
echo.

where winget >nul 2>nul
if errorlevel 1 goto :no_winget

set "MISSING="

rem -------------------------------------------------------------------
rem 1. MSBuild (Visual Studio 2022 Build Tools)
rem -------------------------------------------------------------------
call :find_msbuild
if defined MSBUILD_PATH (
  echo [OK]   MSBuild found: %MSBUILD_PATH%
) else (
  echo [MISS] MSBuild not found.
  call :ask "Install Visual Studio 2022 Build Tools?"
  if defined DO_INSTALL (
    echo        Downloading and installing. This can take 20-40 minutes.
    echo        Do not close this window.
    winget install --id Microsoft.VisualStudio.2022.BuildTools --exact --silent --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.Net.Component.4.8.TargetingPack --includeRecommended"
    if errorlevel 1 (
      echo [FAIL] winget could not install the Build Tools.
      set "MISSING=1"
    ) else (
      call :find_msbuild
      if defined MSBUILD_PATH (
        echo [OK]   MSBuild installed: %MSBUILD_PATH%
      ) else (
        echo [FAIL] The Build Tools were installed but MSBuild was not found.
        echo        Open a new command window and run this script again.
        set "MISSING=1"
      )
    )
  ) else (
    set "MISSING=1"
  )
)
echo.

rem -------------------------------------------------------------------
rem 2. Inno Setup 6
rem -------------------------------------------------------------------
call :find_iscc
if defined ISCC_PATH (
  echo [OK]   Inno Setup 6 found: %ISCC_PATH%
) else (
  echo [MISS] Inno Setup 6 not found.
  call :ask "Install Inno Setup 6?"
  if defined DO_INSTALL (
    winget install --id JRSoftware.InnoSetup --exact --silent
    if errorlevel 1 (
      echo [FAIL] winget could not install Inno Setup 6.
      set "MISSING=1"
    ) else (
      call :find_iscc
      if defined ISCC_PATH (
        echo [OK]   Inno Setup 6 installed: %ISCC_PATH%
      ) else (
        echo [FAIL] Inno Setup 6 was installed but ISCC.exe was not found.
        echo        Open a new command window and run this script again.
        set "MISSING=1"
      )
    )
  ) else (
    set "MISSING=1"
  )
)
echo.

rem -------------------------------------------------------------------
rem 3. Python 3.11 (only used to verify the build output)
rem -------------------------------------------------------------------
call :find_python
if defined PYTHON_PATH (
  echo [OK]   Python found: %PYTHON_PATH%
) else (
  echo [MISS] Python not found (optional: it only verifies the build output).
  call :ask "Install Python 3.11?"
  if defined DO_INSTALL (
    winget install --id Python.Python.3.11 --exact --silent
    if errorlevel 1 (
      echo [FAIL] winget could not install Python.
      set "MISSING=1"
    ) else (
      call :find_python
      if defined PYTHON_PATH (
        echo [OK]   Python installed: %PYTHON_PATH%
      ) else (
        echo [WARN] Python was installed but is not on PATH yet.
        echo        Open a new command window and run this script again.
      )
    )
  )
)
echo.

if defined MISSING (
  echo ============================================================
  echo   Some prerequisites are still missing.
  echo   Install them manually - see installer\README_FA.md.
  echo ============================================================
  exit /b 1
)

echo ============================================================
echo   All prerequisites are ready.
echo.
echo   Now run:  build-windows.bat
echo ============================================================
exit /b 0

rem -------------------------------------------------------------------
rem :find_msbuild   - fills MSBUILD_PATH (empty when not installed)
rem -------------------------------------------------------------------
:find_msbuild
set "MSBUILD_PATH="
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
  for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"`) do set "MSBUILD_PATH=%%i"
)
if not defined MSBUILD_PATH (
  for /f "delims=" %%i in ('where msbuild 2^>nul') do if not defined MSBUILD_PATH set "MSBUILD_PATH=%%i"
)
exit /b 0

rem -------------------------------------------------------------------
rem :find_iscc      - fills ISCC_PATH (empty when not installed)
rem -------------------------------------------------------------------
:find_iscc
set "ISCC_PATH="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC_PATH=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC_PATH if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC_PATH=%ProgramFiles%\Inno Setup 6\ISCC.exe"
exit /b 0

rem -------------------------------------------------------------------
rem :find_python    - fills PYTHON_PATH (empty when not installed)
rem -------------------------------------------------------------------
:find_python
set "PYTHON_PATH="
where py >nul 2>nul && set "PYTHON_PATH=py"
if not defined PYTHON_PATH where python >nul 2>nul && set "PYTHON_PATH=python"
exit /b 0

rem -------------------------------------------------------------------
rem :ask            - sets DO_INSTALL when the user agrees
rem -------------------------------------------------------------------
:ask
set "DO_INSTALL="
echo     %~1
if defined ASSUME_YES (
  echo     (auto-yes) installing.
  set "DO_INSTALL=1"
) else (
  set /p "DO_INSTALL=     Type Y and press Enter to install: "
  if /i "!DO_INSTALL!"=="Y" (set "DO_INSTALL=1") else (set "DO_INSTALL=")
)
exit /b 0

rem -------------------------------------------------------------------
rem :no_winget      - winget is missing (Windows 8.1 or an old Windows 10)
rem -------------------------------------------------------------------
:no_winget
echo ERROR: winget is not available on this computer.
echo        This means Windows 8.1, or Windows 10 older than build 1809.
echo.
echo        Install the prerequisites manually - see installer\README_FA.md:
echo.
echo          1. Visual Studio 2022 Build Tools
echo             (workload: ".NET desktop build tools")
echo          2. .NET Framework 4.8 Developer Pack
echo          3. Inno Setup 6
echo          4. Python 3.11          (optional, verifies the build output)
echo.
exit /b 3
