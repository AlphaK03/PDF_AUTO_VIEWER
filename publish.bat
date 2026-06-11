@echo off
REM ============================================================
REM  PDF Auto Viewer (.NET 8) — Build script
REM ============================================================
REM  Prerequisite: .NET 8 SDK
REM    https://dotnet.microsoft.com/download/dotnet/8.0
REM
REM  Output: publish\PdfAutoViewer.exe
REM    - Single file, no console window
REM    - Requires .NET 8 runtime on the target machine
REM      (pre-installed on Windows 11; available via Windows Update on Win 10)
REM
REM  For a fully self-contained build (bundles the runtime, ~70 MB):
REM    Change --self-contained false  →  --self-contained true
REM ============================================================

echo [*] Building PdfAutoViewer...

dotnet publish PdfAutoViewer\PdfAutoViewer.csproj ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained false ^
    -p:PublishSingleFile=true ^
    --output publish

echo.
if exist publish\PdfAutoViewer.exe (
    echo [OK] Ready: publish\PdfAutoViewer.exe
) else (
    echo [ERROR] Build failed. Check the output above.
    exit /b 1
)

pause
