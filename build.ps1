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

    if ($Aot -and !(Get-Command link.exe -ErrorAction SilentlyContinue)) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (!(Test-Path -LiteralPath $vswhere)) {
            throw 'Native AOT 需要 Visual Studio 的「使用 C++ 的桌面开发」及 Windows SDK。'
        }
        $install = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (!$install) { throw '未找到 C++ 构建工具；请安装 Visual Studio「使用 C++ 的桌面开发」。' }
        Import-Module (Join-Path $install 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
        Enter-VsDevShell -VsInstallPath $install -SkipAutomaticLocation -DevCmdArguments "-arch=$Architecture -host_arch=x64" | Out-Null
    }

    $publishAot = $Aot.IsPresent.ToString().ToLowerInvariant()
    dotnet publish $project -c Release -r "win-$Architecture" -p:Platform=$Architecture -p:PublishProfile= `
        "-p:PublishAot=$publishAot" -p:WindowsAppSDKSelfContained=true -p:UseSharedCompilation=false `
        --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE。" }

    $exe = Join-Path $output 'WeChat-Notifier.exe'
    if (!$Aot) {
        $files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object {
            [IO.Path]::GetRelativePath($output, $_.FullName).Split([IO.Path]::DirectorySeparatorChar)[0] -notin @('Config', 'Log')
        })
        if ($files.Count -ne 1 -or $files[0].FullName -ne $exe) {
            throw '单文件发布仍包含额外文件，请检查发布设置。'
        }
    }
    if (!$SkipTests -and $Architecture -eq [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()) {
        & $exe --selftest | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "发布产物自测失败，退出码 $LASTEXITCODE。" }
    }
    $zip = Join-Path $PSScriptRoot "dist\WeChat-Notifier-win-$Architecture-$mode.zip"
    $publishFiles = if ($Aot) { Join-Path $output '*' } else { $exe }
    Compress-Archive -Path $publishFiles -DestinationPath $zip -Force
    Write-Host "已发布：$output"
    Write-Host "压缩包：$zip"
    if ($Run) { Start-Process -FilePath $exe -ArgumentList '--settings' -WorkingDirectory $output -WindowStyle Hidden }
}
finally { Pop-Location }
