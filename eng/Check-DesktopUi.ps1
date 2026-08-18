[CmdletBinding()]
param(
    [string]$DesktopExe,
    [Parameter(Mandatory = $true)]
    [string]$ServerAddress,
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,
    [string]$OutputRoot,
    [switch]$KeepProcess
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repositoryRoot 'artifacts\test-results\desktop-ui'
$runRoot = Join-Path $testRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
$DesktopExe = if ([string]::IsNullOrWhiteSpace($DesktopExe)) {
    Join-Path $repositoryRoot 'artifacts\bin\Servitium.Desktop\x64\Release\net10.0-windows10.0.26100.0\win-x64\Servitium.Desktop.exe'
} else {
    [System.IO.Path]::GetFullPath($DesktopExe)
}
$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $runRoot } else { [System.IO.Path]::GetFullPath($OutputRoot) }
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)

function Assert-UnderRoot([string]$path, [string]$root, [string]$description) {
    $fullPath = [System.IO.Path]::GetFullPath($path).TrimEnd('\')
    $fullRoot = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
    if (-not $fullPath.StartsWith($fullRoot + '\', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($fullPath, $fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$description must stay under $fullRoot. Actual: $fullPath"
    }
}

if (-not (Test-Path -LiteralPath $DesktopExe -PathType Leaf)) {
    throw "Desktop executable was not found: $DesktopExe"
}
Assert-UnderRoot $DataRoot (Join-Path $repositoryRoot 'artifacts\test-runs') 'DataRoot'
Assert-UnderRoot $OutputRoot $testRoot 'OutputRoot'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ServitiumDesktopUiNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
}
'@

$treeScopeChildren = [System.Windows.Automation.TreeScope]::Children
$treeScopeDescendants = [System.Windows.Automation.TreeScope]::Descendants
$trueCondition = [System.Windows.Automation.Condition]::TrueCondition
$controlTypes = @(
    'ControlType.Button',
    'ControlType.ComboBox',
    'ControlType.Edit',
    'ControlType.Slider',
    'ControlType.TabItem',
    'ControlType.ListItem'
)

function Get-DesktopWindow([int]$processId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($element in $root.FindAll($treeScopeChildren, $trueCondition)) {
        try {
            if ($element.Current.ProcessId -eq $processId -and
                $element.Current.ControlType.ProgrammaticName -eq 'ControlType.Window') {
                return $element
            }
        } catch {
        }
    }

    return $null
}

function Wait-DesktopWindow([int]$processId, [int]$timeoutMilliseconds = 15000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMilliseconds)
    do {
        $window = Get-DesktopWindow $processId
        if ($null -ne $window) {
            return $window
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "WinUI Desktop window was not found for PID $processId."
}

function Get-WindowRectangle([IntPtr]$handle) {
    $native = [ServitiumDesktopUiNative+RECT]::new()
    if (-not [ServitiumDesktopUiNative]::GetWindowRect($handle, [ref]$native)) {
        throw "GetWindowRect failed for $handle."
    }

    return [pscustomobject]@{
        Left = $native.Left
        Top = $native.Top
        Right = $native.Right
        Bottom = $native.Bottom
        Width = $native.Right - $native.Left
        Height = $native.Bottom - $native.Top
    }
}

function Get-ElementRectangle($element) {
    $rect = $element.Current.BoundingRectangle
    return [pscustomobject]@{
        Left = [double]$rect.Left
        Top = [double]$rect.Top
        Right = [double]$rect.Right
        Bottom = [double]$rect.Bottom
        Width = [double]$rect.Width
        Height = [double]$rect.Height
    }
}

function Get-InteractiveElements($window) {
    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($element in $window.FindAll($treeScopeDescendants, $trueCondition)) {
        try {
            $typeName = $element.Current.ControlType.ProgrammaticName
            $rect = $element.Current.BoundingRectangle
            if ($controlTypes -contains $typeName -and
                -not $element.Current.IsOffscreen -and
                $rect.Width -gt 0 -and
                $rect.Height -gt 0) {
                $result.Add($element)
            }
        } catch {
        }
    }

    return @($result)
}

function Invoke-Element($element) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Set-ElementValue($element, [string]$value) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($value)
}

function Wait-WindowWidth($window, [int]$expectedWidth, [int]$timeoutMilliseconds = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMilliseconds)
    do {
        $actual = Get-ElementRectangle $window
        if ([Math]::Abs($actual.Width - $expectedWidth) -le 8) {
            return $actual
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Window did not resize to $expectedWidth pixels. Actual: $($actual.Width)."
}

function Save-WindowScreenshot($window, [string]$path) {
    $windowRect = Get-WindowRectangle ([IntPtr]$window.Current.NativeWindowHandle)
    $bitmap = [System.Drawing.Bitmap]::new($windowRect.Width, $windowRect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $windowRect.Left,
            $windowRect.Top,
            0,
            0,
            [System.Drawing.Size]::new($windowRect.Width, $windowRect.Height))

        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Get-OverlapArea($left, $right) {
    $width = [Math]::Max(0, [Math]::Min($left.Right, $right.Right) - [Math]::Max($left.Left, $right.Left))
    $height = [Math]::Max(0, [Math]::Min($left.Bottom, $right.Bottom) - [Math]::Max($left.Top, $right.Top))
    return $width * $height
}

function Assert-WindowLayout($window, [string]$label) {
    $windowRect = Get-ElementRectangle $window
    $elements = @(Get-InteractiveElements $window)
    if ($elements.Count -eq 0) {
        throw "$label has no visible interactive controls."
    }

    $visibleNames = [System.Collections.Generic.List[string]]::new()
    foreach ($element in $elements) {
        $name = $element.Current.Name
        $rect = Get-ElementRectangle $element
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $visibleNames.Add($name)
        }

        if ($rect.Left -lt $windowRect.Left - 2 -or $rect.Top -lt $windowRect.Top - 2 -or
            $rect.Right -gt $windowRect.Right + 2 -or $rect.Bottom -gt $windowRect.Bottom + 2) {
            throw "$label control '$name' is outside the window bounds: $rect vs $windowRect."
        }

        $type = $element.Current.ControlType.ProgrammaticName
        $automationId = [string]$element.Current.AutomationId
        if ($type -in @('ControlType.Edit', 'ControlType.ComboBox') -and
            -not $automationId.StartsWith('shell:', [System.StringComparison]::OrdinalIgnoreCase) -and
            $rect.Width -lt 280) {
            throw "$label input '$name' is only $($rect.Width) pixels wide."
        }

        if ($type -eq 'ControlType.Button' -and $rect.Height -lt 24) {
            throw "$label button '$name' is only $($rect.Height) pixels high."
        }

        if ($name -match '^(?i:true|false|zh-hans|en-us)$' -or $name -in @('Owner 账户', 'Server 地址')) {
            throw "$label exposes an internal or inconsistent value: '$name'."
        }
    }

    $overlapElements = @($elements | Where-Object {
        -not ([string]$_.Current.AutomationId).StartsWith('shell:', [System.StringComparison]::OrdinalIgnoreCase) -and
        $_.Current.Name -notmatch '^(关闭|Close|最小化|Minimize|最大化|Maximize)$'
    })
    for ($leftIndex = 0; $leftIndex -lt $overlapElements.Count; $leftIndex++) {
        $leftRect = Get-ElementRectangle $overlapElements[$leftIndex]
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $overlapElements.Count; $rightIndex++) {
            $rightRect = Get-ElementRectangle $overlapElements[$rightIndex]
            $overlap = Get-OverlapArea $leftRect $rightRect
            $minimumArea = [Math]::Min($leftRect.Width * $leftRect.Height, $rightRect.Width * $rightRect.Height)
            if ($minimumArea -gt 0 -and $overlap -gt ($minimumArea * 0.2)) {
                $leftName = $overlapElements[$leftIndex].Current.Name
                $rightName = $overlapElements[$rightIndex].Current.Name
                throw "$label interactive controls overlap: '$leftName' and '$rightName'."
            }
        }
    }

    $focusTarget = $elements | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit' } | Select-Object -First 1
    if ($null -ne $focusTarget) {
        $focusTarget.SetFocus()
        if (-not $focusTarget.Current.HasKeyboardFocus) {
            throw "$label could not move keyboard focus to '$($focusTarget.Current.Name)'."
        }
    }

    return [pscustomobject]@{
        Label = $label
        Width = $windowRect.Width
        Height = $windowRect.Height
        VisibleControls = $elements.Count
        VisibleNames = @($visibleNames)
    }
}

function Resize-AndCheck($process, [string]$label, [int]$width, [int]$height) {
    $window = Wait-DesktopWindow $process.Id
    $handle = [IntPtr]$window.Current.NativeWindowHandle
    $dpi = [ServitiumDesktopUiNative]::GetDpiForWindow($handle)
    $physicalWidth = [Math]::Round($width * $dpi / 96)
    $physicalHeight = [Math]::Round($height * $dpi / 96)
    [void][ServitiumDesktopUiNative]::ShowWindow($handle, 9)
    if (-not [ServitiumDesktopUiNative]::SetWindowPos($handle, [IntPtr]::Zero, 40, 40, $physicalWidth, $physicalHeight, 0x0040)) {
        throw "SetWindowPos failed for $label."
    }

    [void][ServitiumDesktopUiNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 250
    $window = Wait-DesktopWindow $process.Id
    $actual = Wait-WindowWidth $window $physicalWidth
    $result = Assert-WindowLayout $window $label
    $screenshot = Join-Path $OutputRoot ($label + '.png')
    Save-WindowScreenshot $window $screenshot
    return [pscustomobject]@{
        Label = $label
        RequestedEffectiveWidth = $width
        RequestedEffectiveHeight = $height
        Dpi = $dpi
        PhysicalWidth = $actual.Width
        PhysicalHeight = $actual.Height
        Screenshot = $screenshot
        VisibleControls = $result.VisibleControls
    }
}

$environment = @{
    SERVITIUM_DESKTOP_TEST_DATA_ROOT = $DataRoot
    DOTNET_ENVIRONMENT = 'Development'
}
$process = Start-Process -FilePath $DesktopExe -ArgumentList @('--server', $ServerAddress, '--isolated-test', '--ui-layout-test') -WorkingDirectory (Split-Path $DesktopExe) -Environment $environment -WindowStyle Normal -PassThru
$results = [System.Collections.Generic.List[object]]::new()
try {
    $window = Wait-DesktopWindow $process.Id
    foreach ($profile in @(
        [pscustomobject]@{ Label = 'compact'; Width = 560; Height = 640 },
        [pscustomobject]@{ Label = 'normal'; Width = 960; Height = 720 },
        [pscustomobject]@{ Label = 'wide'; Width = 1400; Height = 800 }
    )) {
        $results.Add((Resize-AndCheck $process $profile.Label $profile.Width $profile.Height))
        if ($profile.Label -eq 'compact') {
            $window = Wait-DesktopWindow $process.Id
            $openPane = @(Get-InteractiveElements $window | Where-Object {
                $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and
                $_.Current.Name -match '打开导航|Open navigation'
            } | Select-Object -First 1)
            if ($openPane.Count -eq 0) {
                throw 'Compact layout did not expose a navigation pane toggle.'
            }

            Invoke-Element $openPane[0]
            Start-Sleep -Milliseconds 300
            $window = Wait-DesktopWindow $process.Id
            $paneControls = @(Get-InteractiveElements $window | Where-Object {
                $_.Current.AutomationId -in @('shell:server-profile', 'shell:server-address', 'shell:language', 'shell:theme')
            })
            foreach ($control in $paneControls) {
                if ((Get-ElementRectangle $control).Width -lt 200) {
                    throw "Expanded compact navigation control '$($control.Current.Name)' is too narrow."
                }
            }
        }
    }

    # 以隔离的未初始化 Server 验证核心主操作可被 UIA 调用，并且空输入仍被拒绝。
    $window = Wait-DesktopWindow $process.Id
    $setupButton = @(Get-InteractiveElements $window | Where-Object {
        $_.Current.AutomationId -eq 'PG-001:action:complete-setup'
    } | Select-Object -First 1)
    if ($setupButton.Count -eq 0 -or -not $setupButton[0].Current.IsEnabled) {
        throw 'The setup action is not exposed as an enabled UI control.'
    }

    Invoke-Element $setupButton[0]
    Start-Sleep -Milliseconds 500
    $state = Invoke-RestMethod -Uri ($ServerAddress.TrimEnd('/') + '/api/v1/auth/state') -Method Get
    if ($state.initialized) {
        throw 'The empty setup form was accepted by the isolated Server.'
    }

    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'result.json') -Encoding UTF8
    Write-Output ($results | Format-Table Label,RequestedEffectiveWidth,Dpi,PhysicalWidth,PhysicalHeight,VisibleControls,Screenshot -AutoSize | Out-String)
    Write-Output "Desktop UI automation passed. Screenshots and result: $OutputRoot"
}
finally {
    if (-not $KeepProcess -and $null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
