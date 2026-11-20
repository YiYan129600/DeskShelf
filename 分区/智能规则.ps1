#requires -Version 5.1
<#
.SYNOPSIS
预览或安装桌屿的 PecoFence 0.4.0 自动归类规则。
.DESCRIPTION
默认预览拟安装的规则。-Apply 安装后自动归类今后进入桌面的项目。
-PreviewExisting 调用原生只读预览，显示当前已安装规则对现有图标的影响。
-UseStarterLayout 用于复用原生初始布局时，停用可识别的三条预设规则。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Apply,
    [switch]$PreviewExisting,
    [switch]$UseStarterLayout,
    [string]$CliPath,
    [ValidateNotNullOrEmpty()][string]$Instance
)

$ErrorActionPreference = 'Stop'
if (-not $CliPath) { $CliPath = Join-Path $PSScriptRoot 'PecoFence\pecofence-cli.exe' }
if (-not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
    throw "找不到 PecoFence CLI：$CliPath"
}
$CliPath = (Get-Item -LiteralPath $CliPath).FullName
$instanceFlags = @()
if ($Instance) { $instanceFlags = @('--instance', $Instance) }

function Invoke-PecoFence {
    param([string[]]$ArgumentList)

    $ErrorActionPreference = 'Continue'
    $output = & $CliPath --ascii --compact --timeout 8000 @instanceFlags @ArgumentList 2>&1
    $exitCode = $LASTEXITCODE
    $message = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        $reason = $null
        try { $reason = (ConvertFrom-Json -InputObject $message -ErrorAction Stop).error.message } catch {}
        if (-not $reason) { $reason = $message -split '\r?\n' | Select-Object -First 1 }
        throw "PecoFence $($ArgumentList[0]) $($ArgumentList[1]) 失败（退出码 $exitCode）。$reason"
    }
    $decoded = ConvertFrom-Json -InputObject $message -ErrorAction Stop
    $decoded
}

function Copy-NativeRule {
    param($Rule)

    $copy = [ordered]@{
        allOf = @($Rule.allOf)
        enabled = $Rule.enabled
        id = $Rule.id
        name = $Rule.name
        priorityClass = $Rule.priorityClass
        target = $Rule.target
    }
    if ($null -ne $Rule.template) {
        $copy.template = $Rule.template
    }
    $copy
}

function New-DeskShelfRule {
    param([int]$Number, [string]$Name, [string]$Class, [string]$FenceId, [object[]]$Conditions)

    [ordered]@{
        allOf = $Conditions
        enabled = $true
        id = ('4b9cc82e-41dc-5a9a-ab9c-ada074{0:d6}' -f $Number)
        name = "桌屿｜$Name"
        priorityClass = $Class
        target = [ordered]@{ fence = $FenceId }
    }
}

if ($PreviewExisting -and -not $Apply) {
    Invoke-PecoFence @('rule', 'apply', '--dry-run')
    return
}

$fences = @(Invoke-PecoFence @('fence', 'list', '--fields', 'id,title,kind,tool,smart'))
$target = @{}
foreach ($title in @('项目资料', '图片影音', '工具入口', '游戏')) {
    $match = @($fences | Where-Object { $_.title -ceq $title })
    if ($match.Count -ne 1 -or $match[0].kind -cne 'virtual' -or $match[0].tool -or $match[0].smart) {
        throw "需要唯一、可容纳桌面图标的虚拟分区「$title」。请先完成分区布局。"
    }
    $target[$title] = $match[0].id
}
$inbox = @($fences | Where-Object { $_.kind -ceq 'inbox' })
if ($inbox.Count -ne 1) {
    throw '无法识别 PecoFence 的原生收件箱。'
}

$projectKeywords = [ordered]@{
    '需求' = 1; '合同' = 2; '报价' = 3; '方案' = 4; '会议' = 5; '发票' = 6
    '立项' = 7; '纪要' = 8; '验收' = 9; '项目' = 10
}
$managed = @(
    $steamTools = @(
        @{ number = 31; id = '431960'; title = 'Wallpaper Engine' }
        @{ number = 32; id = '365670'; title = 'Blender' }
        @{ number = 33; id = '431730'; title = 'Aseprite' }
        @{ number = 34; id = '993090'; title = 'Lossless Scaling' }
    )
    foreach ($app in $steamTools) {
        New-DeskShelfRule $app.number "Steam 应用：$($app.title)" 'target' $target['工具入口'] @(
            [ordered]@{ cond = 'shortcutTarget'; value = [ordered]@{ op = 'is'; value = "steam://rungameid/$($app.id)" } }
        )
    }
    foreach ($soundtrack in @(@{ number = 35; keyword = 'Soundtrack' }, @{ number = 36; keyword = '原声' })) {
        New-DeskShelfRule $soundtrack.number "Steam 原声：$($soundtrack.keyword)" 'target' $target['图片影音'] @(
            [ordered]@{ cond = 'shortcutTarget'; value = [ordered]@{ op = 'startsWith'; value = 'steam://rungameid/' } }
            [ordered]@{ cond = 'name'; value = [ordered]@{ op = 'contains'; value = $soundtrack.keyword } }
        )
    }
    New-DeskShelfRule 37 'Steam 游戏启动快捷方式' 'target' $target['游戏'] @(
        [ordered]@{ cond = 'shortcutTarget'; value = [ordered]@{ op = 'startsWith'; value = 'steam://rungameid/' } }
    )

    foreach ($keyword in $projectKeywords.Keys) {
        New-DeskShelfRule $projectKeywords[$keyword] "文件名含「$keyword」" 'name' $target['项目资料'] @(
            [ordered]@{ cond = 'filesOnly' }
            [ordered]@{ cond = 'name'; value = [ordered]@{ op = 'contains'; value = $keyword } }
        )
    }

    New-DeskShelfRule 21 '图片、截图和音视频' 'type' $target['图片影音'] @(
        [ordered]@{ cond = 'type'; value = @('images', 'music', 'video') }
    )
    New-DeskShelfRule 22 '其他常见影音格式' 'type' $target['图片影音'] @(
        [ordered]@{ cond = 'filesOnly' }
        [ordered]@{ cond = 'ext'; value = @('apng', 'heif', 'jxl', 'dng', 'mpeg', 'mpg', 'm2ts', 'mts', 'flv', '3gp', 'aif', 'aiff', 'alac', 'ape', 'mid', 'midi') }
    )
    New-DeskShelfRule 23 '常用办公文档' 'type' $target['项目资料'] @(
        [ordered]@{ cond = 'filesOnly' }
        [ordered]@{ cond = 'ext'; value = @(
            'doc', 'docx', 'docm', 'dot', 'dotx', 'dotm', 'xls', 'xlsx', 'xlsm', 'xlsb', 'xlt', 'xltx', 'xltm',
            'ppt', 'pptx', 'pptm', 'pps', 'ppsx', 'ppsm', 'pot', 'potx', 'potm', 'pdf', 'txt', 'md', 'rtf',
            'odt', 'ods', 'odp', 'csv', 'tsv', 'one', 'wps', 'et', 'dps'
        ) }
    )
    New-DeskShelfRule 24 '快捷方式、程序和安装包' 'type' $target['工具入口'] @(
        [ordered]@{ cond = 'type'; value = @('programs', 'shortcuts', 'installers') }
    )
    New-DeskShelfRule 25 '脚本和传统可执行格式' 'type' $target['工具入口'] @(
        [ordered]@{ cond = 'filesOnly' }
        [ordered]@{ cond = 'ext'; value = @('ps1', 'vbs', 'vbe', 'jse', 'wsf', 'wsh', 'com', 'scr', 'pif') }
    )
)

$current = Invoke-PecoFence @('rule', 'list')
if ($null -eq $current.defaultTarget -or $null -eq $current.keepUpdated -or $null -eq $current.list) {
    throw 'PecoFence 未返回有效的原生规则配置。'
}

$starterPresets = @(
    @{ types = 'programs,shortcuts'; fence = $target['工具入口'] }
    @{ types = 'folders'; fence = $target['项目资料'] }
    @{ types = 'documents,images,music,video'; fence = $target['图片影音'] }
)
$currentRules = @($current.list | ForEach-Object { Copy-NativeRule $_ })
$starterIds = @()
if ($UseStarterLayout) {
    $starterIds = @(foreach ($preset in $starterPresets) {
        $matches = @($currentRules | Where-Object {
            $_.priorityClass -ceq 'type' -and
            -not $_.template -and $_.target.fence -eq $preset.fence -and
            $_.allOf.Count -eq 1 -and $_.allOf[0].cond -ceq 'type' -and
            ((($_.allOf[0].value | Where-Object { $_ -ne 'archives' }) | Sort-Object) -join ',') -ceq $preset.types
        })
        if ($matches.Count -eq 1) { $matches[0].id }
    })
    if ($starterIds.Count -gt 0 -and $starterIds.Count -ne $starterPresets.Count) {
        Write-Warning '只匹配到部分初始预设，现有规则全部保留。'
        $starterIds = @()
    }
}

$managedIds = @($managed | ForEach-Object { $_.id })
$disabledStarter = @()
$other = @(foreach ($rule in $currentRules) {
    if ($rule.id -in $managedIds) { continue }
    $copy = Copy-NativeRule $rule
    if ($copy.enabled -and $copy.id -in $starterIds) {
        $copy.enabled = $false
        $disabledStarter += $copy.name
    }
    $copy
})

# PecoFence evaluates shortcut-target rules first. Existing shortcut-target rules keep precedence.
$managedNamesAndTypes = @($managed | Where-Object { $_.priorityClass -ne 'target' })
$managedShortcutTargets = @($managed | Where-Object { $_.priorityClass -eq 'target' })
$before = [ordered]@{
    defaultTarget = $current.defaultTarget
    keepUpdated = $current.keepUpdated
    unmatched = $current.unmatched
    list = $currentRules
}
$after = [ordered]@{
    defaultTarget = $current.defaultTarget
    keepUpdated = $true
    unmatched = $current.unmatched
    list = $managedNamesAndTypes + $other + $managedShortcutTargets
}
$beforeJson = ConvertTo-Json -InputObject $before -Depth 40 -Compress
$afterJson = ConvertTo-Json -InputObject $after -Depth 40 -Compress
$changed = $beforeJson -cne $afterJson

if (-not $Apply) {
    [pscustomobject]@{
        mode = 'preview'
        wouldChange = $changed
        targets = [ordered]@{
            '项目资料' = $target['项目资料']
            '图片影音' = $target['图片影音']
            '工具入口' = $target['工具入口']
            '游戏' = $target['游戏']
            '收件箱' = $inbox[0].id
        }
        keepUpdated = $after.keepUpdated
        defaultTarget = $after.defaultTarget
        disableStarterRules = $disabledStarter
        preservedRules = $other.Count
        rules = $managed
    }
    return
}

if ($changed -and $PSCmdlet.ShouldProcess('PecoFence', '安装桌屿自动归类规则')) {
    $latest = Invoke-PecoFence @('rule', 'list')
    if ((ConvertTo-Json -InputObject $latest -Depth 40 -Compress) -cne (ConvertTo-Json -InputObject $current -Depth 40 -Compress)) {
        throw '规则在读取后发生变动，请重新运行此脚本。'
    }

    $tempFile = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($tempFile, $afterJson, [Text.UTF8Encoding]::new($false))
        $result = Invoke-PecoFence @('rule', 'import', $tempFile)
        if ($result.warning) { Write-Warning $result.warning }
        [pscustomobject]@{ mode = 'apply'; changed = $result.changed; managedRules = $managed.Count; disabledStarterRules = $disabledStarter }
    } finally {
        Remove-Item -LiteralPath $tempFile -ErrorAction SilentlyContinue
    }
} elseif (-not $changed) {
    [pscustomobject]@{ mode = 'apply'; changed = $false; managedRules = $managed.Count; disabledStarterRules = @() }
}

if ($PreviewExisting) {
    Invoke-PecoFence @('rule', 'apply', '--dry-run')
}
