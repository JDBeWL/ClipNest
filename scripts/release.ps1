#Requires -Version 5.1
<#
  构建 ClipNest 的四种发行包：
  含 .NET 运行时的安装包与便携版，以及需要 .NET 10 运行时的安装包与便携版。
  安装包依赖 Inno Setup 6 的 ISCC.exe；缺失时跳过安装包，便携版仍会生成。
#>
param(
  [string]$Version = '',
  [string]$Runtime = 'win-x64',
  [string]$InnoSetup = '',
  [switch]$SkipInstaller,
  [switch]$SkipPortable
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if (-not $Version) {
  $versionFile = Join-Path $repo 'VERSION'
  if (Test-Path $versionFile) {
    $Version = (Get-Content $versionFile -Raw).Trim()
  }
}
if (-not $Version) {
  [xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
  $versionNode = $props.SelectSingleNode('//Version')
  if ($versionNode) { $Version = $versionNode.InnerText.Trim() }
}
if (-not $Version) {
  throw '未找到版本号；请在根目录 VERSION 文件中设置，或传入 -Version。'
}

$artifacts = Join-Path $repo 'artifacts'
$stagingRoot = Join-Path $artifacts 'staging'
$installerRoot = Join-Path $artifacts 'installer'
$portableRoot = Join-Path $artifacts 'portable'

foreach ($dir in @($stagingRoot, $installerRoot, $portableRoot)) {
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$variants = @(
  [pscustomobject]@{
    Name = 'with-dotnet'
    Profile = 'win-x64'
    RequiresDotNet = '0'
    Title = '含 .NET 运行时'
    Note = '自带 .NET 运行时，无需额外安装。'
  },
  [pscustomobject]@{
    Name = 'without-dotnet'
    Profile = 'win-x64-framework-dependent'
    RequiresDotNet = '1'
    Title = '需要 .NET 10 运行时'
    Note = '不包含 .NET 运行时，请先安装 .NET 10 运行时：https://dotnet.microsoft.com/download/dotnet/10.0'
  }
)

$staged = @()

foreach ($variant in $variants) {
  $out = Join-Path $stagingRoot "ClipNest-$Version-$Runtime-$($variant.Name)"
  Write-Host "发布 $($variant.Name) ..."
  $ErrorActionPreference = 'Continue'
  & dotnet publish 'src/ClipNest.App/ClipNest.App.csproj' -c Release `
    -p:PublishProfile=$($variant.Profile) -p:Version=$Version `
    -p:RuntimeIdentifier=$Runtime -o $out --nologo
  $exitCode = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'
  if ($exitCode -ne 0) { throw "发布失败：$($variant.Name)" }
  $staged += [pscustomobject]@{ Variant = $variant; PublishDir = $out }
}

if (-not $SkipInstaller) {
  $candidates = @()
  if ($env:ProgramFiles) {
    $candidates += Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'
    $candidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
  }
  if (${env:ProgramFiles(x86)}) {
    $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'
    $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
  }
  if ($env:LOCALAPPDATA) {
    $candidates += Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'
    $candidates += Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
  }

  $uninstallRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
  )
  Get-ItemProperty $uninstallRoots -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'Inno Setup' -and $_.InstallLocation } |
    ForEach-Object { $candidates += Join-Path $_.InstallLocation 'ISCC.exe' }

  $iscc = $InnoSetup
  if (-not $iscc) {
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
  }

  if (-not $iscc -or -not (Test-Path $iscc)) {
    Write-Warning '未找到 ISCC.exe（Inno Setup 7 或 6），跳过安装包。安装后重试，或用 -InnoSetup 指定路径。'
  } else {
    foreach ($item in $staged) {
      $publishRelative = "..\artifacts\staging\ClipNest-$Version-$Runtime-$($item.Variant.Name)"
      $baseName = "ClipNest-$Version-$Runtime-$($item.Variant.Name)-setup"
      Write-Host "生成安装包 $($item.Variant.Name) ..."
      $ErrorActionPreference = 'Continue'
      & $iscc "/DMyAppVersion=$Version" "/DMyPublishDir=$publishRelative" `
        '/DMyOutputDir=..\artifacts\installer' "/DMyOutputBaseFilename=$baseName" `
        "/DMyRequiresDotNet=$($item.Variant.RequiresDotNet)" 'installer\clipnest.iss'
      $exitCode = $LASTEXITCODE
      $ErrorActionPreference = 'Stop'
      if ($exitCode -ne 0) { throw "安装包生成失败：$($item.Variant.Name)" }
    }
  }
}

if (-not $SkipPortable) {
  foreach ($item in $staged) {
    $readme = @"
ClipNest $Version 便携版
$($item.Variant.Title)

$($item.Variant.Note)

运行方式：双击 ClipNest.App.exe。
数据保存在本目录的 data 子目录；若目录不可写，会退回用户目录。
"@
    Set-Content -Path (Join-Path $item.PublishDir 'README.txt') -Value $readme -Encoding UTF8
    $zip = Join-Path $portableRoot "ClipNest-$Version-$Runtime-$($item.Variant.Name).zip"
    Write-Host "打包 $($item.Variant.Name) ..."
    Compress-Archive -Path $item.PublishDir -DestinationPath $zip -CompressionLevel Optimal
  }
}

Write-Host ''
Write-Host '生成结果：'
$files = @()
$files += Get-ChildItem $installerRoot -File -ErrorAction SilentlyContinue
$files += Get-ChildItem $portableRoot -File -ErrorAction SilentlyContinue
$files | ForEach-Object {
  '{0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.FullName.Substring($repo.Length + 1)
}
