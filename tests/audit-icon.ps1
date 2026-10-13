param([string]$Executable, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SpeedForgeIconProbe {
 [DllImport("shell32.dll", CharSet=CharSet.Unicode)] public static extern uint ExtractIconEx(string path, int index, IntPtr[] large, IntPtr[] small, uint count);
 [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
}
'@
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$count = [SpeedForgeIconProbe]::ExtractIconEx($Executable,-1,$null,$null,0)
$big = [IntPtr[]]::new(1); $small = [IntPtr[]]::new(1)
$extracted = [SpeedForgeIconProbe]::ExtractIconEx($Executable,0,$big,$small,1)
$frames = @()
try {
 foreach ($entry in @(@{Name='large';Handle=$big[0]},@{Name='small';Handle=$small[0]})) {
  if ($entry.Handle -eq [IntPtr]::Zero) { throw ('Windows не извлёк значок '+$entry.Name) }
  $icon = [Drawing.Icon]::FromHandle($entry.Handle)
  $bitmap = $icon.ToBitmap()
  try {
   $opaque = 0
   for ($x=0;$x -lt $bitmap.Width;$x++) { for ($y=0;$y -lt $bitmap.Height;$y++) { if ($bitmap.GetPixel($x,$y).A -gt 64) { $opaque++ } } }
   $bitmap.Save((Join-Path $OutputDirectory ($entry.Name+'.png')),[Drawing.Imaging.ImageFormat]::Png)
   if ($opaque -lt 10) { throw ('Windows извлёк пустой значок '+$entry.Name) }
   $frames += @{Name=$entry.Name;Width=$bitmap.Width;Height=$bitmap.Height;VisiblePixels=$opaque}
  } finally { $bitmap.Dispose(); $icon.Dispose() }
 }
 if ($count -lt 1 -or $extracted -lt 1) { throw "В exe нет корректной группы значков: groups=$count; extracted=$extracted" }
 @{Groups=$count;Extracted=$extracted;Frames=$frames} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'icon-check.json') -Encoding utf8
 Get-Content -LiteralPath (Join-Path $OutputDirectory 'icon-check.json')
} finally { foreach ($handle in @($big[0],$small[0])) { if ($handle -ne [IntPtr]::Zero) { [SpeedForgeIconProbe]::DestroyIcon($handle) | Out-Null } } }
