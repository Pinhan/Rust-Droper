<#
 Rust Droper - degisiklik gunlugu uretici: src\RustDroper.App\Resources\changelog.json -> CHANGELOG.md (Ingilizce) + CHANGELOG.tr.md (Turkce).
 changelog.json TEK kaynaktir (uygulamadaki "Guncelleme notlari" sayfasi da bunu okur); markdown dosyalari elle duzenlenmez.
 Kullanim: powershell -File build\changelog.ps1           (dosyalari yeniden uretir)
           powershell -File build\changelog.ps1 -Check    (yazmaz; guncel degilse hata kodu 1)
 Kurallar: docs\08-surum-ve-degisiklik-kaydi.md
#>
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$cli = Join-Path $PSScriptRoot '..\src\RustDroper.Cli\RustDroper.Cli.csproj'
dotnet build $cli -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "derleme basarisiz ($LASTEXITCODE)" }
$dll = Join-Path $PSScriptRoot '..\src\RustDroper.Cli\bin\Release\net10.0\RustDroper.Cli.dll'
$cliArgs = @('changelog'); if ($Check) { $cliArgs += '--check' }
dotnet $dll @cliArgs
exit $LASTEXITCODE
