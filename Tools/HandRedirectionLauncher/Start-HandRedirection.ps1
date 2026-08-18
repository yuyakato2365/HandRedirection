Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$configPath = Join-Path $PSScriptRoot "launcher.config.json"
$launcherConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$bridgePidPath = Join-Path $projectRoot "Temp\tracker_bridge4.pid"
$bridgeLogDirectory = Join-Path $projectRoot "Logs\TrackerBridge"
$quickControlPath = Join-Path $projectRoot "Tools\SpatialAnchorCalibration\pc_anchor_quick_control.ps1"

function Resolve-LauncherPath {
    param([string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) {
        return $Path
    }
    return [IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}

$trackerBridgeDirectory = Resolve-LauncherPath $launcherConfig.trackerBridgeDirectory
$trackerBridgePython = Resolve-LauncherPath $launcherConfig.trackerBridgePython
$trackerBridgeScript = Resolve-LauncherPath $launcherConfig.trackerBridgeScript
$trackerBridgeConfig = Resolve-LauncherPath $launcherConfig.trackerBridgeConfig

function Get-BridgeProcess {
    if (-not (Test-Path -LiteralPath $bridgePidPath)) {
        return $null
    }

    $pidText = (Get-Content -LiteralPath $bridgePidPath -Raw -ErrorAction SilentlyContinue).Trim()
    $bridgePid = 0
    if (-not [int]::TryParse($pidText, [ref]$bridgePid)) {
        return $null
    }

    return Get-Process -Id $bridgePid -ErrorAction SilentlyContinue
}

function Start-SteamVr {
    if (Get-Process vrserver,vrmonitor -ErrorAction SilentlyContinue) {
        return
    }

    if (-not (Test-Path -LiteralPath $launcherConfig.steamVrMonitorPath)) {
        throw "SteamVR was not found: $($launcherConfig.steamVrMonitorPath)"
    }

    Start-Process -FilePath $launcherConfig.steamVrMonitorPath
}

function Start-TrackerBridge {
    param([string]$TargetIp)

    if (Get-BridgeProcess) {
        return
    }

    foreach ($requiredPath in @(
        $trackerBridgePython,
        $trackerBridgeScript,
        $trackerBridgeConfig
    )) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Required tracker bridge file was not found: $requiredPath"
        }
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $bridgePidPath) -Force | Out-Null
    New-Item -ItemType Directory -Path $bridgeLogDirectory -Force | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $stdoutPath = Join-Path $bridgeLogDirectory "tracker_bridge4-$stamp.log"
    $stderrPath = Join-Path $bridgeLogDirectory "tracker_bridge4-$stamp.error.log"
    $arguments = @(
        ('"{0}"' -f $trackerBridgeScript),
        "--config",
        ('"{0}"' -f $trackerBridgeConfig),
        "--quest-ip",
        $TargetIp,
        "--wait-for-openvr",
        "60"
    )
    $startInfo = @{
        FilePath = $trackerBridgePython
        ArgumentList = $arguments
        WorkingDirectory = $trackerBridgeDirectory
        RedirectStandardOutput = $stdoutPath
        RedirectStandardError = $stderrPath
        WindowStyle = "Hidden"
        PassThru = $true
    }
    $process = Start-Process @startInfo
    Set-Content -LiteralPath $bridgePidPath -Value $process.Id -Encoding ascii
}

function Stop-TrackerBridge {
    $process = Get-BridgeProcess
    if ($null -ne $process) {
        Stop-Process -Id $process.Id
    }
    if (Test-Path -LiteralPath $bridgePidPath) {
        Remove-Item -LiteralPath $bridgePidPath -Force
    }
}

function Open-AnchorControl {
    param([string]$TargetIp)

    if (-not (Test-Path -LiteralPath $quickControlPath)) {
        throw "Spatial Anchor Control was not found: $quickControlPath"
    }

    $startInfo = @{
        FilePath = "powershell.exe"
        ArgumentList = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", ('"{0}"' -f $quickControlPath),
            "-TargetIp", $TargetIp
        )
        WindowStyle = "Normal"
    }
    Start-Process @startInfo
}

function Get-SelectedTargetIp {
    if ($modeBox.SelectedItem -eq "Quest Link / Unity Editor") {
        return "127.0.0.1"
    }
    return $questIpBox.Text.Trim()
}

function Update-Status {
    $steamRunning = $null -ne (Get-Process vrserver,vrmonitor -ErrorAction SilentlyContinue)
    $bridgeRunning = $null -ne (Get-BridgeProcess)
    $steamStatus.Text = if ($steamRunning) { "SteamVR: RUNNING" } else { "SteamVR: STOPPED" }
    $steamStatus.ForeColor = if ($steamRunning) { [Drawing.Color]::DarkGreen } else { [Drawing.Color]::DarkRed }
    $bridgeStatus.Text = if ($bridgeRunning) { "Tracker Bridge: RUNNING" } else { "Tracker Bridge: STOPPED" }
    $bridgeStatus.ForeColor = if ($bridgeRunning) { [Drawing.Color]::DarkGreen } else { [Drawing.Color]::DarkRed }
}

$form = [Windows.Forms.Form]::new()
$form.Text = "Hand Redirection Launcher"
$form.Size = [Drawing.Size]::new(560, 420)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false

$title = [Windows.Forms.Label]::new()
$title.Text = "Hand Redirection Launcher"
$title.Font = [Drawing.Font]::new("Segoe UI", 15, [Drawing.FontStyle]::Bold)
$title.Location = [Drawing.Point]::new(18, 16)
$title.Size = [Drawing.Size]::new(500, 32)
$form.Controls.Add($title)

$modeLabel = [Windows.Forms.Label]::new()
$modeLabel.Text = "Run mode"
$modeLabel.Location = [Drawing.Point]::new(18, 62)
$modeLabel.Size = [Drawing.Size]::new(100, 24)
$form.Controls.Add($modeLabel)

$modeBox = [Windows.Forms.ComboBox]::new()
$modeBox.DropDownStyle = "DropDownList"
[void]$modeBox.Items.Add("Quest Link / Unity Editor")
[void]$modeBox.Items.Add("Standalone Quest")
$modeBox.Location = [Drawing.Point]::new(128, 60)
$modeBox.Size = [Drawing.Size]::new(390, 26)
$modeBox.SelectedItem = if ($launcherConfig.defaultMode) { [string]$launcherConfig.defaultMode } else { "Quest Link / Unity Editor" }
$form.Controls.Add($modeBox)

$questIpLabel = [Windows.Forms.Label]::new()
$questIpLabel.Text = "Quest IP"
$questIpLabel.Location = [Drawing.Point]::new(18, 100)
$questIpLabel.Size = [Drawing.Size]::new(100, 24)
$form.Controls.Add($questIpLabel)

$questIpBox = [Windows.Forms.TextBox]::new()
$questIpBox.Text = [string]$launcherConfig.standaloneQuestIp
$questIpBox.Location = [Drawing.Point]::new(128, 98)
$questIpBox.Size = [Drawing.Size]::new(390, 24)
$form.Controls.Add($questIpBox)

$startAllButton = [Windows.Forms.Button]::new()
$startAllButton.Text = "1. Start SteamVR + Tracker Bridge + Anchor Control"
$startAllButton.Location = [Drawing.Point]::new(18, 142)
$startAllButton.Size = [Drawing.Size]::new(500, 46)
$startAllButton.Add_Click({
    try {
        $targetIp = Get-SelectedTargetIp
        if ([string]::IsNullOrWhiteSpace($targetIp)) {
            throw "Quest IP is empty."
        }
        Start-SteamVr
        Start-TrackerBridge $targetIp
        Open-AnchorControl $targetIp
        $messageLabel.Text = "Started. Wait for SteamVR devices, then press Play in Unity."
        $messageLabel.ForeColor = [Drawing.Color]::DarkGreen
        Update-Status
    }
    catch {
        $messageLabel.Text = $_.Exception.Message
        $messageLabel.ForeColor = [Drawing.Color]::DarkRed
    }
})
$form.Controls.Add($startAllButton)

$anchorButton = [Windows.Forms.Button]::new()
$anchorButton.Text = "Open Anchor Control Only"
$anchorButton.Location = [Drawing.Point]::new(18, 200)
$anchorButton.Size = [Drawing.Size]::new(242, 34)
$anchorButton.Add_Click({
    try { Open-AnchorControl (Get-SelectedTargetIp) }
    catch { $messageLabel.Text = $_.Exception.Message; $messageLabel.ForeColor = [Drawing.Color]::DarkRed }
})
$form.Controls.Add($anchorButton)

$stopBridgeButton = [Windows.Forms.Button]::new()
$stopBridgeButton.Text = "Stop Tracker Bridge"
$stopBridgeButton.Location = [Drawing.Point]::new(276, 200)
$stopBridgeButton.Size = [Drawing.Size]::new(242, 34)
$stopBridgeButton.Add_Click({ Stop-TrackerBridge; Update-Status })
$form.Controls.Add($stopBridgeButton)

$steamStatus = [Windows.Forms.Label]::new()
$steamStatus.Location = [Drawing.Point]::new(18, 254)
$steamStatus.Size = [Drawing.Size]::new(242, 28)
$steamStatus.Font = [Drawing.Font]::new("Segoe UI", 10, [Drawing.FontStyle]::Bold)
$form.Controls.Add($steamStatus)

$bridgeStatus = [Windows.Forms.Label]::new()
$bridgeStatus.Location = [Drawing.Point]::new(276, 254)
$bridgeStatus.Size = [Drawing.Size]::new(242, 28)
$bridgeStatus.Font = [Drawing.Font]::new("Segoe UI", 10, [Drawing.FontStyle]::Bold)
$form.Controls.Add($bridgeStatus)

$messageLabel = [Windows.Forms.Label]::new()
$messageLabel.Text = "Choose a mode, press Start All, then press Play in Unity."
$messageLabel.Location = [Drawing.Point]::new(18, 296)
$messageLabel.Size = [Drawing.Size]::new(500, 48)
$form.Controls.Add($messageLabel)

$modeBox.Add_SelectedIndexChanged({
    $standalone = $modeBox.SelectedItem -eq "Standalone Quest"
    $questIpBox.Enabled = $standalone
    $questIpLabel.Enabled = $standalone
})

$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 1000
$timer.Add_Tick({ Update-Status })
$form.Add_Shown({
    $standalone = $modeBox.SelectedItem -eq "Standalone Quest"
    $questIpBox.Enabled = $standalone
    $questIpLabel.Enabled = $standalone
    Update-Status
    $timer.Start()
})
$form.Add_FormClosing({ $timer.Stop() })

[void]$form.ShowDialog()
