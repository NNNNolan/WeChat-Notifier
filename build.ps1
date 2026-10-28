[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [switch]$Aot,
    [switch]$NoAot,
    [switch]$Run,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    $project = Join-Path $PSScriptRoot 'src\WeChat-Notifier.App\WeChat-Notifier.csproj'
    if ($Aot -and $NoAot) { throw '-Aot 与 -NoAot 不能同时使用。' }
    $mode = if ($Aot) { 'aot' } else { 'single' }
    $output = Join-Path $PSScriptRoot "dist\win-$Architecture-$mode"

    if ($Aot) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (!(Test-Path -LiteralPath $vswhere)) {
            throw 'Native AOT 需要 Visual Studio 的「使用 C++ 的桌面开发」及 Windows SDK。'
        }
        $component = if ($Architecture -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
        $install = & $vswhere -latest -prerelease -products '*' -requires $component -property installationPath
        if ($LASTEXITCODE -ne 0 -or !$install) { throw "未找到 $Architecture C++ 构建工具；请安装 Visual Studio「使用 C++ 的桌面开发」及对应架构工具。" }
        Import-Module (Join-Path $install 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
        Enter-VsDevShell -VsInstallPath $install -SkipAutomaticLocation -DevCmdArguments "-arch=$Architecture -host_arch=x64" | Out-Null
        if (!(Get-Command link.exe -ErrorAction SilentlyContinue)) { throw 'Visual Studio 环境未提供 link.exe。' }
    }

    # dotnet publish 不会删除旧文件；仅清理当前发布目录中的生成产物，保留用户数据。
    $dist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
    $output = [IO.Path]::GetFullPath($output)
    if ([IO.Path]::GetDirectoryName($output) -ne $dist) { throw "发布目录超出 dist：$output" }
    if (Test-Path -LiteralPath $output) {
        Get-ChildItem -LiteralPath $output -Force | Where-Object Name -NotIn @('Config', 'Log') | ForEach-Object {
            if ([IO.Path]::GetDirectoryName($_.FullName) -ne $output) { throw "清理路径超出发布目录：$($_.FullName)" }
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    }

    $publishAot = $Aot.IsPresent.ToString().ToLowerInvariant()
    dotnet publish $project -c Release -r "win-$Architecture" -p:Platform=$Architecture -p:PublishProfile= `
        "-p:PublishAot=$publishAot" -p:WindowsAppSDKSelfContained=true -p:UseSharedCompilation=false `
        --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE。" }

    $exe = Join-Path $output 'WeChat-Notifier.exe'
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "发布产物缺少程序：$exe" }
    $files = @(Get-ChildItem -LiteralPath $output -File -Recurse -Force | Where-Object {
        [IO.Path]::GetRelativePath($output, $_.FullName).Split([IO.Path]::DirectorySeparatorChar)[0] -notin @('Config', 'Log')
    })
    if (!$Aot) {
        if ($files.Count -ne 1 -or $files[0].FullName -ne $exe) {
            throw '单文件发布仍包含额外文件，请检查发布设置。'
        }
    }
    if (!$SkipTests -and $Architecture -eq [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()) {
        & $exe --selftest | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "发布产物自测失败，退出码 $LASTEXITCODE。" }
    }
    $zip = Join-Path $PSScriptRoot "dist\WeChat-Notifier-win-$Architecture-$mode.zip"
    $archive = [IO.Compression.ZipArchive]::new([IO.File]::Create($zip), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $entry = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
        }
    }
    finally { $archive.Dispose() }
    Write-Host "已发布：$output"
    Write-Host "压缩包：$zip"
    if ($Run) { Start-Process -FilePath $exe -ArgumentList '--settings' -WorkingDirectory $output -WindowStyle Hidden }
}
finally { Pop-Location }
