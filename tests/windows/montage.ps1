param([string] $Out, [int] $Width = 1900, [Parameter(ValueFromRemainingArguments)] [string[]] $Files)
Add-Type -AssemblyName System.Drawing
$images = $Files | % { [Drawing.Image]::FromFile((Resolve-Path $_)) }
$x = 0; $y = 0; $row = 0; $pos = @()
foreach ($i in $images) { if ($x -gt 0 -and $x + $i.Width -gt $Width) { $x = 0; $y += $row + 24; $row = 0 }; $pos += ,@($x, $y); $x += $i.Width + 8; $row = [Math]::Max($row, $i.Height) }
$bmp = New-Object Drawing.Bitmap($Width, ($y + $row + 24)); $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::DimGray)
$font = New-Object Drawing.Font('Segoe UI', 9)
for ($k = 0; $k -lt $images.Count; $k++) { $g.DrawImage($images[$k], $pos[$k][0], $pos[$k][1] + 18); $g.DrawString([IO.Path]::GetFileNameWithoutExtension($Files[$k]), $font, [Drawing.Brushes]::White, $pos[$k][0], $pos[$k][1]) }
$bmp.Save($Out); $images | % Dispose
