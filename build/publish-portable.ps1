<#
 Rust Droper - portable yayin (self-contained, tek dosya, SIKISTIRMASIZ: bellek icin tercih, bkz. docs/02-mimari.md 2).
 Kullanim:  powershell -File build\publish-portable.ps1 [-Output publish\RustDroper]
 Cikti klasoru git disindadir (publish/). Kullanici verisi exe'nin yanindaki data\ klasorunde tutulur.
#>
param(
  [string]$Output = (Join-Path $PSScriptRoot '..\publish\RustDroper')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\RustDroper.App\RustDroper.App.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o $Output --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish basarisiz ($LASTEXITCODE)" }
$exe = Join-Path $Output 'RustDroper.exe'
"{0}  ({1:N1} MB)" -f (Resolve-Path $exe), ((Get-Item $exe).Length / 1MB)
