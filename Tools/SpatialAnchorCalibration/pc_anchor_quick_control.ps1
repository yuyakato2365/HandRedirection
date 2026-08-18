param(
    [string]$TargetIp = "127.0.0.1"
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$questCommandPort = 9101
$pcStatusPort = 9102
$statusClient = $null

function Add-Log {
    param([string]$Message)
    $stamp = Get-Date -Format "HH:mm:ss"
    $logBox.AppendText("[$stamp] $Message`r`n")
    $logBox.SelectionStart = $logBox.Text.Length
    $logBox.ScrollToCaret()
}

function Send-QuestCommand {
    param([string]$Command)
    try {
        $hostName = $ipBox.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($hostName)) {
            throw "Target IP is empty."
        }
        $client = [Net.Sockets.UdpClient]::new()
        $bytes = [Text.Encoding]::UTF8.GetBytes($Command)
        [void]$client.Send($bytes, $bytes.Length, $hostName, $questCommandPort)
        $client.Close()
        Add-Log "Sent: $Command"
    }
    catch {
        Add-Log "Send failed: $($_.Exception.Message)"
    }
}

function Start-StatusListener {
    try {
        $script:statusClient = [Net.Sockets.UdpClient]::new($pcStatusPort)
        $script:statusClient.Client.Blocking = $false
        Add-Log "Listening on UDP :$pcStatusPort"
    }
    catch {
        Add-Log "Status listener unavailable: $($_.Exception.Message)"
    }
}

function Poll-Status {
    if ($null -eq $script:statusClient) { return }
    try {
        while ($script:statusClient.Available -gt 0) {
            $endpoint = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
            $bytes = $script:statusClient.Receive([ref]$endpoint)
            $message = [Text.Encoding]::UTF8.GetString($bytes).Trim()
            $statusLabel.Text = $message
            Add-Log "Received: $message"
        }
    }
    catch [Net.Sockets.SocketException] {}
    catch { Add-Log "Status receive failed: $($_.Exception.Message)" }
}

$form = [Windows.Forms.Form]::new()
$form.Text = "Spatial Anchor Quick Control"
$form.Size = [Drawing.Size]::new(540, 650)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false

$title = [Windows.Forms.Label]::new()
$title.Text = "Spatial Anchor - Quick Setup"
$title.Font = [Drawing.Font]::new("Segoe UI", 14, [Drawing.FontStyle]::Bold)
$title.Location = [Drawing.Point]::new(16, 14)
$title.Size = [Drawing.Size]::new(490, 32)
$form.Controls.Add($title)

$ipLabel = [Windows.Forms.Label]::new()
$ipLabel.Text = "Target IP"
$ipLabel.Location = [Drawing.Point]::new(16, 58)
$ipLabel.Size = [Drawing.Size]::new(90, 24)
$form.Controls.Add($ipLabel)

$ipBox = [Windows.Forms.TextBox]::new()
$ipBox.Text = $TargetIp
$ipBox.Location = [Drawing.Point]::new(112, 56)
$ipBox.Size = [Drawing.Size]::new(290, 24)
$form.Controls.Add($ipBox)

$pingButton = [Windows.Forms.Button]::new()
$pingButton.Text = "Ping"
$pingButton.Location = [Drawing.Point]::new(414, 54)
$pingButton.Size = [Drawing.Size]::new(92, 30)
$pingButton.Add_Click({ Send-QuestCommand "PING" })
$form.Controls.Add($pingButton)

$guide = [Windows.Forms.Label]::new()
$guide.Text = "Run Unity/Quest first. Complete the four steps below in order."
$guide.Location = [Drawing.Point]::new(16, 98)
$guide.Size = [Drawing.Size]::new(490, 28)
$form.Controls.Add($guide)

$beginButton = [Windows.Forms.Button]::new()
$beginButton.Text = "1. Begin Anchor Placement"
$beginButton.Location = [Drawing.Point]::new(16, 136)
$beginButton.Size = [Drawing.Size]::new(490, 42)
$beginButton.Add_Click({ Send-QuestCommand "BEGIN_ANCHOR_PLACEMENT" })
$form.Controls.Add($beginButton)

$confirmAnchorButton = [Windows.Forms.Button]::new()
$confirmAnchorButton.Text = "2. Confirm Anchor (or pinch in VR)"
$confirmAnchorButton.Location = [Drawing.Point]::new(16, 188)
$confirmAnchorButton.Size = [Drawing.Size]::new(490, 42)
$confirmAnchorButton.Add_Click({ Send-QuestCommand "CONFIRM_ANCHOR_PLACEMENT" })
$form.Controls.Add($confirmAnchorButton)

$angleLabel = [Windows.Forms.Label]::new()
$angleLabel.Text = "3. Adjust desk angle if needed"
$angleLabel.Location = [Drawing.Point]::new(16, 246)
$angleLabel.Size = [Drawing.Size]::new(240, 24)
$form.Controls.Add($angleLabel)

$minusFiveButton = [Windows.Forms.Button]::new()
$minusFiveButton.Text = "-5°"
$minusFiveButton.Location = [Drawing.Point]::new(16, 276)
$minusFiveButton.Size = [Drawing.Size]::new(112, 34)
$minusFiveButton.Add_Click({ Send-QuestCommand "ROTATE_DESK_LEFT_LARGE" })
$form.Controls.Add($minusFiveButton)

$minusOneButton = [Windows.Forms.Button]::new()
$minusOneButton.Text = "-1°"
$minusOneButton.Location = [Drawing.Point]::new(142, 276)
$minusOneButton.Size = [Drawing.Size]::new(112, 34)
$minusOneButton.Add_Click({ Send-QuestCommand "ROTATE_DESK_LEFT" })
$form.Controls.Add($minusOneButton)

$plusOneButton = [Windows.Forms.Button]::new()
$plusOneButton.Text = "+1°"
$plusOneButton.Location = [Drawing.Point]::new(268, 276)
$plusOneButton.Size = [Drawing.Size]::new(112, 34)
$plusOneButton.Add_Click({ Send-QuestCommand "ROTATE_DESK_RIGHT" })
$form.Controls.Add($plusOneButton)

$plusFiveButton = [Windows.Forms.Button]::new()
$plusFiveButton.Text = "+5°"
$plusFiveButton.Location = [Drawing.Point]::new(394, 276)
$plusFiveButton.Size = [Drawing.Size]::new(112, 34)
$plusFiveButton.Add_Click({ Send-QuestCommand "ROTATE_DESK_RIGHT_LARGE" })
$form.Controls.Add($plusFiveButton)

$finishButton = [Windows.Forms.Button]::new()
$finishButton.Text = "4. Confirm Desk + Start Spatial Anchor Mode"
$finishButton.Location = [Drawing.Point]::new(16, 326)
$finishButton.Size = [Drawing.Size]::new(490, 46)
$finishButton.Add_Click({
    Send-QuestCommand "CONFIRM_DESK_ALIGNMENT"
    [Threading.Thread]::Sleep(200)
    Send-QuestCommand "USE_SPATIAL_ANCHOR_REDIRECTION"
})
$form.Controls.Add($finishButton)

$gazeOnButton = [Windows.Forms.Button]::new()
$gazeOnButton.Text = "Gaze Debug ON"
$gazeOnButton.Location = [Drawing.Point]::new(16, 388)
$gazeOnButton.Size = [Drawing.Size]::new(238, 34)
$gazeOnButton.Add_Click({ Send-QuestCommand "ENABLE_GAZE_DEBUG_VISUALS" })
$form.Controls.Add($gazeOnButton)

$gazeOffButton = [Windows.Forms.Button]::new()
$gazeOffButton.Text = "Gaze Debug OFF"
$gazeOffButton.Location = [Drawing.Point]::new(268, 388)
$gazeOffButton.Size = [Drawing.Size]::new(238, 34)
$gazeOffButton.Add_Click({ Send-QuestCommand "DISABLE_GAZE_DEBUG_VISUALS" })
$form.Controls.Add($gazeOffButton)

$advancedButton = [Windows.Forms.Button]::new()
$advancedButton.Text = "Open Advanced Control"
$advancedButton.Location = [Drawing.Point]::new(16, 434)
$advancedButton.Size = [Drawing.Size]::new(490, 32)
$advancedButton.Add_Click({
    $advancedPath = Join-Path $PSScriptRoot "pc_anchor_control_window.ps1"
    $form.Close()
    Start-Process -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"{0}"' -f $advancedPath)
    ) -WindowStyle Normal
})
$form.Controls.Add($advancedButton)

$statusLabel = [Windows.Forms.Label]::new()
$statusLabel.Text = "Waiting for status"
$statusLabel.BorderStyle = [Windows.Forms.BorderStyle]::Fixed3D
$statusLabel.Location = [Drawing.Point]::new(16, 480)
$statusLabel.Size = [Drawing.Size]::new(490, 30)
$form.Controls.Add($statusLabel)

$logBox = [Windows.Forms.TextBox]::new()
$logBox.Multiline = $true
$logBox.ScrollBars = "Vertical"
$logBox.ReadOnly = $true
$logBox.Location = [Drawing.Point]::new(16, 522)
$logBox.Size = [Drawing.Size]::new(490, 78)
$form.Controls.Add($logBox)

$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 100
$timer.Add_Tick({ Poll-Status })
$form.Add_Shown({ Start-StatusListener; $timer.Start() })
$form.Add_FormClosing({
    $timer.Stop()
    if ($null -ne $script:statusClient) {
        $script:statusClient.Close()
        $script:statusClient = $null
    }
})

[void]$form.ShowDialog()
