# Philips Document Flow (PDF)

> *Pop · Display · Flush* — the document appears, you read it, and it's gone.

Windows desktop application that watches the user's Downloads folder and
automatically opens downloaded work instructions (WI / MPI) in PDF format using
a built-in viewer. When the document is closed it is deleted automatically to
keep the folder clean.

Designed for manufacturing environments where documents may be distributed in
two languages, identified by a suffix in the file name (`_SPA`, `_ENG`).

## Behavior

- PDFs always open in the **built-in viewer** (WebView2 / Chromium engine).
- PDFs are **deleted automatically** when the document is closed.
- **20-minute viewing limit**: a warning is shown at 15 minutes and the document
  closes by itself at 20.
- When both languages are downloaded at once, the **preferred language** opens;
  if only one arrives, that one opens. The preferred language is the only
  configurable option and is selected from the main window.
- If a **newer copy** of the open document is downloaded, it replaces the open
  version and the viewing-time limit restarts.

## Continuous operation

Designed to run unattended (24/7):

- **Single instance per Windows session**: prevents duplicate copies within the
  same session without blocking other sessions (VDI / multi-session hosts).
- **Error logging**: unhandled exceptions are written to
  `%LOCALAPPDATA%\PdfAutoViewer\app-error.log`; a transient error does not stop
  the application.
- Bounded memory use during long-running operation.

## Requirements

- Windows 10 / 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- WebView2 Runtime (preinstalled by default on Windows 11)

## Run the application

> Run the commands from the solution root (the folder that contains
> `PdfAutoViewer.sln`).

```bash
dotnet run --project PdfAutoViewer/PdfAutoViewer.csproj
```

### Running from VS Code (local SDK, no admin)

On corporate machines without administrator rights, the .NET 8 SDK can be
installed per-user (under `%USERPROFILE%\.dotnet`) and called directly, without
relying on a system-wide `dotnet` on the `PATH`. Use the VS Code integrated
terminal (PowerShell), from the project root:

```powershell
# Run the application
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --project PdfAutoViewer

# Run the tests
& "$env:USERPROFILE\.dotnet\dotnet.exe" test PdfAutoViewer.Tests

# Build the executable (.exe)
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish PdfAutoViewer/PdfAutoViewer.csproj -c Release -r win-x64 --self-contained false -o publish
```

Once `%USERPROFILE%\.dotnet` is on the user `PATH`, reopen VS Code and the short
form works in any new terminal:

```powershell
dotnet run  --project PdfAutoViewer
dotnet test PdfAutoViewer.Tests
```

## Build the executable (.exe)

```bash
dotnet publish PdfAutoViewer/PdfAutoViewer.csproj -c Release -r win-x64 --self-contained false -o publish
```

The executable is produced at `publish\PdfAutoViewer.exe` (requires the .NET 8
runtime on the target machine). The `publish.bat` script runs this same command.

The solution `PdfAutoViewer.sln` can also be opened directly in Visual Studio
2022 or JetBrains Rider.

## Tests

Unit tests (xUnit) cover the deterministic business logic: language detection,
document pairing, copy identity and configuration defaults.

```bash
dotnet test
```

## Deployment notes (Windows 10)

The built-in viewer depends on the **WebView2 Runtime**. It is preinstalled on
Windows 11, but **not guaranteed on Windows 10**; since the application has no
alternative viewer, the document will not open if the runtime is missing. Before
deploying to Windows 10 terminals:

- **Verify the WebView2 Runtime is installed**; if not, install it (Evergreen
  Standalone Installer) or bundle the *Fixed Version* runtime with the app so it
  does not depend on the system.
- **Publish self-contained** (include the .NET runtime in the executable) to
  avoid depending on .NET 8 being installed. Requires Windows 10 version 1607 or
  later.
- Note that **Windows 10 reached end of support in October 2025**; confirm the
  update plan (ESU or migration to Windows 11).

## Project structure

```
PdfAutoViewer.sln              Visual Studio solution
PdfAutoViewer/
  Program.cs                   Entry point
  PdfAutoViewer.csproj         Project (.NET 8, Windows Forms)
  Core/                        Logic: settings, monitoring, lifecycle
  UI/                          Interface: tray, status window, viewer
PdfAutoViewer.Tests/           Unit tests (xUnit)
```

## Configuration

The only configurable option is the **preferred language**, selected from the
application's main window and saved to
`%LOCALAPPDATA%\PdfAutoViewer\settings.json`. Everything else (watched folder,
built-in viewer, auto-deletion, 20-minute limit) is fixed by design.

## Dependencies

| Dependency | Purpose |
|---|---|
| .NET 8 / Windows Forms | Platform and interface |
| `Microsoft.Web.WebView2` | Built-in viewer (Chromium PDF engine) |
