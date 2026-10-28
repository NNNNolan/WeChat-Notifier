[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MakePriExe,
    [Parameter(Mandatory)][string]$InputFile,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$name = [IO.Path]::GetFileNameWithoutExtension($InputFile)
$output = Join-Path $OutputDirectory ($name + '.pri')
$scriptFile = $PSCommandPath
if ((Test-Path -LiteralPath $output) -and
    (Get-Item -LiteralPath $output).LastWriteTimeUtc -ge (Get-Item -LiteralPath $InputFile).LastWriteTimeUtc -and
    (Get-Item -LiteralPath $output).LastWriteTimeUtc -ge (Get-Item -LiteralPath $scriptFile).LastWriteTimeUtc) { return }

$work = Join-Path $OutputDirectory $name
New-Item -ItemType Directory -Path $work -Force | Out-Null
$dump = Join-Path $work ($name + '.pri.xml')
$generated = Join-Path $work 'generated.pri'
& $MakePriExe dump /if $InputFile /of $dump /dt detailed /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "无法读取 PRI：$InputFile（$LASTEXITCODE）" }
$xml = [xml]::new()
$xml.Load($dump)
$resourceCount = @($xml.SelectNodes('//NamedResource')).Count

# SDK 有九项没有简中资源，明确提供译文，不保留英文语言包也不丢失资源键。
$missingTranslations = @{
    InkToolbarBallpointPenFlyoutName = '圆珠笔浮出控件'
    InkToolbarPencilFlyoutName = '铅笔浮出控件'
    InkToolbarPenConfigurationSizeSliderName = '笔的大小'
    InkToolbarStencilButtonName = '测量工具'
    InkToolbarStencilDropDownControlTypeName = '下拉菜单'
    InkToolbarStencilFlyoutName = '模板浮出控件'
    InkToolbarStrokeWidthSliderRangeFormat = '最小值 %1!s!，最大值 %2!s!'
    RatingControlName = ''
    RatingsControlName = ''
}
foreach ($resource in $xml.SelectNodes('//NamedResource')) {
    $candidates = @($resource.SelectNodes('Candidate'))
    $strings = @($candidates | Where-Object { $_.GetAttribute('type') -eq 'String' })
    if ($strings.Count -eq 0) { continue }
    $chinese = @($strings | Where-Object {
        $_.SelectSingleNode("QualifierSet/Qualifier[@name='Language' and translate(@value,'ZH-CN','zh-cn')='zh-cn']")
    })
    if ($chinese.Count -gt 0) { continue }
    $resourceName = $resource.GetAttribute('name')
    if (!$missingTranslations.ContainsKey($resourceName)) { throw "SDK 资源缺少简中译文：$resourceName" }
    $english = $resource.SelectSingleNode("Candidate[QualifierSet/Qualifier[@name='Language' and @value='EN-US']]")
    if ($null -eq $english) { throw "无法读取待翻译资源：$resourceName" }
    $candidate = $english.CloneNode($true)
    $candidate.SelectSingleNode('Value').InnerText = $missingTranslations[$resourceName]
    $candidate.SelectSingleNode("QualifierSet/Qualifier[@name='Language']").SetAttribute('value', 'ZH-CN')
    $resource.AppendChild($candidate) | Out-Null
}
foreach ($node in @($xml.SelectNodes('//Candidate | //QualifierSet'))) {
    $foreign = @($node.SelectNodes(".//Qualifier[@name='Language']") | Where-Object {
        $_.GetAttribute('value') -ine 'zh-CN'
    })
    if ($foreign.Count -gt 0 -and $null -ne $node.ParentNode) { $node.ParentNode.RemoveChild($node) | Out-Null }
}
foreach ($qualifier in @($xml.SelectNodes("//Qualifier[@name='Language']"))) {
    if ($qualifier.GetAttribute('value') -ine 'zh-CN') {
        $qualifier.ParentNode.RemoveChild($qualifier) | Out-Null
    }
    else { $qualifier.SetAttribute('scoreAsDefault', '1.0') }
}
$xml.Save($dump)
$mapName = $xml.SelectSingleNode('//ResourceMap').GetAttribute('name')
$config = Join-Path $work 'priconfig.xml'
$configXml = [xml]@'
<resources targetOsVersion="10.0.0" majorVersion="1">
  <index root="\" startIndexAt="">
    <default><qualifier name="Language" value="zh-CN"/></default>
    <indexer-config type="priinfo" emitStrings="true" emitPaths="true" emitEmbeddedData="true"/>
  </index>
</resources>
'@
$configXml.resources.index.SetAttribute('startIndexAt', [IO.Path]::GetFileName($dump))
$configXml.Save($config)
& $MakePriExe new /pr $work /cf $config /of $generated /in $mapName /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "无法生成简中 PRI：$name（$LASTEXITCODE）" }

$check = Join-Path $work 'verified.xml'
& $MakePriExe dump /if $generated /of $check /dt detailed /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "无法验证简中 PRI：$name（$LASTEXITCODE）" }
$verified = [xml]::new()
$verified.Load($check)
if (@($verified.SelectNodes('//NamedResource')).Count -ne $resourceCount) { throw "语言裁剪丢失资源键：$name" }
foreach ($qualifier in $verified.SelectNodes("//Qualifier[@name='Language']")) {
    if ($qualifier.GetAttribute('value') -ine 'zh-CN') { throw "语言裁剪仍有非简中资源：$name" }
}
Move-Item -LiteralPath $generated -Destination $output -Force
Write-Host "简中 PRI：$name，保留 $resourceCount 个资源键。"
