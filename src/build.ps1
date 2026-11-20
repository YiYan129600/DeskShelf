$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$wpf = Join-Path $framework 'WPF'
$outputDirectory = Split-Path $PSScriptRoot -Parent
if ($args.Count -eq 1) { $outputDirectory = $args[0] }
$binary = Join-Path $outputDirectory '文件归档.exe'
$launcher = Join-Path $outputDirectory '桌屿.exe'
$options = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001',
    "/out:$binary", "/win32icon:$PSScriptRoot\DeskShelf.ico", "/win32manifest:$PSScriptRoot\app.manifest",
    "/resource:$PSScriptRoot\Main.xaml,Main.xaml", "/resource:$PSScriptRoot\DeskShelf.ico,DeskShelf.ico",
    "/reference:$wpf\WindowsBase.dll", "/reference:$wpf\PresentationCore.dll",
    "/reference:$wpf\PresentationFramework.dll", '/reference:System.Xaml.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Xml.Linq.dll'
)
$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Where-Object Name -ne 'ZoneLauncher.cs' | ForEach-Object FullName
& (Join-Path $framework 'csc.exe') @options @sources
if ($LASTEXITCODE -ne 0) { throw "Compiler failed ($LASTEXITCODE)." }
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$launcher" "/win32icon:$PSScriptRoot\DeskShelf.ico" "/win32manifest:$PSScriptRoot\app.manifest" /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\ZoneLauncher.cs" "$PSScriptRoot\AssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw "Launcher compiler failed ($LASTEXITCODE)." }
$guardFolder = Join-Path $outputDirectory '分区\PecoFence'
[IO.Directory]::CreateDirectory($guardFolder) | Out-Null
$guard = Join-Path $guardFolder '桌屿壁纸助手.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$guard" "/win32manifest:$PSScriptRoot\app.manifest" "$PSScriptRoot\..\分区\WallpaperTaskbar.cs"
if ($LASTEXITCODE -ne 0) { throw "Wallpaper helper compiler failed ($LASTEXITCODE)." }
Get-Item -LiteralPath $launcher,$binary | Select-Object FullName, Length
