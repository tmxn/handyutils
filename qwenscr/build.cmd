@echo off
setlocal
cd /d "%~dp0"
dotnet publish qsc.csproj -c Release -r win-x64 --self-contained false -o out %*
if errorlevel 1 (
  echo BUILD FAILED >&2
  exit /b 1
)
echo OK: out\qsc.exe
