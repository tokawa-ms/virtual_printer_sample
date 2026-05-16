# Generate a small multi-page XPS file using WPF, drop it into the spool dir,
# and verify that the watcher renders it into per-page PNGs.
Add-Type -AssemblyName PresentationCore, PresentationFramework, ReachFramework, WindowsBase, System.Xaml

$outXps = 'C:\VirtualPrintDemo\.spool\spool.xps'

# Build 3 pages with text on each.
$doc = New-Object System.Windows.Documents.FixedDocument
1..3 | ForEach-Object {
    $page = New-Object System.Windows.Documents.PageContent
    $fp   = New-Object System.Windows.Documents.FixedPage
    $fp.Width  = 793   # A4 width in DIP (~210mm @ 96dpi)
    $fp.Height = 1122
    $tb = New-Object System.Windows.Controls.TextBlock
    $tb.Text = "Hello, page $_ of 3`r`nTimestamp: $(Get-Date -Format o)"
    $tb.FontSize = 48
    $tb.Margin = '60,80,60,0'
    [void]$fp.Children.Add($tb)
    $tb2 = New-Object System.Windows.Controls.TextBlock
    $tb2.Text = ('XPS rendering test ' * 5)
    $tb2.FontSize = 18
    $tb2.TextWrapping = 'Wrap'
    $tb2.Margin = '60,260,60,60'
    $tb2.Width = 670
    [void]$fp.Children.Add($tb2)
    [System.Windows.Markup.IAddChild]$page.AddChild($fp)
    [void]$doc.Pages.Add($page)
}

# Write to a temp XPS package, then atomically move into the spool path so
# the watcher sees a single change event.
$tempXps = [IO.Path]::Combine([IO.Path]::GetTempPath(), "smoke_$([Guid]::NewGuid()).xps")
$xps = [System.Windows.Xps.Packaging.XpsDocument]::new($tempXps, [IO.FileAccess]::ReadWrite)
$writer = [System.Windows.Xps.Packaging.XpsDocument]::CreateXpsDocumentWriter($xps)
$writer.Write($doc)
$xps.Close()

Write-Host "Generated $tempXps ($(([IO.FileInfo]$tempXps).Length) bytes)"
Copy-Item $tempXps $outXps -Force
Write-Host "Dropped to $outXps"

# Wait for watcher to consume and render.
$deadline = (Get-Date).AddSeconds(30)
$jobFolder = $null
do {
    Start-Sleep -Milliseconds 500
    $jobFolder = Get-ChildItem 'C:\VirtualPrintDemo' -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{8}_\d{6}_' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
} while ($jobFolder -eq $null -and (Get-Date) -lt $deadline)

if ($jobFolder) {
    Write-Host ""
    Write-Host "Rendered job folder: $($jobFolder.FullName)"
    Get-ChildItem $jobFolder.FullName | Format-Table Name, Length -AutoSize
} else {
    Write-Host "No job folder appeared within 30 seconds."
}
Write-Host ""
Write-Host "--- log tail ---"
Get-Content 'C:\VirtualPrintDemo\virtual-printer.log' -Tail 30
