<#
 Rust Droper - gercek uygulamanin ekran goruntuleri (PrintWindow + UI Automation). Kendi baslattigi sureci kapatir.
 GERCEK veriye dokunmaz: gecici RUSTDROPER_DATA klasoru kullanir (oturum yok -> kartlar "Giris yapilmadi" gosterir).
 Kullanim: powershell -File build\ui-screens.ps1 -Exe <RustDroper.exe> -OutDir <klasor> [-Language tr|en]
 Cikti: 10-gercek-twitch-<dil>.png, 11-gercek-gecis-<dil>.png (gecis ortasi), 12-gercek-kick-<dil>.png, 13-gercek-ayarlar-<dil>.png,
        14-gercek-hakkinda-<dil>.png, 15-gercek-saglik-<dil>.png, 16-gercek-guncelleme-notlari-<dil>.png
 -Language: arayuz dili; gecici veri klasorune settings.json yazilir ("Dil / Language" ayariyla ayni deger).
#>
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [Parameter(Mandatory = $true)][string]$OutDir,
  [int]$Width = 1000,
  [int]$Height = 1250,
  [ValidateSet('tr', 'en')][string]$Language = 'tr'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Cap {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[void][Win32Cap]::SetProcessDPIAware()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$data = Join-Path ([IO.Path]::GetTempPath()) ('rd-ui-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $data | Out-Null
$env:RUSTDROPER_DATA = $data
# Dil: gecici veri klasorune ayar dosyasi (gercek veriye dokunulmaz). Alan adlari AppSettings ile ayni (camelCase).
$settingsJson = '{ "schemaVersion": 1, "data": { "language": "' + $Language + '", "autoClaim": true, "autoClaimDefaultMigrated": true } }'
[IO.File]::WriteAllText((Join-Path $data 'settings.json'), $settingsJson, (New-Object System.Text.UTF8Encoding($false))) # BOM'suz: JSON okuyucu BOM'u reddeder

function Get-Window([int]$procId) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
  $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}
function Find-ById($w, [string]$id) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Invoke-Element($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Save-Shot($w, [string]$name) {
  $h = [IntPtr]$w.Current.NativeWindowHandle
  $r = New-Object Win32Cap+RECT
  [void][Win32Cap]::GetWindowRect($h, [ref]$r)
  $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [void][Win32Cap]::PrintWindow($h, $hdc, 2)
  $g.ReleaseHdc($hdc); $g.Dispose()
  $path = Join-Path $OutDir $name
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Write-Output $path
}

$proc = Start-Process -FilePath $Exe -ArgumentList '--no-browser' -PassThru
try {
  $w = $null
  for ($i = 0; $i -lt 40 -and -not $w; $i++) { Start-Sleep -Milliseconds 500; $w = Get-Window $proc.Id }
  if (-not $w) { throw 'Pencere bulunamadi' }
  $w.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize($Width, $Height)
  Start-Sleep -Seconds 4
  Save-Shot $w "10-gercek-twitch-$Language.png"
  Invoke-Element (Find-ById $w 'PlatformToggleKick')
  Start-Sleep -Milliseconds 60
  Save-Shot $w "11-gercek-gecis-$Language.png"
  Start-Sleep -Milliseconds 900
  Save-Shot $w "12-gercek-kick-$Language.png"
  Invoke-Element (Find-ById $w 'SettingsButton')
  Start-Sleep -Milliseconds 700
  Save-Shot $w "13-gercek-ayarlar-$Language.png"
  Invoke-Element (Find-ById $w 'AboutButton')
  Start-Sleep -Milliseconds 700
  Save-Shot $w "14-gercek-hakkinda-$Language.png"
  Invoke-Element (Find-ById $w 'HealthButton')
  Start-Sleep -Milliseconds 900
  Save-Shot $w "15-gercek-saglik-$Language.png"
  Invoke-Element (Find-ById $w 'ReleaseNotesButton')
  Start-Sleep -Milliseconds 900
  Save-Shot $w "16-gercek-guncelleme-notlari-$Language.png"
}
finally {
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
