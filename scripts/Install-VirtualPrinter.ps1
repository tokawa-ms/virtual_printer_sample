<#
.SYNOPSIS
  Install the virtual printer as a plain Win32 app (no MSIX, no signing).
.DESCRIPTION
  Run elevated. Steps:
    1) Publish the WPF app (if not already published).
    2) xcopy the published files to C:\Program Files\VirtualPrintDemo\.
    3) Register an HKLM\Software\Microsoft\Windows\CurrentVersion\Run entry
       that auto-starts the app in --watch mode for every user at logon.
    4) Start the watcher now (so the user does not need to log off / on).
    5) Create the printer port as a LOCAL FILE PORT pointing to the spool
       file in C:\VirtualPrintDemo\.spool\spool.xps. The Microsoft XPS Class
       Driver will spool job XPS into that file; the watcher renders it and
       moves the result.

  Because data flows through a local file port (not TCP / network), the
  spool always succeeds and no Print Workflow association is required.

.NOTES
  Also tries to remove any earlier MSIX-based version from previous attempts.
#>

[CmdletBinding()]
param(
    [string]$InstallDir  = 'C:\Program Files\VirtualPrintDemo',
    [string]$PrinterName = 'Virtual Print Demo',
    [string]$PortName    = '',
    [string]$DriverName  = 'Microsoft Print To PDF',
    [string]$OutputRoot  = 'C:\VirtualPrintDemo'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal $id
    if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must be run from an elevated PowerShell session."
    }
}
Assert-Admin

$spoolDir  = Join-Path $OutputRoot '.spool'
$spoolFile = Join-Path $spoolDir   'spool.pdf'
if (-not $PortName) { $PortName = $spoolFile }

Write-Host "==> Ensuring output / spool directories exist"
New-Item -ItemType Directory -Force -Path $OutputRoot, $spoolDir | Out-Null
# Pre-create an empty spool file so the local port can be created reliably.
if (-not (Test-Path $spoolFile)) { '' | Out-File -FilePath $spoolFile -Encoding ascii }

# ----------------------------------------------------------------
# 0) Clean up any prior MSIX-based attempt (best-effort)
# ----------------------------------------------------------------
Write-Host "==> Removing legacy MSIX install (if any)"
Get-AppxPackage -Name 'VirtualPrintDemo.Sample' -AllUsers -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue
}
foreach ($p in 'VirtualPrintDemoPort', 'Virtual Print Demo Port') {
    Remove-PrinterPort -Name $p -ErrorAction SilentlyContinue
}

# Stop any already-running watcher so we can overwrite its files.
Write-Host "==> Stopping any running watcher"
Get-Process -Name 'VirtualPrinter.App' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# ----------------------------------------------------------------
# 1) Publish (only if necessary)
# ----------------------------------------------------------------
$root        = Resolve-Path (Join-Path $scriptDir '..')
$projectFile = Join-Path $root 'src\VirtualPrinter.App\VirtualPrinter.App.csproj'

# Match the host architecture so we ship native bits on ARM64.
$arch = $env:PROCESSOR_ARCHITECTURE
if ($env:PROCESSOR_ARCHITEW6432) { $arch = $env:PROCESSOR_ARCHITEW6432 }
switch -Regex ($arch) {
    'ARM64' { $rid = 'win-arm64' }
    'AMD64' { $rid = 'win-x64'   }
    default {
        Write-Warning "Unrecognized PROCESSOR_ARCHITECTURE='$arch'; defaulting to win-x64."
        $rid = 'win-x64'
    }
}
Write-Host "==> Target runtime: $rid"
$publishDir = Join-Path $root "src\VirtualPrinter.App\bin\Release\net8.0-windows\$rid\publish"

Write-Host "==> dotnet publish ($rid)"
& dotnet publish $projectFile -c Release -r $rid --no-self-contained --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
if (-not (Test-Path (Join-Path $publishDir 'VirtualPrinter.App.exe'))) {
    throw "Publish output missing: $publishDir\VirtualPrinter.App.exe"
}

# ----------------------------------------------------------------
# 2) xcopy to Program Files
# ----------------------------------------------------------------
Write-Host "==> Installing to $InstallDir"
if (Test-Path $InstallDir) {
    Get-ChildItem $InstallDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
} else {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
}
Copy-Item (Join-Path $publishDir '*') $InstallDir -Recurse -Force

$exe = Join-Path $InstallDir 'VirtualPrinter.App.exe'
if (-not (Test-Path $exe)) { throw "Install failed: $exe missing." }

# ----------------------------------------------------------------
# 3) Auto-start watcher at every user logon
# ----------------------------------------------------------------
Write-Host "==> Registering HKLM\...\Run autostart (watcher mode)"
$runKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $runKey -Name 'VirtualPrintDemoWatcher' `
    -Value "`"$exe`" --watch" -PropertyType String -Force | Out-Null

# ----------------------------------------------------------------
# 4) Start the watcher right now so testing works without sign-out
# ----------------------------------------------------------------
Write-Host "==> Starting watcher"
Start-Process -FilePath $exe -ArgumentList '--watch' -WindowStyle Hidden

# ----------------------------------------------------------------
# 5) Printer port + driver + queue
# ----------------------------------------------------------------
Write-Host "==> Creating local file port '$PortName'"
# If the printer already exists but is bound to a different port (e.g. the
# legacy spool.xps port from an older install), remove it first so we can
# rebind cleanly to the new PDF port without conflicts.
$existing = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
if ($existing -and ($existing.PortName -ne $PortName -or $existing.DriverName -ne $DriverName)) {
    Write-Host "    -> Removing existing printer (port/driver mismatch: $($existing.PortName) / $($existing.DriverName))"
    Remove-Printer -Name $PrinterName -ErrorAction SilentlyContinue
}
# Drop the legacy XPS spool port if nothing is bound to it any more.
$legacyXpsPort = Join-Path $spoolDir 'spool.xps'
if ($PortName -ne $legacyXpsPort -and (Get-PrinterPort -Name $legacyXpsPort -ErrorAction SilentlyContinue)) {
    if (-not (Get-Printer | Where-Object { $_.PortName -eq $legacyXpsPort })) {
        Write-Host "    -> Removing obsolete port '$legacyXpsPort'"
        Remove-PrinterPort -Name $legacyXpsPort -ErrorAction SilentlyContinue
    }
}
if (-not (Get-PrinterPort -Name $PortName -ErrorAction SilentlyContinue)) {
    Add-PrinterPort -Name $PortName
}

Write-Host "==> Ensuring driver '$DriverName' is installed"
if (-not (Get-PrinterDriver -Name $DriverName -ErrorAction SilentlyContinue)) {
    Add-PrinterDriver -Name $DriverName
}

Write-Host "==> Creating printer '$PrinterName'"
if (Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue) {
    Set-Printer -Name $PrinterName -PortName $PortName -DriverName $DriverName
} else {
    Add-Printer -Name $PrinterName -DriverName $DriverName -PortName $PortName
}

# Force color mode on the printer's default PrintTicket. Microsoft Print To
# PDF advertises PageOutputColor and respects this default, so silent prints
# (and clients like Chrome that mirror the driver capabilities) will produce
# color output.
Write-Host "==> Setting default color mode = Color"
try {
    Set-PrintConfiguration -PrinterName $PrinterName -Color $true
} catch {
    Write-Warning "Set-PrintConfiguration -Color failed: $($_.Exception.Message)"
}

# Echo what actually stuck so we can see whether the driver honors the setting.
try {
    $cfg = Get-PrintConfiguration -PrinterName $PrinterName
    Write-Host ("    -> Effective config: Color={0}, DuplexingMode={1}, PaperSize={2}" -f `
        $cfg.Color, $cfg.DuplexingMode, $cfg.PaperSize)
} catch {
    Write-Warning "Get-PrintConfiguration failed: $($_.Exception.Message)"
}

# Dump the printer's PrintCapabilities XML to see whether the driver actually
# advertises a PageOutputColor feature. If it does not, Chrome / Edge will
# pre-rasterize to grayscale before spooling, regardless of any DEVMODE
# default we set above.
Write-Host "==> Inspecting PrintCapabilities"
try {
    Add-Type -AssemblyName System.Printing -ErrorAction Stop
    $server = New-Object System.Printing.LocalPrintServer
    $queue  = $server.GetPrintQueue($PrinterName)
    $capsStream = $queue.GetPrintCapabilitiesAsXml()
    $reader = New-Object System.IO.StreamReader($capsStream)
    $capsXml = $reader.ReadToEnd()
    $reader.Dispose()
    $capsStream.Dispose()

    $capsPath = Join-Path $OutputRoot 'printer-capabilities.xml'
    [System.IO.File]::WriteAllText($capsPath, $capsXml, [System.Text.Encoding]::UTF8)

    $hasFeature = $capsXml -match 'PageOutputColor'
    $hasColorOption = $capsXml -match 'psk:Color'
    Write-Host ("    -> PageOutputColor feature in capabilities : {0}" -f $hasFeature)
    Write-Host ("    -> psk:Color option in capabilities        : {0}" -f $hasColorOption)
    Write-Host ("    -> Full PrintCapabilities saved to         : {0}" -f $capsPath)
} catch {
    Write-Warning "PrintCapabilities inspection failed: $($_.Exception.Message)"
}

# ----------------------------------------------------------------
# 6) (Start Menu shortcut is intentionally NOT created — the watcher
#     runs headless. Launch the EXE directly to view the log / output.)
# ----------------------------------------------------------------

Write-Host ''
Write-Host "Done."
Write-Host "  Printer   : $PrinterName"
Write-Host "  Port      : $PortName"
Write-Host "  Watcher   : $exe --watch  (auto-starts via Run key)"
Write-Host "  UI window : run '$exe' for the log/output viewer"
Write-Host "  Output    : $OutputRoot\<timestamp>_<jobname>\page_NNN.png (color)"
Write-Host "             (the source PDF is preserved as print.pdf in the same folder)"
Write-Host "  Log file  : $OutputRoot\virtual-printer.log"
