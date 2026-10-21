<#
 Rust Droper - release paketi: portable exe (publish-portable.ps1) + RustDroper-<surum>-win-x64.zip + SHA256SUMS.txt + RELEASE_NOTES.md.
 Kullanim: powershell -File build\package-release.ps1 [-Version 1.0.0] [-Output artifacts]
   -Version : zip adinda kullanilir (etiketten gelir; ornek 1.0.0 ya da 1.0.0-rc1). Bos ise Directory.Build.props <Version>.
   -Output  : cikti klasoru (git disi: artifacts/). Icinde: publish\ (exe), RustDroper-<surum>-win-x64.zip, SHA256SUMS.txt, RELEASE_NOTES.md.
 RELEASE_NOTES.md: CHANGELOG.md icindeki "## [x.y.z]" bolumu (rc/etiket soneki atilir); yoksa kisa bir metin.
 Zip icerigi: RustDroper.exe, README.txt (TR+EN kisa), LICENSE, THIRD-PARTY-NOTICES.md.
#>
param(
  [string]$Version = '',
  [string]$Output = 'artifacts'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

$props = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'Directory.Build.props')
$propsVersion = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $propsVersion }
$baseVersion = ($Version -split '-')[0]
if ($baseVersion -ne $propsVersion) {
  throw "Surum uyumsuz: istenen $Version, Directory.Build.props $propsVersion."
}

if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $root $Output }
if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$publishDir = Join-Path $Output 'publish'
& (Join-Path $PSScriptRoot 'publish-portable.ps1') -Output $publishDir
if ($LASTEXITCODE -ne 0) { throw 'publish-portable basarisiz' }

$stage = Join-Path $Output 'stage'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $publishDir 'RustDroper.exe') $stage
Copy-Item (Join-Path $PSScriptRoot 'release-readme.txt') (Join-Path $stage 'README.txt')
Copy-Item (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage

$zipName = "RustDroper-$Version-win-x64.zip"
$zip = Join-Path $Output $zipName
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -Recurse -Force $stage

# SHA256SUMS.txt: zip + zip icindeki RustDroper.exe (sha256sum -c ile uyumlu: 'hash  dosya')
$lines = foreach ($f in @($zip, (Join-Path $publishDir 'RustDroper.exe'))) {
  $hash = (Get-FileHash -Algorithm SHA256 $f).Hash.ToLowerInvariant()
  $name = if ($f -eq $zip) { $zipName } else { 'RustDroper.exe' }
  "$hash  $name"
}
[IO.File]::WriteAllText((Join-Path $Output 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

# Surum notlari: CHANGELOG.md'den ilgili bolum
$changelog = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'CHANGELOG.md')
$m = [regex]::Match($changelog, '(?ms)^## \[' + [regex]::Escape($baseVersion) + '\].*?(?=^## \[|\z)')
$notes = if ($m.Success) { $m.Value.Trim() } else { "Release $Version" }
$repoUrl = if ($env:GITHUB_REPOSITORY) { "https://github.com/$($env:GITHUB_REPOSITORY)" } else { '..' }
$footer = @"


---
**Download / Indir:** ``$zipName`` from **Assets** below (NOT the "Source code" files, they have no exe / "Source code" dosyalarinda exe YOKTUR) - unzip and run ``RustDroper.exe`` (Windows 10/11 x64, no install). Verify with ``SHA256SUMS.txt``.
The executable is **not code-signed**: Windows SmartScreen may warn; see the README for details and for building from source. / Exe kod imzali degildir; SmartScreen uyarabilir, ayrintilar README'de.
**Use at your own risk** - Twitch/Kick terms may restrict automation; not affiliated with Twitch, Kick, Facepunch or Rust. / Kullanim riski size aittir; Twitch, Kick, Facepunch veya Rust ile baglantili degildir.
Full changelog: [CHANGELOG.md]($repoUrl/blob/main/CHANGELOG.md) - [CHANGELOG.tr.md]($repoUrl/blob/main/CHANGELOG.tr.md) - [README]($repoUrl#readme)
"@
[IO.File]::WriteAllText((Join-Path $Output 'RELEASE_NOTES.md'), ($notes + $footer), (New-Object System.Text.UTF8Encoding($false)))

Get-ChildItem $Output -File | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Get-Content (Join-Path $Output 'SHA256SUMS.txt')
