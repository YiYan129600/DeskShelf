[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path (Split-Path $PSScriptRoot -Parent) '桌屿.exe'
$command = '"' + $launcher + '" --arrange'
$subkey = 'Software\Classes\DesktopBackground\Shell\DeskShelf.Arrange'
$user = [Microsoft.Win32.Registry]::CurrentUser
$previous = $user.OpenSubKey($subkey + '\command')
if ($previous) {
    try { $value = [string]$previous.GetValue('') } finally { $previous.Dispose() }
    if ($value -and $value -ne $command) { throw '这个右键入口已经指向其他安装位置。' }
}
if ($Remove) {
    $user.DeleteSubKeyTree($subkey, $false)
    return
}
if (-not [IO.File]::Exists($launcher)) { throw '找不到桌屿启动器。' }
$menu = $user.CreateSubKey($subkey)
try {
    $menu.SetValue('MUIVerb', '自动排列桌面分区')
    $menu.SetValue('Icon', ('"' + $launcher + '",0'))
    $action = $menu.CreateSubKey('command')
    try { $action.SetValue('', $command) } finally { $action.Dispose() }
} finally { $menu.Dispose() }
