Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$QuestCommandPort = 9101
$PcStatusPort = 9102
$DefaultTargetIp = "127.0.0.1"
$statusClient = $null

function Add-Log {
    param([string]$Message)

    $stamp = Get-Date -Format "HH:mm:ss"
    $logBox.AppendText("[$stamp] $Message`r`n")
    $logBox.SelectionStart = $logBox.Text.Length
    $logBox.ScrollToCaret()
}

function Send-QuestCommand {
    param(
        [string]$HostName,
        [string]$Command
    )

    try {
        if ([string]::IsNullOrWhiteSpace($HostName)) {
            Add-Log "Target IP is empty"
            return
        }

        $client = [System.Net.Sockets.UdpClient]::new()
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Command)
        [void]$client.Send($bytes, $bytes.Length, $HostName, $QuestCommandPort)
        $client.Close()
        Add-Log "Sent: $Command"
    }
    catch {
        Add-Log "Send failed: $($_.Exception.Message)"
    }
}

function Start-StatusListener {
    try {
        $script:statusClient = [System.Net.Sockets.UdpClient]::new($PcStatusPort)
        $script:statusClient.Client.Blocking = $false
        Add-Log "Listening for status on UDP :$PcStatusPort"
    }
    catch {
        Add-Log "Status listener failed: $($_.Exception.Message)"
    }
}

function Poll-Status {
    if ($null -eq $script:statusClient) {
        return
    }

    try {
        while ($script:statusClient.Available -gt 0) {
            $endpoint = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $bytes = $script:statusClient.Receive([ref]$endpoint)
            $message = [System.Text.Encoding]::UTF8.GetString($bytes).Trim()
            $statusLabel.Text = "$($endpoint.Address): $message"
            Add-Log "Received: $message"
        }
    }
    catch [System.Net.Sockets.SocketException] {
        # Non-blocking UDP can report that no packet is ready between Available and Receive.
    }
    catch {
        Add-Log "Status receive failed: $($_.Exception.Message)"
    }
}

$form = [System.Windows.Forms.Form]::new()
$form.Text = "Spatial Anchor Calibration Control"
$form.Size = [System.Drawing.Size]::new(560, 878)
$form.StartPosition = "CenterScreen"

$ipLabel = [System.Windows.Forms.Label]::new()
$ipLabel.Text = "Target IP"
$ipLabel.Location = [System.Drawing.Point]::new(16, 18)
$ipLabel.Size = [System.Drawing.Size]::new(80, 24)
$form.Controls.Add($ipLabel)

$ipBox = [System.Windows.Forms.TextBox]::new()
$ipBox.Text = $DefaultTargetIp
$ipBox.Location = [System.Drawing.Point]::new(100, 16)
$ipBox.Size = [System.Drawing.Size]::new(420, 24)
$form.Controls.Add($ipBox)

$steps = [System.Windows.Forms.Label]::new()
$steps.Text = @"
1. Use 127.0.0.1 when running through Quest Link / Unity Editor.
2. Use the Quest headset IP only for a standalone Quest build.
3. Press Begin Anchor Placement.
4. In VR, move your hand marker to the reference point.
5. Pinch in VR, or press Confirm Anchor here to place deskOrigin at the anchor position.
6. Adjust desk angle, then press Confirm Desk Alignment.
"@
$steps.Location = [System.Drawing.Point]::new(16, 56)
$steps.Size = [System.Drawing.Size]::new(510, 130)
$form.Controls.Add($steps)

$beginButton = [System.Windows.Forms.Button]::new()
$beginButton.Text = "Begin Anchor Placement"
$beginButton.Location = [System.Drawing.Point]::new(16, 196)
$beginButton.Size = [System.Drawing.Size]::new(248, 34)
$beginButton.Add_Click({
    Send-QuestCommand $ipBox.Text "BEGIN_ANCHOR_PLACEMENT"
})
$form.Controls.Add($beginButton)

$confirmButton = [System.Windows.Forms.Button]::new()
$confirmButton.Text = "Confirm Anchor"
$confirmButton.Location = [System.Drawing.Point]::new(278, 196)
$confirmButton.Size = [System.Drawing.Size]::new(248, 34)
$confirmButton.Add_Click({
    Send-QuestCommand $ipBox.Text "CONFIRM_ANCHOR_PLACEMENT"
})
$form.Controls.Add($confirmButton)

$pingButton = [System.Windows.Forms.Button]::new()
$pingButton.Text = "Ping"
$pingButton.Location = [System.Drawing.Point]::new(16, 240)
$pingButton.Size = [System.Drawing.Size]::new(160, 34)
$pingButton.Add_Click({
    Send-QuestCommand $ipBox.Text "PING"
})
$form.Controls.Add($pingButton)

$cancelButton = [System.Windows.Forms.Button]::new()
$cancelButton.Text = "Cancel"
$cancelButton.Location = [System.Drawing.Point]::new(190, 240)
$cancelButton.Size = [System.Drawing.Size]::new(160, 34)
$cancelButton.Add_Click({
    Send-QuestCommand $ipBox.Text "CANCEL_ANCHOR_PLACEMENT"
})
$form.Controls.Add($cancelButton)

$clearButton = [System.Windows.Forms.Button]::new()
$clearButton.Text = "Clear Anchor"
$clearButton.Location = [System.Drawing.Point]::new(364, 240)
$clearButton.Size = [System.Drawing.Size]::new(162, 34)
$clearButton.Add_Click({
    Send-QuestCommand $ipBox.Text "CLEAR_ANCHOR"
})
$form.Controls.Add($clearButton)

$loadSavedButton = [System.Windows.Forms.Button]::new()
$loadSavedButton.Text = "Load Saved Anchor"
$loadSavedButton.Location = [System.Drawing.Point]::new(16, 284)
$loadSavedButton.Size = [System.Drawing.Size]::new(248, 34)
$loadSavedButton.Add_Click({
    Send-QuestCommand $ipBox.Text "LOAD_SAVED_ANCHOR"
})
$form.Controls.Add($loadSavedButton)

$clearSavedButton = [System.Windows.Forms.Button]::new()
$clearSavedButton.Text = "Clear Saved Anchor"
$clearSavedButton.Location = [System.Drawing.Point]::new(278, 284)
$clearSavedButton.Size = [System.Drawing.Size]::new(248, 34)
$clearSavedButton.Add_Click({
    Send-QuestCommand $ipBox.Text "CLEAR_SAVED_ANCHOR"
})
$form.Controls.Add($clearSavedButton)

$deskAngleLabel = [System.Windows.Forms.Label]::new()
$deskAngleLabel.Text = "Desk Angle Adjustment"
$deskAngleLabel.Location = [System.Drawing.Point]::new(16, 336)
$deskAngleLabel.Size = [System.Drawing.Size]::new(220, 22)
$form.Controls.Add($deskAngleLabel)

$rotateLeftLargeButton = [System.Windows.Forms.Button]::new()
$rotateLeftLargeButton.Text = "-5 deg"
$rotateLeftLargeButton.Location = [System.Drawing.Point]::new(16, 362)
$rotateLeftLargeButton.Size = [System.Drawing.Size]::new(118, 34)
$rotateLeftLargeButton.Add_Click({
    Send-QuestCommand $ipBox.Text "ROTATE_DESK_LEFT_LARGE"
})
$form.Controls.Add($rotateLeftLargeButton)

$rotateLeftButton = [System.Windows.Forms.Button]::new()
$rotateLeftButton.Text = "-1 deg"
$rotateLeftButton.Location = [System.Drawing.Point]::new(146, 362)
$rotateLeftButton.Size = [System.Drawing.Size]::new(118, 34)
$rotateLeftButton.Add_Click({
    Send-QuestCommand $ipBox.Text "ROTATE_DESK_LEFT"
})
$form.Controls.Add($rotateLeftButton)

$rotateRightButton = [System.Windows.Forms.Button]::new()
$rotateRightButton.Text = "+1 deg"
$rotateRightButton.Location = [System.Drawing.Point]::new(278, 362)
$rotateRightButton.Size = [System.Drawing.Size]::new(118, 34)
$rotateRightButton.Add_Click({
    Send-QuestCommand $ipBox.Text "ROTATE_DESK_RIGHT"
})
$form.Controls.Add($rotateRightButton)

$rotateRightLargeButton = [System.Windows.Forms.Button]::new()
$rotateRightLargeButton.Text = "+5 deg"
$rotateRightLargeButton.Location = [System.Drawing.Point]::new(408, 362)
$rotateRightLargeButton.Size = [System.Drawing.Size]::new(118, 34)
$rotateRightLargeButton.Add_Click({
    Send-QuestCommand $ipBox.Text "ROTATE_DESK_RIGHT_LARGE"
})
$form.Controls.Add($rotateRightLargeButton)

$resetDeskYawButton = [System.Windows.Forms.Button]::new()
$resetDeskYawButton.Text = "Reset Desk Angle"
$resetDeskYawButton.Location = [System.Drawing.Point]::new(16, 406)
$resetDeskYawButton.Size = [System.Drawing.Size]::new(248, 34)
$resetDeskYawButton.Add_Click({
    Send-QuestCommand $ipBox.Text "RESET_DESK_ROTATION"
})
$form.Controls.Add($resetDeskYawButton)

$confirmDeskAlignmentButton = [System.Windows.Forms.Button]::new()
$confirmDeskAlignmentButton.Text = "Confirm Desk Alignment"
$confirmDeskAlignmentButton.Location = [System.Drawing.Point]::new(278, 406)
$confirmDeskAlignmentButton.Size = [System.Drawing.Size]::new(248, 34)
$confirmDeskAlignmentButton.Add_Click({
    Send-QuestCommand $ipBox.Text "CONFIRM_DESK_ALIGNMENT"
})
$form.Controls.Add($confirmDeskAlignmentButton)

$statusTitle = [System.Windows.Forms.Label]::new()
$statusTitle.Text = "Status"
$enableButton = [System.Windows.Forms.Button]::new()
$enableButton.Text = "Use Spatial Anchor Mode"
$enableButton.Location = [System.Drawing.Point]::new(16, 458)
$enableButton.Size = [System.Drawing.Size]::new(248, 34)
$enableButton.Add_Click({
    Send-QuestCommand $ipBox.Text "USE_SPATIAL_ANCHOR_REDIRECTION"
})
$form.Controls.Add($enableButton)

$disableButton = [System.Windows.Forms.Button]::new()
$disableButton.Text = "Restore Original Mode"
$disableButton.Location = [System.Drawing.Point]::new(278, 458)
$disableButton.Size = [System.Drawing.Size]::new(248, 34)
$disableButton.Add_Click({
    Send-QuestCommand $ipBox.Text "RESTORE_ORIGINAL_HAND_REDIRECTION"
})
$form.Controls.Add($disableButton)

$diminishedRealityButton = [System.Windows.Forms.Button]::new()
$diminishedRealityButton.Text = "Diminished Reality"
$diminishedRealityButton.Location = [System.Drawing.Point]::new(16, 502)
$diminishedRealityButton.Size = [System.Drawing.Size]::new(248, 34)
$diminishedRealityButton.Add_Click({
    Send-QuestCommand $ipBox.Text "USE_DIMINISHED_REALITY"
})
$form.Controls.Add($diminishedRealityButton)

$scaledScaniverseButton = [System.Windows.Forms.Button]::new()
$scaledScaniverseButton.Text = "Scaled Scaniverse"
$scaledScaniverseButton.Location = [System.Drawing.Point]::new(278, 502)
$scaledScaniverseButton.Size = [System.Drawing.Size]::new(248, 34)
$scaledScaniverseButton.Add_Click({
    Send-QuestCommand $ipBox.Text "USE_SCALED_SCANIVERSE_ROOM"
})
$form.Controls.Add($scaledScaniverseButton)

$setRedirectionOriginButton = [System.Windows.Forms.Button]::new()
$setRedirectionOriginButton.Text = "Set Redirection Origin"
$setRedirectionOriginButton.Location = [System.Drawing.Point]::new(16, 546)
$setRedirectionOriginButton.Size = [System.Drawing.Size]::new(510, 34)
$setRedirectionOriginButton.Add_Click({
    Send-QuestCommand $ipBox.Text "REARM_RIGHT_PINCH_REDIRECTION_ORIGIN"
})
$form.Controls.Add($setRedirectionOriginButton)

$resetRedirectionOriginButton = [System.Windows.Forms.Button]::new()
$resetRedirectionOriginButton.Text = "Reset Redirection Origin"
$resetRedirectionOriginButton.Location = [System.Drawing.Point]::new(16, 590)
$resetRedirectionOriginButton.Size = [System.Drawing.Size]::new(510, 34)
$resetRedirectionOriginButton.Add_Click({
    Send-QuestCommand $ipBox.Text "RESET_REDIRECTION_ORIGIN_TO_DESK"
})
$form.Controls.Add($resetRedirectionOriginButton)

$nextParticipantButton = [System.Windows.Forms.Button]::new()
$nextParticipantButton.Text = "Next Participant / Reset Objects"
$nextParticipantButton.Location = [System.Drawing.Point]::new(16, 634)
$nextParticipantButton.Size = [System.Drawing.Size]::new(510, 34)
$nextParticipantButton.Add_Click({
    Send-QuestCommand $ipBox.Text "RESET_EXPERIENCE_FOR_NEXT_PARTICIPANT"
})
$form.Controls.Add($nextParticipantButton)

$statusTitle.Location = [System.Drawing.Point]::new(16, 684)
$statusTitle.Size = [System.Drawing.Size]::new(80, 24)
$form.Controls.Add($statusTitle)

$statusLabel = [System.Windows.Forms.Label]::new()
$statusLabel.Text = "Waiting for status"
$statusLabel.BorderStyle = [System.Windows.Forms.BorderStyle]::Fixed3D
$statusLabel.Location = [System.Drawing.Point]::new(16, 710)
$statusLabel.Size = [System.Drawing.Size]::new(510, 28)
$form.Controls.Add($statusLabel)

$logBox = [System.Windows.Forms.TextBox]::new()
$logBox.Multiline = $true
$logBox.ScrollBars = "Vertical"
$logBox.ReadOnly = $true
$logBox.Location = [System.Drawing.Point]::new(16, 750)
$logBox.Size = [System.Drawing.Size]::new(510, 72)
$form.Controls.Add($logBox)

$timer = [System.Windows.Forms.Timer]::new()
$timer.Interval = 100
$timer.Add_Tick({
    Poll-Status
})

$form.Add_Shown({
    Start-StatusListener
    $timer.Start()
})

$form.Add_FormClosing({
    $timer.Stop()
    if ($null -ne $script:statusClient) {
        $script:statusClient.Close()
        $script:statusClient = $null
    }
})

[void]$form.ShowDialog()
