$Script:Base = $PSScriptRoot
$Script:App = 'C:\Program Files\AmneziaGeo'
$appFile = Join-Path $PSScriptRoot 'app.txt'
if (Test-Path $appFile) { $Script:App = (Get-Content -Raw $appFile).Trim() }
$Script:UiExe = Join-Path $Script:App 'AmneziaGeo.Windows.Ui.exe'
$Script:CliExe = Join-Path $Script:App 'amneziageo.exe'
$Script:Strings = @{}
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
if (-not $Lang) { $Lang = 'en' }

function Get-Language([string]$lang = $Lang) {
    $all = Import-PowerShellDataFile (Join-Path $Script:Base 'languages.psd1')
    if (-not $all.ContainsKey($lang)) { throw "no language in languages.psd1: $lang" }
    $all[$lang]
}

function T([string]$key) {
    if (-not $Script:Strings.ContainsKey($Lang)) {
        $map = @{}
        foreach ($file in @('Strings.resx', "Strings.$Lang.resx")) {
            $path = Join-Path $Script:Base "strings\$file"
            if (-not (Test-Path $path)) { continue }
            foreach ($d in ([xml](Get-Content -Raw -Encoding UTF8 $path)).root.data) { $map[$d.name] = [string]$d.value }
        }
        $Script:Strings[$Lang] = $map
    }
    $value = $Script:Strings[$Lang][$key]
    if ($null -eq $value) { throw "no string: $key" }
    $value
}

function Invoke-Cli { & $Script:CliExe --lang en @args }

function Get-Selected { ((@(Invoke-Cli status) -match '^selected\s' | Select-Object -First 1) -replace '^selected\s+', '').Trim() }

function Enter-Fleet([string]$partner) {
    $Script:FleetWas = ((@(Invoke-Cli settings show) -match '^multi-server\s' | Select-Object -First 1) -replace '^multi-server\s+', '').Trim()
    $Script:FleetSelected = Get-Selected
    Invoke-Cli settings set multi-server on | Out-Null
    Start-Sleep -Seconds 3
    Invoke-Cli fleet up $partner | Out-Null
    Start-Sleep -Seconds 15
    "fleet on (was $Script:FleetWas), $partner up"
}

function Exit-Fleet([string]$partner) {
    Invoke-Cli fleet down $partner | Out-Null
    Start-Sleep -Seconds 3
    if ($Script:FleetWas -ne 'on') { Invoke-Cli settings set multi-server off | Out-Null }
    Invoke-Cli select $Script:FleetSelected | Out-Null
    Start-Sleep -Seconds 3
    Restart-Tunnel
}

function Restart-Tunnel {
    $selected = Get-Selected
    Invoke-Cli down | Out-Null
    Start-Sleep -Seconds 3
    Invoke-Cli up $selected | Out-Null
    Start-Sleep -Seconds 10
    @(Invoke-Cli status) -match '^(state|selected|routing)\s'
}

function Get-UiProcess {
    $me = (Get-Process -Id $PID).SessionId
    Get-Process -Name 'AmneziaGeo.Windows.Ui' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $me } | Select-Object -First 1
}

function Get-UiWindows {
    $p = Get-UiProcess
    if (-not $p) { return @() }
    @([Win]::WindowsOf([uint32]$p.Id))
}

function Get-MainWindow {
    $best = [IntPtr]::Zero
    $area = 0
    foreach ($h in Get-UiWindows) {
        $r = [Win]::Frame($h)
        $a = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        if ($a -gt $area -and [Win]::Title($h) -eq 'AmneziaGeo') { $area = $a; $best = $h }
    }
    if ($best -eq [IntPtr]::Zero) {
        foreach ($h in Get-UiWindows) {
            $r = [Win]::Frame($h)
            $a = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
            if ($a -gt $area) { $area = $a; $best = $h }
        }
    }
    $best
}

function Get-Popups {
    $main = Get-MainWindow
    @(Get-UiWindows | Where-Object { $_ -ne $main })
}

function Show-Windows {
    foreach ($h in Get-UiWindows) {
        $f = [Win]::Frame($h)
        '{0} [{1}] "{2}" {3},{4} {5}x{6} iconic={7}' -f $h, [Win]::Class($h), [Win]::Title($h), $f.Left, $f.Top, ($f.Right - $f.Left), ($f.Bottom - $f.Top), [Win]::IsIconic($h)
    }
}

function Stop-Ui {
    $p = Get-UiProcess
    if ($p) { $p | Stop-Process -Force; 'ui stopped' } else { 'no ui' }
}

function Set-WindowBox([int]$x, [int]$y, [int]$w, [int]$h) {
    $main = Get-MainWindow
    [void][Win]::ShowWindow($main, 4)
    [void][Win]::SetWindowPos($main, [IntPtr]::Zero, $x, $y, $w, $h, 0x0010 -bor 0x0004)
}

function Reset-Ui {
    Stop-Load
    Stop-Ui | Out-Null
    Start-Sleep -Seconds 2
    Start-Process -FilePath $Script:UiExe
    for ($i = 0; $i -lt 60 -and (Get-MainWindow) -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Seconds 5
    # Первый запуск окна новой версии условий: условия принимаются, как это делает пользователь.
    $accept = Find-Element (T 'Terms_Accept') 'Button'
    if ($accept) {
        Invoke-Element $accept | Out-Null
        Start-Sleep -Seconds 3
    }
    Set-WindowBox 460 140 1000 760
    Start-Sleep -Seconds 1
}

function Get-Root([IntPtr]$hwnd = [IntPtr]::Zero) {
    if ($hwnd -eq [IntPtr]::Zero) { $hwnd = Get-MainWindow }
    $AE::FromHandle($hwnd)
}

function Get-All($root = (Get-Root)) {
    $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function Get-OfType([string]$type, $root = (Get-Root)) {
    @(Get-All $root | Where-Object { $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type" })
}

function Num([double]$v) { if ([double]::IsInfinity($v) -or [double]::IsNaN($v)) { return -1 } [int]$v }

function Show-Tree([string]$filter = '', $root = (Get-Root), [switch]$Offscreen) {
    $origin = $root.Current.BoundingRectangle
    foreach ($e in Get-All $root) {
        $c = $e.Current
        if (-not $Offscreen -and $c.IsOffscreen) { continue }
        $line = '{0,-12} "{1}" id={2} cls={3} at {4},{5} {6}x{7}{8}' -f $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $c.AutomationId, $c.ClassName, (Num ($c.BoundingRectangle.X - $origin.X)), (Num ($c.BoundingRectangle.Y - $origin.Y)), (Num $c.BoundingRectangle.Width), (Num $c.BoundingRectangle.Height), $(if (-not $c.IsEnabled) { ' disabled' } else { '' })
        if ($filter -eq '' -or $line -match $filter) { $line }
    }
}

function Find-Element([string]$name, [string]$type = '', $root = (Get-Root), [int]$index = 0) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    $found = @($root.FindAll($TS::Descendants, $cond))
    if ($type) { $found = @($found | Where-Object { $_.Current.ControlType.ProgrammaticName -eq "ControlType.$type" }) }
    if ($found.Count -le $index) { return $null }
    $found[$index]
}

function Find-Id([string]$id, $root = (Get-Root), [int]$index = 0) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $found = @($root.FindAll($TS::Descendants, $cond))
    if ($found.Count -le $index) { return $null }
    $found[$index]
}

function Get-Pattern($element, $pattern) {
    $out = $null
    if ($element.TryGetCurrentPattern($pattern, [ref]$out)) { return $out }
    $null
}

function Invoke-Element($element) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $current = $element
    for ($i = 0; $i -lt 6 -and $current; $i++) {
        $p = Get-Pattern $current ([System.Windows.Automation.InvokePattern]::Pattern)
        if ($p) { $p.Invoke(); return "invoked '$($current.Current.Name)' ($($current.Current.ControlType.ProgrammaticName))" }
        $p = Get-Pattern $current ([System.Windows.Automation.TogglePattern]::Pattern)
        if ($p) { $p.Toggle(); return "toggled '$($current.Current.Name)'" }
        $p = Get-Pattern $current ([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($p) { $p.Select(); return "selected '$($current.Current.Name)'" }
        $p = Get-Pattern $current ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($p) { $p.Expand(); return "expanded '$($current.Current.Name)'" }
        $current = $walker.GetParent($current)
    }
    throw "nothing to invoke for '$($element.Current.Name)'"
}

function Click-Name([string]$name, [string]$type = '', [int]$index = 0, $root = (Get-Root)) {
    $e = Find-Element $name $type $root $index
    if (-not $e) { throw "not found: '$name'" }
    Invoke-Element $e
}

function Click-Key([string]$key, [string]$type = '', [int]$index = 0) { Click-Name (T $key) $type $index }

function Click-Id([string]$id, [int]$index = 0, $root = (Get-Root)) {
    $e = Find-Id $id $root $index
    if (-not $e) { throw "not found id: $id" }
    Invoke-Element $e
}

function Set-Value($element, [string]$text) {
    $p = Get-Pattern $element ([System.Windows.Automation.ValuePattern]::Pattern)
    if (-not $p) { throw 'no value pattern' }
    $p.SetValue($text)
}

function Get-Value($element) { (Get-Pattern $element ([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value }

function Open-Combo($combo) {
    $ec = Get-Pattern $combo ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand()
    Start-Sleep -Milliseconds 900
    $ec
}

function T-All([string]$key) {
    $names = foreach ($Lang in (Import-PowerShellDataFile (Join-Path $Script:Base 'languages.psd1')).Keys) { T $key }
    @($names | Select-Object -Unique)
}

function Select-ComboItem($combo, [string[]]$items) {
    $ec = Open-Combo $combo
    $target = $null
    foreach ($item in $items) {
        foreach ($h in @(Get-Popups)) {
            $target = Find-Element $item '' ($AE::FromHandle($h))
            if ($target) { break }
        }
        if ($target) { break }
    }
    if (-not $target) { $ec.Collapse(); throw "item not found: $($items -join ' | ')" }
    $name = $target.Current.Name
    $si = Get-Pattern $target ([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($si) { $si.Select() } else { Invoke-Element $target | Out-Null }
    Start-Sleep -Milliseconds 300
    if ($ec.Current.ExpandCollapseState -eq 'Expanded') { $ec.Collapse() }
    "combo -> $name"
}

function Wait-Until([scriptblock]$test, [int]$seconds = 30) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        if (& $test) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "timeout: $test"
}

function Open-ListEditor([int]$index = 0) {
    Click-Id 'SettingsPart' $index | Out-Null
    Wait-Until { Find-Id 'RuleBox' } 90
    Start-Sleep -Milliseconds 1000
}

function Get-SwitchFor([string]$key) {
    $label = Find-Element (T $key)
    if (-not $label) { throw "not found: $key" }
    $y = $label.Current.BoundingRectangle.Y
    $switch = @(Get-All | Where-Object { $_.Current.ClassName -eq 'ToggleSwitch' } | Sort-Object { [math]::Abs($_.Current.BoundingRectangle.Y - $y) })[0]
    Get-Pattern $switch ([System.Windows.Automation.TogglePattern]::Pattern)
}

function Set-Switch([string]$key, [bool]$on) {
    $toggle = Get-SwitchFor $key
    $was = [string]$toggle.Current.ToggleState -eq 'On'
    if ($was -ne $on) { $toggle.Toggle(); Start-Sleep -Seconds 2 }
    $was
}

function Test-InstallerLang {
    if (Get-ChildItem (Join-Path $Script:Base 'installer') -Filter '*.exe' -ErrorAction SilentlyContinue) { return $true }
    $os = (Get-Culture).TwoLetterISOLanguageName
    $shown = if ($os -eq 'ru') { 'ru' } else { 'en' }
    if ($shown -eq $Lang) { return $true }
    "skip: this installer follows the Windows format language ($shown)"
    $false
}

function Get-InstallerWindow {
    $me = (Get-Process -Id $PID).SessionId
    $best = [IntPtr]::Zero
    $area = 0
    foreach ($p in @(Get-Process -Name 'AmneziaGeo.Windows.Installer', $Script:InstallerName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $me })) {
        foreach ($h in [Win]::WindowsOf([uint32]$p.Id)) {
            $r = [Win]::Frame($h)
            $a = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
            if ($a -gt $area) { $area = $a; $best = $h }
        }
    }
    $best
}

function Start-Installer {
    $exe = Get-ChildItem (Join-Path $Script:Base 'installer') -Filter 'AmneziaGeo-*-win-*.exe' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $exe) {
        $exe = Get-ChildItem "$env:USERPROFILE\Downloads" -Filter 'AmneziaGeo-*-win-*.exe' | Where-Object { $_.Name -like "*-$(Get-Version)-*" } | Select-Object -First 1
    }
    if (-not $exe) { throw "no installer of $(Get-Version) in Downloads" }
    "installer: $($exe.Name)"
    $Script:InstallerName = $exe.BaseName
    Start-Process -FilePath $exe.FullName
    Wait-Until { (Get-InstallerWindow) -ne [IntPtr]::Zero } 60
    Wait-Until { Find-Any (T-All 'InstallerUi_Remove') ($AE::FromHandle((Get-InstallerWindow))) } 60
    Start-Sleep -Seconds 1
}

function Stop-Installer {
    $me = (Get-Process -Id $PID).SessionId
    foreach ($name in 'AmneziaGeo.Windows.Installer', $Script:InstallerName) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $me } | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    'installer closed, left: ' + @(Get-Process -Name 'AmneziaGeo.Windows.Installer', $Script:InstallerName -ErrorAction SilentlyContinue).Count
}

function Find-Any([string[]]$names, $root = (Get-Root)) {
    foreach ($n in $names) {
        $e = Find-Element $n '' $root
        if ($e) { return $e }
    }
    $null
}

function Click-Any([string[]]$names, $root = (Get-Root)) {
    $e = Find-Any $names $root
    if (-not $e) { throw "not found: $($names -join ' | ')" }
    Invoke-Element $e
}

function Save-Window([IntPtr]$hwnd, [string]$name, [string]$Pattern = (Get-MaskPattern)) {
    $bmp = [Win]::Grab($hwnd)
    try {
        $m = @(Get-MaskRects $hwnd $Pattern)
        foreach ($x in $m) { Blur-Rect $bmp $x.Rect }
        Save-Png $bmp $name
    }
    finally { $bmp.Dispose() }
    "saved $Lang/$name.png, masked $($m.Count)"
}

function Open-Settings {
    Click-Name 'Avalonia.Controls.PathIcon' 'Button' | Out-Null
    Start-Sleep -Milliseconds 1200
}

function Open-Section([string]$key) {
    Open-Settings
    Click-Key $key | Out-Null
    Start-Sleep -Milliseconds 1500
}

function Scroll-NameTo([string]$name, [int]$targetY = 120, [string]$viewId = 'ContentScroll') {
    Wait-Until { Find-Element $name } 30
    $sp = Get-Pattern (Find-Id $viewId) ([System.Windows.Automation.ScrollPattern]::Pattern)
    if (-not $sp) { return 'no scroll pattern' }
    Wait-Until { $sp.Current.VerticallyScrollable } 10
    $top = (Get-Root).Current.BoundingRectangle.Y
    $lo = 0.0; $hi = 100.0; $mid = 0.0; $y = 0
    for ($i = 0; $i -lt 14; $i++) {
        $mid = ($lo + $hi) / 2
        $sp.SetScrollPercent(-1, $mid)
        Start-Sleep -Milliseconds 200
        $y = (Find-Element $name).Current.BoundingRectangle.Y - $top
        if ($y -gt $targetY) { $lo = $mid } else { $hi = $mid }
    }
    "scrolled to $([math]::Round($mid, 1))%, '$name' at y=$([int]$y)"
}

function Scroll-Top([string]$viewId = 'ContentScroll') {
    $sp = Get-Pattern (Find-Id $viewId) ([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($sp) { $sp.SetScrollPercent(-1, 0); 'scrolled to top' } else { 'no scroll pattern' }
}

function Get-ViewportRect($e) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $p = $walker.GetParent($e)
    while ($p) {
        if ($p.Current.ClassName -eq 'ScrollViewer') { return $p.Current.BoundingRectangle }
        $p = $walker.GetParent($p)
    }
    $null
}

function Start-Load([int]$seconds = 25) {
    $Script:Load = Start-Job -ArgumentList $seconds {
        param($s)
        $end = (Get-Date).AddSeconds($s)
        while ((Get-Date) -lt $end) {
            foreach ($u in 'https://www.youtube.com/', 'https://m.youtube.com/', 'https://i.ytimg.com/generate_204') {
                try { Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 10 | Out-Null } catch {}
            }
        }
    }
}

function Stop-Load {
    if ($Script:Load) { Stop-Job $Script:Load; Remove-Job $Script:Load -Force; $Script:Load = $null }
}

function Get-StandParts {
    # Имя пользователя - отдельным словом: в адресе bor-project.github.io оно не прячется.
    $parts = @('(?<![\w-])' + [regex]::Escape($env:USERNAME) + '(?![\w-])')
    $file = Join-Path $Script:Base 'mask.txt'
    if (-not (Test-Path $file)) { return $parts }
    foreach ($line in Get-Content -Encoding UTF8 $file) {
        $line = $line.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        if ($line -match '^host\s+(\S+)$') {
            $parts += [regex]::Escape($Matches[1])
            try { $parts += [System.Net.Dns]::GetHostAddresses($Matches[1]) | ForEach-Object { [regex]::Escape($_.IPAddressToString) } } catch {}
        }
        else { $parts += $line }
    }
    $parts
}

function Get-MaskPattern { (@(Get-StandParts) + '(?<![\d.])\d{1,3}(\.\d{1,3}){3}(?![\d.])') -join '|' }

function Get-StandPattern { (@(Get-StandParts) + 'Key\s*=') -join '|' }

function Get-Version { (Get-Item $Script:UiExe).VersionInfo.ProductVersion -replace '\+.*$', '' }

function Get-MaskRects([IntPtr]$hwnd, [string]$pattern) {
    $frame = [Win]::Frame($hwnd)
    $version = Get-Version
    $found = @()
    foreach ($e in Get-All ($AE::FromHandle($hwnd))) {
        $c = $e.Current
        if ($c.IsOffscreen) { continue }
        $text = [string]$c.Name
        $vp = Get-Pattern $e ([System.Windows.Automation.ValuePattern]::Pattern)
        if ($vp) { $text += ' ' + $vp.Current.Value }
        if ($version) { $text = $text -replace ('(?<![\d.])' + [regex]::Escape($version) + '(?![\d.])'), '' }
        if ($text -notmatch $pattern) { continue }
        $r = $c.BoundingRectangle
        if ([double]::IsInfinity($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
        $found += [pscustomobject]@{
            Type = $c.ControlType.ProgrammaticName.Replace('ControlType.', '')
            Rect = New-Object System.Drawing.Rectangle ([int]$r.X - $frame.Left - 2), ([int]$r.Y - $frame.Top - 2), ([int]$r.Width + 4), ([int]$r.Height + 4)
        }
    }
    $found
}

function Blur-Rect([System.Drawing.Bitmap]$bmp, [System.Drawing.Rectangle]$r) {
    $r = [System.Drawing.Rectangle]::Intersect($r, (New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height))
    if ($r.Width -le 0 -or $r.Height -le 0) { return }
    $sw = [Math]::Max(1, [int][Math]::Floor($r.Width / 20))
    $small = New-Object System.Drawing.Bitmap $sw, 1
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $sw, 1), $r, 'Pixel')
    $g.Dispose()
    $avg = New-Object System.Drawing.Bitmap 1, 1
    $g = [System.Drawing.Graphics]::FromImage($avg)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($small, (New-Object System.Drawing.Rectangle 0, 0, 1, 1), (New-Object System.Drawing.Rectangle 0, 0, $sw, 1), 'Pixel')
    $g.Dispose()
    $fill = $avg.GetPixel(0, 0)
    $avg.Dispose()
    $attr = New-Object System.Drawing.Imaging.ImageAttributes
    $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, $fill.R, $fill.G, $fill.B))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.FillRectangle($brush, $r)
    $g.InterpolationMode = 'HighQualityBilinear'
    $g.DrawImage($small, $r, 0, 0, $sw, 1, [System.Drawing.GraphicsUnit]::Pixel, $attr)
    $g.Dispose()
    $brush.Dispose()
    $attr.Dispose()
    $small.Dispose()
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$name) {
    $dir = Join-Path $Script:Base "out\$Lang"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $crop = $bmp.Clone((New-Object System.Drawing.Rectangle 1, 0, ($bmp.Width - 2), ($bmp.Height - 1)), $bmp.PixelFormat)
    try { [Win]::Save($crop, (Join-Path $dir "$name.png")) } finally { $crop.Dispose() }
}

function Save-Masked([string]$name, [switch]$WithPopups, [string]$Pattern = (Get-MaskPattern)) {
    $main = Get-MainWindow
    if ($main -eq [IntPtr]::Zero) { throw 'no window' }
    $canvas = [Win]::Grab($main)
    $masks = @()
    try {
        $m = @(Get-MaskRects $main $Pattern)
        foreach ($x in $m) { Blur-Rect $canvas $x.Rect }
        $masks += $m
        if ($WithPopups) {
            $base = [Win]::Frame($main)
            $g = [System.Drawing.Graphics]::FromImage($canvas)
            foreach ($p in @(Get-Popups)) {
                $part = [Win]::Grab($p)
                $pm = @(Get-MaskRects $p $Pattern)
                foreach ($x in $pm) { Blur-Rect $part $x.Rect }
                $masks += $pm
                $f = [Win]::Frame($p)
                $g.DrawImage($part, ($f.Left - $base.Left), ($f.Top - $base.Top), $part.Width, $part.Height)
                $part.Dispose()
            }
            $g.Dispose()
        }
        Save-Png $canvas $name
    }
    finally { $canvas.Dispose() }
    $big = @($masks | Where-Object { $_.Rect.Height -gt 40 })
    "saved $Lang/$name.png, masked $($masks.Count)" + $(if ($big.Count) { ", large: " + (($big | ForEach-Object { "$($_.Type) $($_.Rect.Width)x$($_.Rect.Height)" }) -join '; ') } else { '' })
}

function Save-LineMasked([string]$name, [string]$Pattern = (Get-StandPattern)) {
    $main = Get-MainWindow
    if ($main -eq [IntPtr]::Zero) { throw 'no window' }
    $frame = [Win]::Frame($main)
    $canvas = [Win]::Grab($main)
    $count = 0; $multi = 0
    try {
        foreach ($e in Get-All ($AE::FromHandle($main))) {
            $c = $e.Current
            if ($c.IsOffscreen) { continue }
            $r = $c.BoundingRectangle
            if ([double]::IsInfinity($r.X) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
            $text = [string]$c.Name
            if ($text -notmatch $Pattern) { continue }
            if ($text -notmatch "`n") {
                Blur-Rect $canvas (New-Object System.Drawing.Rectangle ([int]$r.X - $frame.Left - 2), ([int]$r.Y - $frame.Top - 2), ([int]$r.Width + 4), ([int]$r.Height + 4))
                $count++
                continue
            }
            $multi++
            $lines = $text -split "`r?`n"
            $lh = $r.Height / $lines.Count
            $vp = Get-ViewportRect $e
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -notmatch $Pattern) { continue }
                $top = $r.Y + $i * $lh
                $bottom = $top + $lh
                $left = $r.X - 2
                $right = $r.X + $r.Width + 2
                if ($vp) {
                    if ($bottom -le $vp.Y -or $top -ge ($vp.Y + $vp.Height)) { continue }
                    $top = [Math]::Max($top, $vp.Y)
                    $bottom = [Math]::Min($bottom, $vp.Y + $vp.Height)
                    $left = [Math]::Max($left, $vp.X)
                    $right = [Math]::Min($right, $vp.X + $vp.Width - 18)
                }
                Blur-Rect $canvas (New-Object System.Drawing.Rectangle ([int]$left - $frame.Left), ([int]$top - $frame.Top), ([int]($right - $left)), ([int]($bottom - $top)))
                $count++
            }
        }
        Save-Png $canvas $name
    }
    finally { $canvas.Dispose() }
    "saved $Lang/$name.png, masked $count line(s) in $multi multiline block(s)"
}
