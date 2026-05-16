# Generate a small multi-page color PDF using WPF + XpsDocumentWriter
# targeting the installed "Virtual Print Demo" printer, then verify that the
# watcher rasterizes it into per-page color PNGs.
#
# This intentionally uses WriteAsync against the real PrintQueue (not a file
# port) so the entire Microsoft Print To PDF pipeline is exercised, including
# the PrintCapabilities color negotiation. The driver writes its PDF spool
# into C:\VirtualPrintDemo\.spool\spool.pdf via the local file port created
# by Install-VirtualPrinter.ps1, and the watcher picks it up from there.

param(
    [string]$PrinterName = 'Virtual Print Demo',
    [string]$OutputRoot  = 'C:\VirtualPrintDemo'
)

Add-Type -AssemblyName PresentationCore, PresentationFramework, ReachFramework, WindowsBase, System.Xaml, System.Printing

function New-ColorPage([int]$pageNumber, [int]$total) {
    $fp = New-Object System.Windows.Documents.FixedPage
    $fp.Width  = 793   # A4 width in DIP (~210mm @ 96dpi)
    $fp.Height = 1122

    # Bright background swatches so we can visually confirm color output.
    $colors = @(
        [System.Windows.Media.Colors]::Tomato,
        [System.Windows.Media.Colors]::SteelBlue,
        [System.Windows.Media.Colors]::MediumSeaGreen
    )
    $bg = New-Object System.Windows.Shapes.Rectangle
    $bg.Width  = $fp.Width
    $bg.Height = 200
    $bg.Fill   = New-Object System.Windows.Media.SolidColorBrush ($colors[($pageNumber - 1) % $colors.Length])
    [System.Windows.Controls.Canvas]::SetLeft($bg, 0)
    [System.Windows.Controls.Canvas]::SetTop($bg, 0)
    [void]$fp.Children.Add($bg)

    $tb = New-Object System.Windows.Controls.TextBlock
    $tb.Text = "Hello, page $pageNumber of $total`r`nTimestamp: $(Get-Date -Format o)"
    $tb.FontSize = 48
    $tb.Margin = '60,260,60,0'
    $tb.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Colors]::Black)
    [void]$fp.Children.Add($tb)

    $tb2 = New-Object System.Windows.Controls.TextBlock
    $tb2.Text = ('Color PDF rendering test ' * 5)
    $tb2.FontSize = 18
    $tb2.TextWrapping = 'Wrap'
    $tb2.Margin = '60,440,60,60'
    $tb2.Width = 670
    $tb2.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Colors]::DarkBlue)
    [void]$fp.Children.Add($tb2)

    $page = New-Object System.Windows.Documents.PageContent
    [System.Windows.Markup.IAddChild]$page.AddChild($fp)
    return $page
}

# Build a 3-page document with colored backgrounds.
$doc = New-Object System.Windows.Documents.FixedDocument
1..3 | ForEach-Object { [void]$doc.Pages.Add((New-ColorPage $_ 3)) }

# Submit to the print queue (this goes through the driver and produces a PDF
# in the local file port spool).
$server = New-Object System.Printing.LocalPrintServer
$queue  = $server.GetPrintQueue($PrinterName)
$writer = [System.Windows.Xps.Packaging.XpsDocument]::CreateXpsDocumentWriter($queue)
Write-Host "Submitting 3-page color test to '$PrinterName'..."
$writer.Write($doc)
Write-Host "Submitted. Waiting for watcher to render..."

# Wait for the watcher to create a new job folder containing PNGs.
$deadline = (Get-Date).AddSeconds(45)
$startTime = Get-Date
$jobFolder = $null
do {
    Start-Sleep -Milliseconds 500
    $jobFolder = Get-ChildItem $OutputRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{8}_\d{6}_' -and $_.LastWriteTime -gt $startTime.AddSeconds(-2) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
} while (($null -eq $jobFolder -or -not (Get-ChildItem $jobFolder.FullName -Filter 'page_*.png' -ErrorAction SilentlyContinue)) `
         -and (Get-Date) -lt $deadline)

if ($jobFolder) {
    Write-Host ""
    Write-Host "Rendered job folder: $($jobFolder.FullName)"
    Get-ChildItem $jobFolder.FullName | Format-Table Name, Length -AutoSize
} else {
    Write-Host "No job folder appeared within 45 seconds."
}
Write-Host ""
Write-Host "--- log tail ---"
Get-Content (Join-Path $OutputRoot 'virtual-printer.log') -Tail 30
