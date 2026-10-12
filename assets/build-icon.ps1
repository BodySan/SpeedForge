# Generates the PNG/ICO from the same vector geometry as SpeedForge.svg. Windows WPF, no extra packages.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
function Color([string]$hex) { [Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Gradient([string]$first,[string]$last) {
    $brush=[Windows.Media.LinearGradientBrush]::new((Color $first),(Color $last),[Windows.Point]::new(0,0),[Windows.Point]::new(1,1))
    $brush.Freeze(); return $brush
}
function Solid([string]$hex) { $brush=[Windows.Media.SolidColorBrush]::new((Color $hex));$brush.Freeze();return $brush }
function Pen($brush) {
    $pen=[Windows.Media.Pen]::new($brush,50)
    $pen.StartLineCap=$pen.EndLineCap=[Windows.Media.PenLineCap]::Round
    $pen.LineJoin=[Windows.Media.PenLineJoin]::Round
    return $pen
}
function Frame([int]$size) {
    $visual=[Windows.Media.DrawingVisual]::new();$dc=$visual.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($size/512.0,$size/512.0))
    $dc.DrawRoundedRectangle((Gradient '#1B2C43' '#0B121D'),$null,[Windows.Rect]::new(16,16,480,480),104,104)
    $dc.PushTransform([Windows.Media.TranslateTransform]::new(0,-27))
    $dc.DrawGeometry($null,(Pen (Gradient '#86F2DD' '#39BBA6')),[Windows.Media.Geometry]::Parse('M350 143 C322 119 291 107 252 107 H226 C171 107 135 142 135 190 C135 234 164 257 211 270 L293 291 C341 304 371 326 371 365 C371 411 334 439 282 439 H258'))
    $dc.Pop()
    $dc.DrawGeometry((Solid '#86F2DD'),$null,[Windows.Media.Geometry]::Parse('M355 102 L369 163 L308 149 Z'))
    $dc.DrawGeometry($null,(Pen (Solid '#A497FF')),[Windows.Media.Geometry]::Parse('M260 412 H230 C194 412 163 399 138 373'))
    $dc.DrawGeometry((Solid '#A497FF'),$null,[Windows.Media.Geometry]::Parse('M123 354 L183 367 L139 410 Z'))
    $dc.Pop();$dc.Close()
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual);$bitmap.Freeze();return $bitmap
}
function PngBytes($bitmap) {
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.MemoryStream]::new();$encoder.Save($stream);$bytes=$stream.ToArray();$stream.Dispose();return ,$bytes
}
$sizes=@(16,20,24,32,40,48,64,96,128,256)
$frames=@();foreach($size in $sizes){$frames+=,(PngBytes (Frame $size))}
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'SpeedForge.png'),(PngBytes (Frame 512)))
$file=[IO.File]::Open((Join-Path $PSScriptRoot 'SpeedForge.ico'),[IO.FileMode]::Create)
$writer=[IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++){
        $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
        $offset+=$frames[$i].Length
    }
    foreach($bytes in $frames){$writer.Write([byte[]]$bytes)}
}finally{$writer.Dispose()}
$sheet=[Windows.Media.DrawingVisual]::new();$dc=$sheet.RenderOpen()
$dc.DrawRectangle((Solid '#0B121D'),$null,[Windows.Rect]::new(0,0,800,360))
$dc.DrawImage((Frame 256),[Windows.Rect]::new(24,40,256,256))
$position=320
foreach($size in @(16,24,32,48,64,96)){$dc.DrawImage((Frame $size),[Windows.Rect]::new($position,128-$size/2,$size,$size));$position+=$size+16}
$dc.Close();$bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(800,360,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($sheet)
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'Icon-preview.png'),(PngBytes $bitmap))
Write-Output ('ICO: '+$sizes.Count+' размеров; SVG, PNG и предпросмотр сохранены в '+$PSScriptRoot)
