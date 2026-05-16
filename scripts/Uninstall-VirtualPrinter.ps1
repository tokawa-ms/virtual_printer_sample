<#
.SYNOPSIS
  Reverse Install-VirtualPrinter.ps1: removes the printer, port, watcher
  autostart, and the C:\Program Files\VirtualPrintDemo directory. Also
  removes any leftover MSIX from an earlier prototype.
#>

[CmdletBinding()]
param(
    [string]$InstallDir  = 'C:\Program Files\VirtualPrintDemo',
    [string]$PrinterName = 'Virtual Print Demo',
    [string]$OutputRoot  = 'C:\VirtualPrintDemo',
    [string]$CertSubject = 'CN=VirtualPrintDemoDev'
)

$ErrorActionPreference = 'Continue'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal $id
    if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run elevated."
    }
}
Assert-Admin

$spoolFile = Join-Path $OutputRoot '.spool\spool.xps'

Write-Host "==> Stopping watcher"
Get-Process -Name 'VirtualPrinter.App' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "==> Removing printer '$PrinterName' and its port"
$printer = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
if ($printer) {
    $portName = $printer.PortName
    Remove-Printer -Name $PrinterName -ErrorAction SilentlyContinue
    if ($portName) {
        Remove-PrinterPort -Name $portName -ErrorAction SilentlyContinue
    }
}
# Stale ports left by earlier prototypes
foreach ($p in 'VirtualPrintDemoPort', $spoolFile) {
    Remove-PrinterPort -Name $p -ErrorAction SilentlyContinue
}

Write-Host "==> Removing Run autostart"
$runKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'VirtualPrintDemoWatcher' -ErrorAction SilentlyContinue

Write-Host "==> Removing $InstallDir"
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Clean up earlier MSIX-prototype artifacts if still present
Write-Host "==> Removing legacy MSIX (if present)"
Get-AppxPackage -Name 'VirtualPrintDemo.Sample' -AllUsers -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue
}
foreach ($store in 'Cert:\LocalMachine\Root', 'Cert:\LocalMachine\TrustedPeople', 'Cert:\CurrentUser\My') {
    Get-ChildItem $store -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $CertSubject } |
        ForEach-Object {
            Write-Host "    removing cert $($_.Thumbprint) from $store"
            Remove-Item $_.PSPath -Force -ErrorAction SilentlyContinue
        }
}

Write-Host ''
Write-Host "Uninstall complete. Output files under $OutputRoot were left intact."
