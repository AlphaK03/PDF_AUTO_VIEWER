@echo off
REM ============================================================
REM  Philips Document Flow (PDF) (.NET 8) — Build script
REM ============================================================
REM  Prerequisite: .NET 8 SDK
REM    https://dotnet.microsoft.com/download/dotnet/8.0
REM
REM  Output: publish-light\PdfAutoViewer.exe
REM    - Single ~1.8 MB file (app + WebView2 DLLs + native loader embedded)
REM    - No console window
REM    - Does NOT bundle .NET: requires the .NET 8 Desktop Runtime on the
REM      target machine (pre-installed on Windows 11; available via Windows
REM      Update on Windows 10). The WebView2 Runtime must also be present.
REM
REM  For a fully self-contained build (bundles the runtime, ~80 MB, no .NET
REM  needed on the target): change --self-contained false  →  --self-contained true
REM ============================================================

echo [*] Building PdfAutoViewer (lightweight single file)...

dotnet publish PdfAutoViewer\PdfAutoViewer.csproj ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    --output publish-light

echo.
if exist publish-light\PdfAutoViewer.exe (
    echo [OK] Ready: publish-light\PdfAutoViewer.exe
    echo      The .xml / .pdb files next to it are not needed at runtime.
) else (
    echo [ERROR] Build failed. Check the output above.
    exit /b 1
)

pause
