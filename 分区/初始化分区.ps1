[CmdletBinding()]
param(
    [string]$CliPath,
    [string]$Instance,
    [switch]$UseStarterLayout,
    [switch]$Preview,
    [switch]$InstallStarterRules,
    [string]$ExpectedConfigPath,
    [string]$PreviousRecentPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-FenceCli {
    param([string[]]$Command)

    # Windows PowerShell 5.1 wraps native stderr in ErrorRecord objects.
    $ErrorActionPreference = 'Continue'
    $output = & $CliPath --ascii --compact --timeout 8000 @instanceFlags @Command 2>&1
    $exitCode = $LASTEXITCODE
    $message = ($output | ForEach-Object { [string]$_ }) -join "`n"
    if ($exitCode -ne 0) {
        $reason = $null
        try { $reason = (ConvertFrom-Json -InputObject $message -ErrorAction Stop).error.message } catch {}
        if (-not $reason) { $reason = $message -split '\r?\n' | Select-Object -First 1 }
        throw "PecoFence $($Command[0]) $($Command[1]) 失败（退出码 $exitCode）。$reason"
    }
    $result = ConvertFrom-Json -InputObject $message -ErrorAction Stop
    if ($null -ne $result -and $result.PSObject.Properties['warning'] -and $result.warning) {
        Write-Warning $result.warning
    }
    return $result
}

function Find-FenceRect {
    param([object[]]$Monitors, [object[]]$Occupied, [string]$Title, [int]$Density)

    # 优先排成靠右的等宽列。尺寸以 DIP 选择，再按每块屏幕的 DPI 转为物理像素。
    $sizes = @(
        @{ w = 240; h = 184 }
        @{ w = 200; h = 144 }
        @{ w = 144; h = 100 }
    )
    switch ($Title) {
        '工具入口' { $sizes = @(@{ w = 400; h = 320 }, @{ w = 320; h = 240 }) + $sizes }
        '游戏' { $sizes = @(@{ w = 400; h = 250 }, @{ w = 320; h = 200 }) + $sizes }
        { $_ -in @('图片影音', '收件箱') } { $sizes = $sizes[1..2] }
    }
    if ($Density -eq 1) { $sizes = @(@{ w = 200; h = 144 }, @{ w = 144; h = 100 }) }
    if ($Density -eq 2) { $sizes = @(@{ w = 144; h = 100 }) }
    if ($Density -eq 3) { $sizes = @(@{ w = 96; h = 64 }, @{ w = 64; h = 36 }) }
    foreach ($size in $sizes) {
        foreach ($monitor in $Monitors) {
            $area = $monitor.workArea
            $scale = [double]$monitor.dpi / 96
            $gap = [int][Math]::Ceiling($(if ($Density -eq 3) { 8 } else { 14 }) * $scale)
            $width = [int][Math]::Ceiling($size.w * $scale)
            $height = [int][Math]::Ceiling($size.h * $scale)
            $left = $area.x + $gap
            $top = $area.y + $gap
            $right = $area.x + $area.w - $gap
            $bottom = $area.y + $area.h - $gap
            $xCandidates = @($right - $width) + @($Occupied | ForEach-Object { $_.x - $gap - $width })
            $yCandidates = @($top) + @($Occupied | ForEach-Object { $_.y + $_.h + $gap })

            $xs = @($xCandidates | Sort-Object -Descending -Unique)
            $ys = @($yCandidates | Sort-Object -Unique)
            $topBand = $top + [Math]::Ceiling(320 * $scale) + $gap + [Math]::Ceiling(250 * $scale)
            foreach ($limit in @([Math]::Min($bottom, $topBand), $bottom) | Select-Object -Unique) {
                foreach ($x in $xs) {
                    if ($x -lt $left -or $x + $width -gt $right) { continue }
                    foreach ($y in $ys) {
                        if ($y -lt $top -or $y + $height -gt $limit) { continue }
                        $overlap = $Occupied | Where-Object {
                            $x -lt $_.x + $_.w + $gap -and $_.x -lt $x + $width + $gap -and
                            $y -lt $_.y + $_.h + $gap -and $_.y -lt $y + $height + $gap
                        } | Select-Object -First 1
                        if (-not $overlap) { return @{ x = $x; y = $y; w = $width; h = $height } }
                    }
                }
            }
        }
    }
}

$mutex = $null
$ownsLock = $false
try {
    if ($ExpectedConfigPath) { [Console]::OutputEncoding = [Text.Encoding]::UTF8 }
    if (-not $CliPath) { $CliPath = Join-Path $PSScriptRoot 'PecoFence\pecofence-cli.exe' }
    $CliPath = (Get-Item -LiteralPath $CliPath).FullName
    $recentScript = (Get-Item -LiteralPath (Join-Path $PSScriptRoot '最近文档.ps1')).FullName
    $explicitInstance = $PSBoundParameters.ContainsKey('Instance')
    if (-not $explicitInstance) {
        $Instance = $env:PECOFENCE_INSTANCE
        if ($null -eq $Instance) { $Instance = $env:OPENFENCE_INSTANCE }
    }
    $Instance = ([string]$Instance).Trim()
    $instanceFlags = @()
    if ($explicitInstance -and -not $Instance) {
        throw '请提供非空的实例名称，或省略 -Instance 使用当前实例。'
    }
    if ($Instance) { $instanceFlags = @('--instance', $Instance) }

    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $pipeName = "PecoFence.$sid"
    if ($Instance) { $pipeName += ".$Instance" }
    $mutex = [Threading.Mutex]::new($false, "Local\DeskShelf.Initialize.$pipeName")
    try { $ownsLock = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsLock = $true }
    if (-not $ownsLock) { throw '该 PecoFence 实例正在初始化分区。' }

    if ($ExpectedConfigPath) {
        $status = Invoke-FenceCli @('--fields', 'configPath', 'status')
        if (-not $status.configPath -or [IO.Path]::GetFullPath($status.configPath) -ne [IO.Path]::GetFullPath($ExpectedConfigPath)) {
            throw '运行中的分区引擎已发生变化，请重新打开桌屿。'
        }
    }
    $existing = @(Invoke-FenceCli @('fence', 'list'))
    if ($PreviousRecentPath) {
        foreach ($fence in $existing | Where-Object { $_.tool -eq 'script' -and $_.script.path -eq $PreviousRecentPath }) {
            if (-not $Preview) {
                Invoke-FenceCli @('fence', 'set', $fence.id, 'scriptPath', $recentScript, '--string') | Out-Null
            }
        }
        return
    }
    $allMonitors = @()
    for ($retry = 0; $retry -lt 6; $retry++) {
        if ($retry -gt 0) { Start-Sleep -Milliseconds 400 }
        $allMonitors = @(Invoke-FenceCli @('monitor', 'list') | Where-Object {
            $_.workArea.w -gt 0 -and $_.workArea.h -gt 0 -and $_.dpi -gt 0
        } | Sort-Object -Property primary -Descending)
        if ($allMonitors.Count -gt 0) { break }
    }
    if ($allMonitors.Count -eq 0) { throw 'PecoFence 暂未报告可用的显示器工作区。' }
    $monitors = @(if ($UseStarterLayout) { $allMonitors[0] } else { $allMonitors })

    $inboxes = @($existing | Where-Object { $_.kind -eq 'inbox' })
    if ($inboxes.Count -ne 1) { throw '未找到唯一的 PecoFence 内建收件箱。' }
    $inbox = $inboxes[0]
    $pendingRecentTitle = '最近 7 天（桌屿初始化中）'
    $plan = @(
        [pscustomobject]@{ Title = '工具入口'; Kind = 'virtual'; StockTypes = 'programs,shortcuts'; Action = 'Create'; Fence = $null; Rect = $null }
        [pscustomobject]@{ Title = '游戏'; Kind = 'virtual'; StockTypes = ''; Action = 'Create'; Fence = $null; Rect = $null }
        [pscustomobject]@{ Title = '项目资料'; Kind = 'virtual'; StockTypes = 'folders'; Action = 'Create'; Fence = $null; Rect = $null }
        [pscustomobject]@{ Title = '图片影音'; Kind = 'virtual'; StockTypes = 'documents,images,music,video'; Action = 'Create'; Fence = $null; Rect = $null }
        [pscustomobject]@{ Title = '收件箱'; Kind = 'inbox'; StockTypes = ''; Action = 'Reuse'; Fence = $inbox; Rect = $null }
        [pscustomobject]@{ Title = '最近 7 天'; Kind = 'script'; StockTypes = ''; Action = 'Create'; Fence = $null; Rect = $null }
    )
    if ($UseStarterLayout) { $starterRules = (Invoke-FenceCli @('rule', 'list')).list }

    foreach ($entry in $plan) {
        $matchesByTitle = @($existing | Where-Object { $_.title -ceq $entry.Title })
        if ($matchesByTitle.Count -gt 1) { throw "分区名称「$($entry.Title)」重复。请先区分同名分区。" }
        if ($entry.Kind -eq 'script') {
            if ($matchesByTitle.Count -eq 1 -and $matchesByTitle[0].script.path -ne $recentScript) {
                throw "现有「$($entry.Title)」未使用随附的最近文档脚本。"
            }
            $scriptFences = @($existing | Where-Object { $_.tool -eq 'script' -and $_.script.path -eq $recentScript })
            if ($scriptFences.Count -gt 1) { throw '检测到多个使用最近文档脚本的分区。' }
            if ($scriptFences.Count -eq 1) {
                $entry.Fence = $scriptFences[0]
                $entry.Action = if ($entry.Fence.title -ceq $pendingRecentTitle) { 'Complete' } else { 'Reuse' }
            }
            elseif ($existing | Where-Object { $_.title -ceq $pendingRecentTitle }) {
                throw "现有「$pendingRecentTitle」与最近文档分区初始化冲突。"
            }
            continue
        }
        if ($matchesByTitle.Count -eq 1) {
            $fence = $matchesByTitle[0]
            if ($fence.kind -ne $entry.Kind -or $fence.tool -or $fence.smart) {
                throw "现有「$($entry.Title)」的类型与初始化方案冲突。"
            }
            $entry.Action = 'Reuse'
            $entry.Fence = $fence
            continue
        }
        if (-not $UseStarterLayout) { continue }
        if ($entry.Kind -eq 'inbox') {
            $entry.Action = 'Convert'
            continue
        }
        if (-not $entry.StockTypes) { continue }

        # Native starter rules use the same conditions across Windows display languages.
        $starterIds = @($starterRules | Where-Object {
            $_.priorityClass -ceq 'type' -and -not $_.template -and $_.allOf.Count -eq 1 -and
            $_.allOf[0].cond -ceq 'type' -and
            ((($_.allOf[0].value | Where-Object { $_ -ne 'archives' }) | Sort-Object) -join ',') -ceq $entry.StockTypes
        } | ForEach-Object { $_.target.fence })
        $stock = @($existing | Where-Object {
            $_.kind -eq $entry.Kind -and $_.id -in $starterIds -and
            -not $_.tool -and -not $_.smart -and -not $_.tabHost -and -not $_.tabs -and
            -not $_.locked -and -not $_.autoHeight
        })
        if ($stock.Count -ne 1) {
            throw "未能识别默认分区「$($entry.Title)」的对应项。现有布局请省略 -UseStarterLayout。"
        }
        $entry.Action = 'Convert'
        $entry.Fence = $stock[0]
    }

    if ($UseStarterLayout) {
        $knownIds = @($plan | ForEach-Object { $_.Fence.id })
        if ($existing | Where-Object { $_.id -notin $knownIds }) {
            throw '检测到初始布局以外的分区。现有布局请省略 -UseStarterLayout。'
        }
    }

    $occupiedBase = @(
        $convertIds = @($plan | Where-Object Action -eq 'Convert' | ForEach-Object { $_.Fence.id })
        foreach ($fence in $existing) {
            if ($fence.id -in $convertIds -or $fence.tabHost) { continue }
            # 桌面排列型 inbox 是底层图标网格，会自行绕开其他分区。
            if ($fence.kind -eq 'inbox' -and $fence.desktopLayout) { continue }
            if ($fence.rect) { $fence.rect }
            if ($fence.windowRect) { $fence.windowRect }
        }
    )
    foreach ($density in 0..3) {
        $occupied = @($occupiedBase)
        $unplaced = $null
        foreach ($entry in $plan) {
            if ($entry.Action -in @('Reuse', 'Complete')) {
                $entry.Rect = $entry.Fence.rect
                continue
            }
            $entry.Rect = Find-FenceRect -Monitors $monitors -Occupied $occupied -Title $entry.Title -Density $density
            if (-not $entry.Rect) {
                $unplaced = $entry.Title
                break
            }
            $occupied += $entry.Rect
        }
        if (-not $unplaced) { break }
    }
    if ($unplaced) { throw "工作区没有足够的空位放置「$unplaced」。" }

    if (-not $Preview) {
        if ($InstallStarterRules) {
            Invoke-FenceCli @('settings', 'set', 'autoCheckUpdates', 'false') | Out-Null
            Invoke-FenceCli @('settings', 'set', 'usageStats', 'false') | Out-Null
        }
        foreach ($entry in $plan) {
            if ($entry.Action -eq 'Reuse') { continue }
            $r = $entry.Rect
            $rectArgument = '{0},{1},{2},{3}' -f $r.x, $r.y, $r.w, $r.h
            $rollOnCreate = $entry.Title -in @('图片影音', '收件箱')
            if ($entry.Action -eq 'Convert') {
                $id = $entry.Fence.id
                if ($entry.Kind -eq 'inbox' -and $entry.Fence.desktopLayout) {
                    Invoke-FenceCli @('fence', 'set', $id, 'desktopLayout', 'false') | Out-Null
                }
                Invoke-FenceCli @('fence', 'move', $id, "--rect=$rectArgument") | Out-Null
                $rollCommand = if ($rollOnCreate) { 'roll' } else { 'unroll' }
                Invoke-FenceCli @('fence', $rollCommand, $id) | Out-Null
                # 将改名放在最后。若前一步中断，重试仍可从默认名称识别同一个分区。
                $result = Invoke-FenceCli @('fence', 'rename', $id, $entry.Title)
            }
            elseif ($entry.Kind -eq 'script') {
                $id = $entry.Fence.id
                if ($entry.Action -eq 'Create') {
                    $created = Invoke-FenceCli @('fence', 'create', '--title', $pendingRecentTitle, "--rect=$rectArgument", '--script', $recentScript)
                    $id = $created.fence.id
                }
                if (-not $id) { throw "未收到「$($entry.Title)」的分区 ID。" }
                $configured = Invoke-FenceCli @('fence', 'set', $id, 'scriptInterval', '1')
                if ($configured.fence.script.intervalMinutes -ne 1) { throw '未能设置最近文档每分钟刷新。' }
                $result = Invoke-FenceCli @('fence', 'rename', $id, $entry.Title)
            }
            else {
                $result = Invoke-FenceCli @('fence', 'create', '--title', $entry.Title, "--rect=$rectArgument")
                if ($rollOnCreate -and $result.fence.id) {
                    $result = Invoke-FenceCli @('fence', 'roll', $result.fence.id)
                }
            }
            if (-not $result.fence.id) { throw "未收到「$($entry.Title)」的分区 ID。" }
            $entry.Fence = $result.fence
            $entry.Rect = $result.fence.rect
        }
        if ($InstallStarterRules) {
            $ruleOptions = @{ Apply = $true; UseStarterLayout = $UseStarterLayout; CliPath = $CliPath }
            if ($Instance) { $ruleOptions.Instance = $Instance }
            & (Join-Path $PSScriptRoot '智能规则.ps1') @ruleOptions | Out-Null
            Invoke-FenceCli @('rule', 'apply') | Out-Null
        }
    }

    foreach ($entry in $plan) {
        [pscustomobject]@{
            Title = $entry.Title
            ActualTitle = $entry.Fence.title
            Id = $entry.Fence.id
            Kind = $entry.Kind
            Action = $entry.Action
            Rect = $entry.Rect
        }
    }
}
catch {
    if (-not $ExpectedConfigPath) { throw }
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
finally {
    if ($ownsLock) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
