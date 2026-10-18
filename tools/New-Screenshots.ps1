<#
    New-Screenshots.ps1 -- regenerates the project page's pictures (site\img) from the demo round. No server is contacted.

    - WUGrid.exe --demo --screenshots: the grid, the Reboot window (preview: sends and records nothing), a server's
      details, the error catalog, and the demo's end-of-round report (site\sample-report.html)
    - Microsoft Edge, headless: that report as a picture
    - a console window running wugrid-cli watch --demo, captured once the demo round is under way

    Windows sit off-screen while they are captured. Run it on a 100 % display scale for 1:1 pictures.

    .\tools\New-Screenshots.ps1                  pictures from builds\<Version>\ (run Publish-Build.ps1 first)
    .\tools\New-Screenshots.ps1 -Build C:\out    from another folder with WUGrid.exe and wugrid-cli.exe
#>
param([string] $Build)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Build) {
    [xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
    $version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    $Build = Join-Path $root "builds\$version"
}
$gui = Join-Path $Build 'WUGrid.exe'
$cli = Join-Path $Build 'wugrid-cli.exe'
foreach ($exe in $gui, $cli) { if (-not (Test-Path $exe)) { throw "$exe not found: run tools\Publish-Build.ps1 first" } }
$site = Join-Path $root 'site'
$img = Join-Path $site 'img'
[void](New-Item -ItemType Directory -Force $img)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class WUGridShot
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Cropped to the visible frame, square corners, as the GUI's own screenshots
    public static void Save(IntPtr hwnd, string path)
    {
        int noRound = 1;
        DwmSetWindowAttribute(hwnd, 33, ref noRound, 4);
        System.Threading.Thread.Sleep(300);
        Rect w, f;
        GetWindowRect(hwnd, out w);
        if (DwmGetWindowAttribute(hwnd, 9, out f, Marshal.SizeOf(typeof(Rect))) != 0) f = w;
        using (var full = new Bitmap(w.Right - w.Left, w.Bottom - w.Top, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, 2); } finally { g.ReleaseHdc(hdc); }
            }
            var crop = new Rectangle(f.Left - w.Left, f.Top - w.Top, f.Right - f.Left, f.Bottom - f.Top);
            using (var shot = full.Clone(crop, PixelFormat.Format24bppRgb)) shot.Save(path, ImageFormat.Png);
        }
    }
}
'@

# 0. The app icon, for the page's header and favicon
Add-Type -AssemblyName System.Drawing
$icon = New-Object System.Drawing.Icon (Join-Path $root 'assets\WUGrid.ico'), 256, 256
$bitmap = $icon.ToBitmap()
$bitmap.Save((Join-Path $img 'icon.png'), [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose(); $icon.Dispose()

# 1. The GUI's own tour
$tmp = Join-Path ([IO.Path]::GetTempPath()) "wugrid-shots-$PID"
$p = Start-Process $gui -ArgumentList '--demo', '--screenshots', "`"$tmp`"" -PassThru
if (-not $p.WaitForExit(120000)) { Stop-Process $p -Force; throw 'WUGrid.exe --screenshots did not finish within 2 min' }
Copy-Item (Join-Path $tmp '*.png') $img -Force
Copy-Item (Join-Path $tmp 'report.html') (Join-Path $site 'sample-report.html') -Force
Remove-Item $tmp -Recurse -Force

# 2. The report, rendered by Edge
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edge) {
    $page = [Uri]::new((Join-Path $site 'sample-report.html')).AbsoluteUri
    $profileDir = Join-Path ([IO.Path]::GetTempPath()) "wugrid-edge-$PID"
    $e = Start-Process $edge -ArgumentList '--headless=new', '--disable-gpu', '--hide-scrollbars', "--user-data-dir=`"$profileDir`"",
                                           '--window-size=1280,1000', "--screenshot=`"$(Join-Path $img 'report.png')`"", $page -PassThru
    [void]$e.WaitForExit(60000)
    Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
} else { Write-Warning 'Microsoft Edge not found: report.png not refreshed' }

# 3. The console grid: a classic console window, moved off-screen, captured ~27 min into the demo round (30x)
$c = Start-Process conhost.exe -ArgumentList "cmd.exe /c mode con cols=124 lines=38 && `"$cli`" watch --demo" -PassThru
try {
    # The console window is reported under the process attached to it (cmd), not under conhost
    $window = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds(15)
    while ($window -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $shell = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($c.Id)" | Select-Object -First 1
        if ($shell) { $window = (Get-Process -Id $shell.ProcessId -ErrorAction SilentlyContinue).MainWindowHandle }
        if ($null -eq $window) { $window = [IntPtr]::Zero }
    }
    if ($window -eq [IntPtr]::Zero) { throw 'console window not found' }
    Start-Sleep -Seconds 2   # mode con resizes the window
    [void][WUGridShot]::SetWindowPos($window, [IntPtr]::Zero, -12000, -12000, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010)
    Start-Sleep -Seconds 52
    [WUGridShot]::Save($window, (Join-Path $img 'console.png'))
} finally {
    Get-CimInstance Win32_Process -Filter "Name='wugrid-cli.exe'" | Where-Object { $_.ExecutablePath -eq $cli } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process $c -Force -ErrorAction SilentlyContinue
}

Get-ChildItem $img -Filter *.png | ForEach-Object {
    Add-Type -AssemblyName System.Drawing
    $i = [Drawing.Image]::FromFile($_.FullName)
    "site\img\$($_.Name)  $($i.Width)x$($i.Height)"
    $i.Dispose()
}
