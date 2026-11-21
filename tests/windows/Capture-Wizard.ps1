<#
  Opens the installer's window and saves a picture of each page, so the screens can be
  looked at without a Windows desktop to hand. It presses Next with the keyboard and
  never reaches Install, so nothing on the machine is changed.
#>
param([Parameter(Mandatory)] [string] $Setup, [string] $Out = "wizard", [int] $Pages = 8)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null
$process = Start-Process (Resolve-Path $Setup).Path -PassThru
Start-Sleep -Seconds 6
$shell = New-Object -ComObject WScript.Shell

for ($i = 1; $i -le $Pages; $i++) {
    $process.Refresh()
    if ($process.HasExited) { Write-Host "The installer closed after $($i - 1) page(s)."; break }
    $shell.AppActivate($process.Id) | Out-Null
    Start-Sleep -Milliseconds 700
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $Out ("page-{0:00}.png" -f $i)), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Host "Saved page $i ($($process.MainWindowTitle))"
    [System.Windows.Forms.SendKeys]::SendWait("%n")   # Alt+N: Next
    Start-Sleep -Seconds 3
}

if (-not $process.HasExited) { $process.Kill() }
