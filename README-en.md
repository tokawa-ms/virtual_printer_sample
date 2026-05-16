# Virtual Print Demo

[English README (this file)](README-en.md) · [日本語 README](README.md)

A sample **virtual printer for Windows 10 / 11** (x64 and **ARM64**, both native).
Choose "Virtual Print Demo" from the standard Windows print dialog and the job
will be saved as one **color PNG per page** under
`C:\VirtualPrintDemo\<timestamp>_PrintJob\page_NNN.png` (the source PDF is
preserved as `print.pdf` in the same folder).

- Language: **C# / WPF / .NET 8**
- Required driver: only the in-box **Microsoft Print To PDF**
- PDF → PNG rasterizer: **PDFium** (via the `PDFtoImage` NuGet) for native color output
- No MSIX packaging, no code-signing certificate
- Verified on Windows 11 24H2 (x64) with .NET 8 Desktop Runtime

> **License:** [MIT](LICENSE) © 2026 tokawa-ms

> **Why not Microsoft XPS Class Driver?** The XPS Class Driver does **not** advertise the `PageOutputColor` print capability, so applications such as Chrome / Edge / PowerPoint pre-rasterize every job to grayscale **before spooling**. No amount of post-processing can recover the original color. Microsoft Print To PDF, by contrast, does advertise `PageOutputColor`, accepts color jobs, and emits a color PDF — which PDFium then turns into a color PNG.

---

## Table of contents

1. [Architecture overview](#architecture-overview)
2. [Supported platforms](#supported-platforms)
3. [Prerequisites](#prerequisites)
4. [Installation](#installation)
   - [Install on x64 Windows](#install-on-x64-windows)
   - [Install on ARM64 Windows](#install-on-arm64-windows)
5. [Verifying the install](#verifying-the-install)
6. [Uninstallation](#uninstallation)
7. [Upgrading (re-install)](#upgrading-re-install)
8. [Troubleshooting](#troubleshooting)
9. [Repository layout](#repository-layout)
10. [Further documentation](#further-documentation)
11. [License](#license)

---

## Architecture overview

```
 ┌─────────────────────┐                                                ┌────────────────────┐
 │ Any Windows app     │  ─print─▶  Microsoft Print To PDF  ─PDF─▶ │ Local file port    │
 │ (Notepad / Edge..)  │            (PageOutputColor = Color)           │ C:\VirtualPrintDemo │
 └─────────────────────┘                                                │  \.spool\spool.pdf │
                                                                        └────────┬───────────┘
                                                                                 │
                                                                FileSystemWatcher fires
                                                                                 │
                                                                                 ▼
                                      ┌──────────────────────────────────────────────────────────┐
                                      │ VirtualPrinter.App.exe --watch (resident, HKLM\Run)        │
                                      │   1. Wait for a complete PDF ("%PDF-" header + "%%EOF")    │
                                      │   2. Move into the per-job folder as print.pdf             │
                                      │   3. Rasterize with PDFium (PDFtoImage) at 300 DPI, color  │
                                      └──────────────────────────────────────────────────────────┘
                                                                                 │
                                                                                 ▼
                                       C:\VirtualPrintDemo\<timestamp>_PrintJob\page_NNN.png (color)
```

| Layer | Component | Responsibility |
|---|---|---|
| Print queue | `Microsoft Print To PDF` + local file port | Appears in the print dialog and spools a color PDF to a file |
| Resident service | `VirtualPrinter.App.exe --watch` (WPF / .NET 8) | Watches the spool file, converts the PDF to color PNGs |
| Rasterizer | PDFium via `PDFtoImage` 5.x NuGet | Renders each page into a 300 DPI color `SKBitmap` |
| Output | `C:\VirtualPrintDemo\<timestamp>_PrintJob\page_NNN.png` | One 300 DPI **color** PNG per page (plus `print.pdf`) |

The "resident service" is not a Windows service; it is a windowless process registered under `HKLM\…\Run` that starts when any user logs on. See [docs/architecture.md](docs/architecture.md) for details.

---

## Supported platforms

| Item | Value |
|---|---|
| OS | Windows 10 1809+ / Windows 11 |
| CPU | **Native x64** and **Native ARM64** |
| .NET | .NET 8 Desktop Runtime (matching the host CPU) |
| Privilege | Administrator is required **only** for install / uninstall |

The installer reads `PROCESSOR_ARCHITECTURE` and automatically chooses between `win-x64` and `win-arm64` when publishing.

---

## Prerequisites

| Software | Used for | Source |
|---|---|---|
| .NET 8 SDK | Building from source | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| .NET 8 Desktop Runtime | Running pre-built binaries (matching CPU) | Same |
| Windows PowerShell 5.1 or PowerShell 7+ | Install / uninstall scripts | Ships with Windows |

The `Microsoft Print To PDF` driver ships with Windows 10 and later, so nothing else needs to be installed. The installer registers it with `Add-PrinterDriver` if necessary.

---

## Installation

> Open PowerShell **as Administrator** before running any install command.

### Install on x64 Windows

```powershell
# 1) Get the source
git clone https://github.com/tokawa-ms/virtual_printer_sample.git
cd virtual_printer_sample

# 2) Build (optional — the installer will run `dotnet publish` for you)
dotnet publish src\VirtualPrinter.App -c Release -r win-x64 --no-self-contained

# 3) Install (Administrator PowerShell)
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

The installer:

1. Stops/removes any existing "Virtual Print Demo" printer, port, or watcher process
2. Runs `dotnet publish -r win-x64 --no-self-contained` if needed
3. Copies the publish output to **`C:\Program Files\VirtualPrintDemo\`**
4. Registers `HKLM\Software\Microsoft\Windows\CurrentVersion\Run\VirtualPrintDemoWatcher` so the watcher (`VirtualPrinter.App.exe --watch`) auto-starts at every user logon
5. Starts the watcher immediately (no re-logon needed before testing)
6. Creates the local file port `C:\VirtualPrintDemo\.spool\spool.pdf`
7. Creates the **Virtual Print Demo** printer using the `Microsoft Print To PDF` driver and forces its default `PrintConfiguration` to color

### Install on ARM64 Windows

The steps are identical:

```powershell
git clone https://github.com/tokawa-ms/virtual_printer_sample.git
cd virtual_printer_sample
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

Internally the script will:

- Detect `PROCESSOR_ARCHITECTURE = ARM64`
- Run `dotnet publish -r win-arm64 --no-self-contained` to produce native ARM64 binaries
- Copy the resulting `bin\Release\net8.0-windows\win-arm64\publish\` into `C:\Program Files\VirtualPrintDemo\`

You should see `==> Target runtime: win-arm64` in the install output.

#### Cross-build from an x64 development box

You can publish ARM64 binaries from an x64 dev machine and copy them to the ARM64 target:

```powershell
# On the x64 dev box
dotnet publish src\VirtualPrinter.App -c Release -r win-arm64 --no-self-contained
# Copy src\VirtualPrinter.App\bin\Release\net8.0-windows\win-arm64\publish\ to the ARM64 machine
# On the ARM64 machine (Administrator PowerShell)
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

> Windows on ARM can run x64 binaries through emulation, but using a **native ARM64 build is strongly recommended** for startup and render performance.

---

## Verifying the install

1. Open any application (Notepad, Edge, Word, PowerPoint, …)
2. `File → Print` and select **Virtual Print Demo**
3. Check the output folder:
   - `C:\VirtualPrintDemo\<timestamp>_PrintJob\page_001.png`, `page_002.png`, …
4. Optional: look at the log at
   - `C:\VirtualPrintDemo\virtual-printer.log`

### Smoke test (optional)

A script submits a 3-page color document through the real print queue and verifies that the watcher produces color PNGs:

```powershell
# Prints a 3-page color document to Virtual Print Demo and waits for page_001..003.png (color)
powershell -ExecutionPolicy Bypass -File scripts\Test-Smoke.ps1
```

---

## Uninstallation

Same procedure on x64 and ARM64. From an **Administrator PowerShell**:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Uninstall-VirtualPrinter.ps1
```

The script:

1. Stops the resident watcher process (`VirtualPrinter.App.exe`)
2. Removes the `Virtual Print Demo` printer
3. Removes the local file port
4. Removes the `HKLM\…\Run\VirtualPrintDemoWatcher` registry value
5. Deletes `C:\Program Files\VirtualPrintDemo\`

> **Preserved**: the previously generated PNGs and log under `C:\VirtualPrintDemo\<timestamp>_PrintJob\` are kept. Run `Remove-Item C:\VirtualPrintDemo -Recurse -Force` manually if you want a fully clean state.

---

## Upgrading (re-install)

To swap in new binaries, uninstall first, then install again:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Uninstall-VirtualPrinter.ps1
git pull
dotnet publish src\VirtualPrinter.App -c Release -r win-x64   --no-self-contained   # or -r win-arm64
powershell -ExecutionPolicy Bypass -File scripts\Install-VirtualPrinter.ps1
```

---

## Troubleshooting

| Symptom | What to do |
|---|---|
| Print succeeds but no PNGs appear | Check `C:\VirtualPrintDemo\virtual-printer.log`. Failed PDF payloads are kept under `C:\VirtualPrintDemo\.failed\` for inspection |
| Printer does not appear in the print dialog | Re-run `Install-VirtualPrinter.ps1` as Administrator. If `==> Target runtime: ...` still shows `win-x64` on an ARM64 machine, delete the stale publish output and re-run |
| `never became a complete PDF` in the log | No complete PDF (header `%PDF-` + trailer `%%EOF`) arrived within 30 s. The watcher auto-deletes the partial file and waits for the next job, so re-printing usually recovers |
| PNGs come out grayscale | A stale queue bound to the old XPS Class Driver may be lingering. Run `Uninstall-VirtualPrinter.ps1` → `Install-VirtualPrinter.ps1` to rebuild it, and confirm `Get-PrintConfiguration -PrinterName 'Virtual Print Demo'` reports `Color=True` |
| `Another watcher instance is already running` | The watcher is already resident. The installer stops it automatically on re-install, so this message is benign |

See [docs/troubleshooting.md](docs/troubleshooting.md) for the detailed diagnostic playbook.

---

## Repository layout

```
virtual_printer_sample/
├── VirtualPrinter.sln
├── src/VirtualPrinter.App/                WPF (.NET 8) project
│   ├── App.xaml(.cs)                       Startup and argument dispatch
│   ├── MainWindow.xaml(.cs)                Minimal management UI
│   ├── Logger.cs                           Append-only file logger
│   ├── Workflow/SpoolWatcher.cs            FileSystemWatcher + PDF completion detection
│   ├── Rendering/PdfToPngRenderer.cs       PDF → color PNG (300 DPI, PDFium)
│   └── Assets/                             Placeholder icons
├── scripts/
│   ├── Install-VirtualPrinter.ps1          Administrator installer
│   ├── Uninstall-VirtualPrinter.ps1        Administrator uninstaller
│   ├── Generate-Assets.ps1                 Placeholder icon generator
│   └── Test-Smoke.ps1                      End-to-end smoke test (real print queue → color PNGs)
├── docs/                                  In-depth documentation (see below)
├── LICENSE                                MIT
├── README.md                              Japanese README
└── README-en.md                           This file
```

---

## Further documentation

| Document | Contents |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Detailed architecture, per-component responsibilities, run modes |
| [docs/pdf-pipeline.md](docs/pdf-pipeline.md) | **Current**: deep dive into the Microsoft Print To PDF + PDFium color-PNG pipeline |
| [docs/design-history.md](docs/design-history.md) | All previously-considered approaches and why each was adopted or rejected, including the XPS-Class-Driver → Print-To-PDF migration for color support |
| [docs/xps-internals.md](docs/xps-internals.md) | **Historical archive**: OpenXPS / OPC piece-streaming handling that was required by the legacy XPS pipeline (v1.x line) |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Detailed troubleshooting and diagnostics |

---

## License

[MIT License](LICENSE) © 2026 **tokawa-ms**
