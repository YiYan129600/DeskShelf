# PecoFence 脚本栏。由 PecoFence 定时运行，每行的 open 参数交给 Windows 打开。
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
if (-not $desktop) {
    '无法读取当前用户的桌面。'
    return
}

$driveRoot = [IO.Path]::GetPathRoot($desktop)
if ($driveRoot -notmatch '^[A-Za-z]:\\$' -or [IO.DriveInfo]::new($driveRoot).DriveType -notin @('Fixed', 'Removable')) {
    '最近文档仅显示本机磁盘上的桌面文件。'
    return
}

$extensions = '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx', '.pdf', '.txt', '.md', '.rtf',
              '.odt', '.ods', '.odp', '.csv', '.tsv'
$now = [DateTime]::UtcNow
$since = $now.AddDays(-7)
try {
    $recent = Get-ChildItem -LiteralPath $desktop -File -ErrorAction Stop | Where-Object {
        $_.Extension -in $extensions -and $_.LastWriteTimeUtc -ge $since -and $_.LastWriteTimeUtc -le $now -and
        -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        $_.FullName -notmatch '[|"\p{Cc}\p{Zl}\p{Zp}]'
    } | Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, Name |
        Select-Object -First 30
}
catch {
    '无法读取当前用户的桌面。'
    return
}

$bytes = 0
foreach ($file in $recent) {
    $name = $file.Name -replace '\|', '｜' -replace '["'']', ([string][char]0x2019) -replace '[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]', ' '
    $date = $file.LastWriteTime.ToString('MM-dd HH:mm')
    $open = ' | open="' + $file.FullName + '"'
    # 给完整的打开路径留足空间，避免 PecoFence 的 300 字符上限截断它。
    $labelLimit = 300 - $date.Length - 2 - $open.Length
    if ($labelLimit -lt 2) { continue }
    if ($name.Length -gt $labelLimit) {
        $keep = $labelLimit - 1
        if ([char]::IsHighSurrogate($name[$keep - 1])) { $keep-- }
        $name = $name.Substring(0, $keep) + '…'
    }
    $row = "$date  $name$open"
    $rowBytes = [Text.Encoding]::UTF8.GetByteCount($row) + 2
    if ($bytes + $rowBytes -gt 16000) { break }
    $bytes += $rowBytes
    $row
}
if ($bytes -eq 0) { '桌面顶层暂无最近 7 天的文档。' }
