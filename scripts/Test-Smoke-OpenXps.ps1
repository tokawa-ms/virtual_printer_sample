# Smoke test: generate a 2-page XPS, convert it to OpenXPS form, drop it into
# the spool, and verify the watcher's OpenXPS normalizer kicks in and produces
# per-page PNGs.
Add-Type -AssemblyName PresentationCore, PresentationFramework, ReachFramework, WindowsBase, System.IO.Compression, System.IO.Compression.FileSystem

# --- 1. Build XPS via WPF -------------------------------------------------
$doc = New-Object System.Windows.Documents.FixedDocument
1..2 | ForEach-Object {
    $page = New-Object System.Windows.Documents.PageContent
    $fp = New-Object System.Windows.Documents.FixedPage
    $fp.Width = 793; $fp.Height = 1122
    $tb = New-Object System.Windows.Controls.TextBlock
    $tb.Text = "OpenXPS smoke - page $_"
    $tb.FontSize = 48; $tb.Margin = '60,80,60,0'
    [void]$fp.Children.Add($tb)
    [System.Windows.Markup.IAddChild]$page.AddChild($fp)
    [void]$doc.Pages.Add($page)
}
$tempXps = [IO.Path]::Combine([IO.Path]::GetTempPath(), "oxps_src_$([Guid]::NewGuid()).xps")
$xpsPkg  = [System.Windows.Xps.Packaging.XpsDocument]::new($tempXps, [IO.FileAccess]::ReadWrite)
[System.Windows.Xps.Packaging.XpsDocument]::CreateXpsDocumentWriter($xpsPkg).Write($doc)
$xpsPkg.Close()

# --- 2. Rewrite XPS -> OpenXPS by inverting the renderer's substitutions ---
$subs = @(
    @{ from='http://schemas.microsoft.com/xps/2005/06/fixedrepresentation'; to='http://schemas.openxps.org/oxps/v1.0/fixedrepresentation' }
    @{ from='http://schemas.microsoft.com/xps/2005/06/required-resource';   to='http://schemas.openxps.org/oxps/v1.0/required-resource' }
    @{ from='http://schemas.microsoft.com/xps/2005/06/printticket';         to='http://schemas.openxps.org/oxps/v1.0/printticket' }
    @{ from='http://schemas.microsoft.com/xps/2005/06/restricted-font';     to='http://schemas.openxps.org/oxps/v1.0/restricted-font' }
    @{ from='http://schemas.microsoft.com/xps/2005/06/discard-control';     to='http://schemas.openxps.org/oxps/v1.0/discard-control' }
    @{ from='http://schemas.microsoft.com/xps/2005/06';                     to='http://schemas.openxps.org/oxps/v1.0' }
    @{ from='application/vnd.ms-package.xps-';                              to='application/vnd.ms-package.oxps-' }
)
$tempOxps = [IO.Path]::ChangeExtension($tempXps, '.oxps')
Copy-Item $tempXps $tempOxps -Force

$zip = [System.IO.Compression.ZipFile]::Open($tempOxps, [System.IO.Compression.ZipArchiveMode]::Update)
foreach ($entry in @($zip.Entries)) {
    $name = $entry.FullName
    $ext  = [IO.Path]::GetExtension($name).ToLowerInvariant()
    $isText = $ext -in '.xml','.rels','.fdseq','.fdoc','.fpage','.dict','.xaml' -or $name -eq '[Content_Types].xml'
    if (-not $isText) { continue }
    $sr = New-Object System.IO.StreamReader($entry.Open())
    $text = $sr.ReadToEnd(); $sr.Dispose()
    $orig = $text
    foreach ($s in $subs) { $text = $text.Replace($s.from, $s.to) }
    if ($text -eq $orig) { continue }
    $ws = $entry.Open(); $ws.SetLength(0)
    $sw = New-Object System.IO.StreamWriter($ws, (New-Object System.Text.UTF8Encoding($false)))
    $sw.Write($text); $sw.Dispose()
}
$zip.Dispose()
Write-Host "Generated OpenXPS package: $tempOxps ($(([IO.FileInfo]$tempOxps).Length) bytes)"

# --- 3. Drop into spool and wait for the watcher --------------------------
$dropTime = Get-Date
$spool = 'C:\VirtualPrintDemo\.spool\spool.xps'
Copy-Item $tempOxps $spool -Force
Write-Host "Dropped to $spool"

$deadline = $dropTime.AddSeconds(30)
$jobFolder = $null
do {
    Start-Sleep -Milliseconds 500
    $jobFolder = Get-ChildItem 'C:\VirtualPrintDemo' -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{8}_\d{6}_' -and $_.LastWriteTime -ge $dropTime } |
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
Write-Host "--- log tail ---"
Get-Content 'C:\VirtualPrintDemo\virtual-printer.log' -Tail 20
