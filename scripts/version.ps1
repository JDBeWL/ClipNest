#Requires -Version 5.1
<#
  查看或更新仓库根目录 VERSION 文件中的版本号。
  用法：
    .\scripts\version.ps1
    .\scripts\version.ps1 0.2.0
    .\scripts\version.ps1 -Bump patch|minor|major
#>
[CmdletBinding(DefaultParameterSetName = 'Show')]
param(
  [Parameter(ParameterSetName = 'Set', Position = 0)]
  [string]$Set,

  [Parameter(ParameterSetName = 'Bump', Mandatory = $true)]
  [ValidateSet('major', 'minor', 'patch')]
  [string]$Bump
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repo 'VERSION'
$semverPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$'

function Read-CurrentVersion {
  if (-not (Test-Path $versionFile)) {
    throw "未找到版本文件：$versionFile"
  }
  $value = (Get-Content $versionFile -Raw).Trim()
  if ($value -notmatch $semverPattern) {
    throw "VERSION 文件内容格式无效：$value。请使用 MAJOR.MINOR.PATCH 格式。"
  }
  return $value
}

function Write-VersionFile([string]$value) {
  # 写入无 BOM 的 UTF-8，确保 Inno Setup 的 FileRead 与 MSBuild 都能直接读取。
  $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText($versionFile, "$value`n", $utf8NoBom)
}

if ($PSCmdlet.ParameterSetName -eq 'Set') {
  $target = $Set.Trim()
  if ($target -notmatch $semverPattern) {
    throw "版本号格式无效：$target。请使用 MAJOR.MINOR.PATCH 或带预发布后缀的格式，例如 0.2.0-rc.1。"
  }
  $previous = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { '' }
  Write-VersionFile $target
  if ($previous) {
    Write-Host "$previous -> $target"
  } else {
    Write-Host $target
  }
  return
}

$current = Read-CurrentVersion

if ($PSCmdlet.ParameterSetName -eq 'Bump') {
  $null = $current -match $semverPattern
  $major = [int]$Matches[1]
  $minor = [int]$Matches[2]
  $patch = [int]$Matches[3]

  switch ($Bump) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
  }

  $next = "$major.$minor.$patch"
  Write-VersionFile $next
  Write-Host "$current -> $next"
  return
}

Write-Output $current
