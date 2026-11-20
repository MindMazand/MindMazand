@echo off
setlocal
where msbuild >nul 2>nul
if errorlevel 1 (
  echo ERROR: MSBuild not found. Install Visual Studio 2022 with .NET desktop development.
  exit /b 2
)
msbuild app\Mazandasht.sln /restore /p:Configuration=Release /p:Platform="Any CPU"
if errorlevel 1 (
  echo BUILD FAILED. Review the MSBuild output; do not deploy.
  exit /b 1
)
echo BUILD SUCCEEDED. This is not yet a signed installer.
echo Output: app\Mazandasht.Desktop\bin\Release
endlocal
