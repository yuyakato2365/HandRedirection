Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$cfg=Get-Content (Join-Path $PSScriptRoot 'launcher.config.json') -Raw|ConvertFrom-Json
$pidFile=Join-Path $root 'Temp\tracker_bridge4.pid'; $logDir=Join-Path $root 'Logs\TrackerBridge'
$editorCommandFile=Join-Path $root 'Temp\hand_redirection_editor_command.txt'
$cmdPort=9101; $statusPort=9102; $statusClient=$null
$bg=[Drawing.ColorTranslator]::FromHtml('#F3F6FB');$white=[Drawing.Color]::White
$navy=[Drawing.ColorTranslator]::FromHtml('#17233C');$ink=[Drawing.ColorTranslator]::FromHtml('#1F2937')
$muted=[Drawing.ColorTranslator]::FromHtml('#64748B');$blue=[Drawing.ColorTranslator]::FromHtml('#2563EB')
$green=[Drawing.ColorTranslator]::FromHtml('#16845B');$red=[Drawing.ColorTranslator]::FromHtml('#C2414B')
$line=[Drawing.ColorTranslator]::FromHtml('#D8E0EC');$font=[Drawing.Font]::new('Segoe UI',9.5)
function ResolveLauncherPath($p){if([IO.Path]::IsPathRooted($p)){return $p};[IO.Path]::GetFullPath((Join-Path $root $p))}
$bridgeDir=ResolveLauncherPath $cfg.trackerBridgeDirectory;$python=ResolveLauncherPath $cfg.trackerBridgePython;$script=ResolveLauncherPath $cfg.trackerBridgeScript;$bridgeCfg=ResolveLauncherPath $cfg.trackerBridgeConfig
function Label($s,$x,$y,$w,$h=24){$c=[Windows.Forms.Label]::new();$c.Text=$s;$c.Location=[Drawing.Point]::new($x,$y);$c.Size=[Drawing.Size]::new($w,$h);$c.Font=$font;$c.ForeColor=$ink;$c.BackColor=[Drawing.Color]::Transparent;$c}
function Button($s,$x,$y,$w,$h=38,$primary=$false,$danger=$false){$c=[Windows.Forms.Button]::new();$c.Text=$s;$c.Location=[Drawing.Point]::new($x,$y);$c.Size=[Drawing.Size]::new($w,$h);$c.FlatStyle='Flat';$c.Font=[Drawing.Font]::new('Segoe UI Semibold',9.5);$c.Cursor='Hand';if($primary){$c.BackColor=$blue;$c.ForeColor=$white;$c.FlatAppearance.BorderSize=0}else{$c.BackColor=$white;$c.ForeColor=if($danger){$red}else{$ink};$c.FlatAppearance.BorderColor=if($danger){$red}else{$line}};$c}
function Card($x,$y,$w,$h){$c=[Windows.Forms.Panel]::new();$c.Location=[Drawing.Point]::new($x,$y);$c.Size=[Drawing.Size]::new($w,$h);$c.BackColor=$white;$c.BorderStyle='FixedSingle';$c}
function Log($s){if($log){$log.AppendText("[$(Get-Date -Format HH:mm:ss)] $s`r`n");$log.SelectionStart=$log.Text.Length;$log.ScrollToCaret()}}
function Message($s,$bad=$false){$message.Text=$s;$message.ForeColor=if($bad){$red}else{$green}}
function TargetIp{if($mode.SelectedItem -eq 'Quest Link / Unity Editor'){'127.0.0.1'}else{$ip.Text.Trim()}}
function BridgeProcess{if(!(Test-Path $pidFile)){return $null};$n=0;$v=(Get-Content $pidFile -Raw -ErrorAction SilentlyContinue).Trim();if(![int]::TryParse($v,[ref]$n)){return $null};Get-Process -Id $n -ErrorAction SilentlyContinue}
function StartServices{
 if(!(Get-Process vrserver,vrmonitor -ErrorAction SilentlyContinue)){if(!(Test-Path $cfg.steamVrMonitorPath)){throw 'SteamVR was not found.'};Start-Process $cfg.steamVrMonitorPath}
 if(BridgeProcess){return};foreach($p in @($python,$script,$bridgeCfg)){if(!(Test-Path $p)){throw "Required file not found: $p"}}
 New-Item -ItemType Directory (Split-Path $pidFile) -Force|Out-Null;New-Item -ItemType Directory $logDir -Force|Out-Null;$stamp=Get-Date -Format yyyyMMdd-HHmmss
 $args=@(('"{0}"' -f $script),'--config',('"{0}"' -f $bridgeCfg),'--quest-ip',(TargetIp),'--wait-for-openvr','60')
 $p=Start-Process $python -ArgumentList $args -WorkingDirectory $bridgeDir -RedirectStandardOutput (Join-Path $logDir "tracker-$stamp.log") -RedirectStandardError (Join-Path $logDir "tracker-$stamp.error.log") -WindowStyle Hidden -PassThru
 Set-Content $pidFile $p.Id -Encoding ascii
}
function StopBridge{$p=BridgeProcess;if($p){Stop-Process $p.Id};if(Test-Path $pidFile){Remove-Item $pidFile -Force};Log 'Tracker Bridge stopped.'}
function UnityEditorCommand($command){
 New-Item -ItemType Directory (Split-Path $editorCommandFile) -Force|Out-Null
 $sentAt=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
 Set-Content -LiteralPath $editorCommandFile -Value ("{0}|{1}" -f $command,$sentAt) -Encoding ascii
 Log "Unity Editor command: $command"
}
function Send($s){try{$hostName=TargetIp;if([string]::IsNullOrWhiteSpace($hostName)){throw 'Quest IP is empty.'};$u=[Net.Sockets.UdpClient]::new();$b=[Text.Encoding]::UTF8.GetBytes($s);[void]$u.Send($b,$b.Length,$hostName,$cmdPort);$u.Close();Log "Sent: $s"}catch{Message $_.Exception.Message $true;Log "Send failed: $($_.Exception.Message)"}}
function Status{$a=$null-ne(Get-Process vrserver,vrmonitor -ErrorAction SilentlyContinue);$b=$null-ne(BridgeProcess);$steam.Text=if($a){'[ON] SteamVR  RUNNING'}else{'[--] SteamVR  STOPPED'};$steam.ForeColor=if($a){$green}else{$muted};$bridge.Text=if($b){'[ON] Tracker Bridge  RUNNING'}else{'[--] Tracker Bridge  STOPPED'};$bridge.ForeColor=if($b){$green}else{$muted}}
function OffsetStatus($s){$p=$s -split '\s+';if($p.Count -ne 8 -or $p[0] -ne 'TRACKER_OFFSET'){return};foreach($r in $grid.Rows){if([int]$r.Cells[0].Value -eq [int]$p[1]){for($i=0;$i -lt 6;$i++){$r.Cells[$i+2].Value=$p[$i+2]};$r.DefaultCellStyle.BackColor=[Drawing.ColorTranslator]::FromHtml('#ECFDF5')}}}
function Poll{if(!$script:statusClient){return};try{while($script:statusClient.Available-gt0){$e=[Net.IPEndPoint]::new([Net.IPAddress]::Any,0);$s=[Text.Encoding]::UTF8.GetString($script:statusClient.Receive([ref]$e)).Trim();$unity.Text="Unity: $s";$unity.ForeColor=if($s.StartsWith('ERROR')){$red}else{$green};OffsetStatus $s;Log "Received: $s"}}catch [Net.Sockets.SocketException]{}catch{Log $_.Exception.Message}}
function Row{if($grid.SelectedRows.Count-eq0){throw 'Select one tracker row first.'};$grid.SelectedRows[0]}
function Num($r,$i){$n=0.0;if(![double]::TryParse([string]$r.Cells[$i].Value,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$n)){throw 'Use numbers such as 0.02 or -5.'};$n.ToString('R',[Globalization.CultureInfo]::InvariantCulture)}

$form=[Windows.Forms.Form]::new();$form.Text='Hand Redirection Control Center';$form.Size=[Drawing.Size]::new(980,850);$form.MinimumSize=$form.Size;$form.StartPosition='CenterScreen';$form.BackColor=$bg;$form.Font=$font
$head=[Windows.Forms.Panel]::new();$head.Dock='Top';$head.Height=82;$head.BackColor=$navy;$form.Controls.Add($head)
$t=Label 'Hand Redirection Control Center' 24 13 650 36;$t.Font=[Drawing.Font]::new('Segoe UI Semibold',18);$t.ForeColor=$white;$head.Controls.Add($t);$t=Label 'SteamVR / Tracker Bridge / Spatial Anchor / Tracker Offsets' 26 50 650;$t.ForeColor=[Drawing.ColorTranslator]::FromHtml('#C7D2E5');$head.Controls.Add($t)
$tabs=[Windows.Forms.TabControl]::new();$tabs.Location=[Drawing.Point]::new(20,98);$tabs.Size=[Drawing.Size]::new(924,615);$tabs.Padding=[Drawing.Point]::new(18,8);$form.Controls.Add($tabs)
$setup=[Windows.Forms.TabPage]::new('  Setup & Anchor  ');$setup.BackColor=$bg;$tabs.TabPages.Add($setup);$offset=[Windows.Forms.TabPage]::new('  Tracker Offsets  ');$offset.BackColor=$bg;$tabs.TabPages.Add($offset)
$axes=[Windows.Forms.TabPage]::new('  Debug Axes  ');$axes.BackColor=$bg;$tabs.TabPages.Add($axes)
$c=Card 14 16 880 154;$setup.Controls.Add($c);$h=Label '1  CONNECTION' 20 12 250;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$c.Controls.Add($h);$c.Controls.Add((Label 'Run mode' 20 50 85))
$mode=[Windows.Forms.ComboBox]::new();$mode.DropDownStyle='DropDownList';$mode.Location=[Drawing.Point]::new(110,48);$mode.Size=[Drawing.Size]::new(250,28);[void]$mode.Items.Add('Quest Link / Unity Editor');[void]$mode.Items.Add('Standalone Quest');$mode.SelectedItem=[string]$cfg.defaultMode;$c.Controls.Add($mode)
$ipLabel=Label 'Quest IP' 385 50 70;$c.Controls.Add($ipLabel);$ip=[Windows.Forms.TextBox]::new();$ip.Text=[string]$cfg.standaloneQuestIp;$ip.Location=[Drawing.Point]::new(458,48);$ip.Size=[Drawing.Size]::new(175,27);$c.Controls.Add($ip)
$start=Button 'Start All + Unity Play' 655 39 205 44 $true;$c.Controls.Add($start);$stop=Button 'Stop Unity + Bridge' 655 94 205 34 $false $true;$c.Controls.Add($stop)
$steam=Label '[--] SteamVR  STOPPED' 20 101 220;$steam.Font=[Drawing.Font]::new('Segoe UI Semibold',9.5);$c.Controls.Add($steam);$bridge=Label '[--] Tracker Bridge  STOPPED' 250 101 250;$bridge.Font=$steam.Font;$c.Controls.Add($bridge);$ping=Button 'Ping Unity' 520 94 113 34;$ping.Add_Click({Send 'PING'});$c.Controls.Add($ping)
$w=Card 14 184 880 379;$setup.Controls.Add($w);$h=Label '2  SPATIAL ANCHOR WORKFLOW' 20 12 330;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$w.Controls.Add($h);$q=Label 'After pressing Play in Unity, complete these steps from top to bottom.' 365 15 470;$q.ForeColor=$muted;$w.Controls.Add($q)
$b=Button '1. Begin Anchor Placement' 20 54 405 48 $true;$b.Add_Click({Send 'BEGIN_ANCHOR_PLACEMENT'});$w.Controls.Add($b);$b=Button '2. Confirm Anchor' 445 54 405 48 $true;$b.Add_Click({Send 'CONFIRM_ANCHOR_PLACEMENT'});$w.Controls.Add($b)
$h=Label '3. Desk angle adjustment' 20 125 280;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$w.Controls.Add($h);$items=@(@('-5 deg','ROTATE_DESK_LEFT_LARGE'),@('-1 deg','ROTATE_DESK_LEFT'),@('+1 deg','ROTATE_DESK_RIGHT'),@('+5 deg','ROTATE_DESK_RIGHT_LARGE'));for($i=0;$i-lt4;$i++){$x=$items[$i];$b=Button $x[0] (20+$i*142) 160 126;$cmd=$x[1];$b.Add_Click({Send $cmd}.GetNewClosure());$w.Controls.Add($b)};$b=Button 'Reset angle' 588 160 126;$b.Add_Click({Send 'RESET_DESK_ROTATION'});$w.Controls.Add($b)
$b=Button '4. Confirm Desk + Start Spatial Anchor Mode' 20 222 830 52 $true;$b.Add_Click({Send 'CONFIRM_DESK_ALIGNMENT';[Threading.Thread]::Sleep(200);Send 'USE_SPATIAL_ANCHOR_REDIRECTION'});$w.Controls.Add($b)
$b=Button 'Gaze Debug ON' 20 303 160 34;$b.Add_Click({Send 'ENABLE_GAZE_DEBUG_VISUALS'});$w.Controls.Add($b);$b=Button 'Gaze Debug OFF' 192 303 160 34;$b.Add_Click({Send 'DISABLE_GAZE_DEBUG_VISUALS'});$w.Controls.Add($b)
$b=Button 'Open Advanced Control' 650 303 200 34;$b.Add_Click({$p=Join-Path $root 'Tools\SpatialAnchorCalibration\pc_anchor_control_window.ps1';Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $p)) -WindowStyle Normal});$w.Controls.Add($b)

$o=Card 14 16 880 547;$offset.Controls.Add($o);$h=Label 'PER-OBJECT TRACKER OFFSETS' 20 12 360;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$o.Controls.Add($h);$q=Label 'Press Play, load current values from Unity, then edit position (meters) or rotation (degrees).' 20 43 830 30;$q.ForeColor=$muted;$o.Controls.Add($q)
$grid=[Windows.Forms.DataGridView]::new();$grid.Location=[Drawing.Point]::new(20,82);$grid.Size=[Drawing.Size]::new(838,335);$grid.BackgroundColor=$white;$grid.BorderStyle='None';$grid.GridColor=$line;$grid.RowHeadersVisible=$false;$grid.AllowUserToAddRows=$false;$grid.AllowUserToDeleteRows=$false;$grid.SelectionMode='FullRowSelect';$grid.MultiSelect=$false;$grid.AutoSizeColumnsMode='Fill';$grid.ColumnHeadersHeight=38;$grid.RowTemplate.Height=38;$grid.EnableHeadersVisualStyles=$false;$grid.ColumnHeadersDefaultCellStyle.BackColor=$navy;$grid.ColumnHeadersDefaultCellStyle.ForeColor=$white;$grid.DefaultCellStyle.SelectionBackColor=[Drawing.ColorTranslator]::FromHtml('#DBEAFE');$grid.DefaultCellStyle.SelectionForeColor=$ink
foreach($x in @(@('Id','ID'),@('Name','Unity target'),@('PX','Pos X'),@('PY','Pos Y'),@('PZ','Pos Z'),@('RX','Rot X'),@('RY','Rot Y'),@('RZ','Rot Z'))){[void]$grid.Columns.Add($x[0],$x[1])};$grid.Columns[0].ReadOnly=$true;$grid.Columns[0].FillWeight=40;$grid.Columns[1].ReadOnly=$true;$grid.Columns[1].FillWeight=145
foreach($r in @(@(1,'cubeRelativeToHmd',0,-0.04,-0.07,0,0,0),@(2,'Object ID 2',0,0,0,0,0,0),@(3,'cubeRelativeToHmd (2)',0.02,-0.02,-0.02,0,0,0),@(4,'cubeRelativeToHmd (3)',0,0,0,0,0,0))){[void]$grid.Rows.Add($r)};$grid.Rows[0].Selected=$true;$o.Controls.Add($grid)
$b=Button 'Load from Unity' 20 440 190 42 $true;$b.Add_Click({Send 'GET_TRACKER_OFFSETS'});$o.Controls.Add($b);$b=Button 'Apply selected row' 224 440 210 42 $true;$b.Add_Click({try{$r=Row;$id=[int]$r.Cells[0].Value;$v=for($i=2;$i -lt 8;$i++){Num $r $i};Send ("SET_TRACKER_OFFSET {0} {1}" -f $id,($v -join ' '));Message "Object ID $id offset sent and saved."}catch{Message $_.Exception.Message $true}});$o.Controls.Add($b);$b=Button 'Reset selected to zero' 448 440 210 42 $false $true;$b.Add_Click({try{$r=Row;Send "RESET_TRACKER_OFFSET $([int]$r.Cells[0].Value)"}catch{Message $_.Exception.Message $true}});$o.Controls.Add($b);$q=Label 'Green rows were received from Unity. ID 2 may report an error if the Scene has no target.' 20 498 820;$q.ForeColor=$muted;$o.Controls.Add($q)

$a=Card 14 16 880 547;$axes.Controls.Add($a);$h=Label 'COORDINATE AXES VISIBILITY' 20 12 420;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$a.Controls.Add($h);$q=Label 'Runtime controls require Unity Play. Scene-view controls also work before Play.' 20 43 820 28;$q.ForeColor=$muted;$a.Controls.Add($q)
$h=Label 'All runtime axes' 20 92 270;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',10);$a.Controls.Add($h);$b=Button 'Show All' 330 82 190 40 $true;$b.Add_Click({Send 'ENABLE_ALL_COORDINATE_AXES'});$a.Controls.Add($b);$b=Button 'Hide All' 536 82 190 40;$b.Add_Click({Send 'DISABLE_ALL_COORDINATE_AXES'});$a.Controls.Add($b)
$h=Label 'Spatial Anchor preview / marker' 20 157 290;$a.Controls.Add($h);$b=Button 'Show' 330 146 190 40;$b.Add_Click({Send 'ENABLE_ANCHOR_AXES'});$a.Controls.Add($b);$b=Button 'Hide' 536 146 190 40;$b.Add_Click({Send 'DISABLE_ANCHOR_AXES'});$a.Controls.Add($b)
$h=Label 'DeskOrigin / RedirectOrigin' 20 221 290;$a.Controls.Add($h);$b=Button 'Show' 330 210 190 40;$b.Add_Click({Send 'ENABLE_ORIGIN_AXES'});$a.Controls.Add($b);$b=Button 'Hide' 536 210 190 40;$b.Add_Click({Send 'DISABLE_ORIGIN_AXES'});$a.Controls.Add($b)
$h=Label 'Detected tracker poses (objects)' 20 285 290;$a.Controls.Add($h);$b=Button 'Show' 330 274 190 40;$b.Add_Click({Send 'ENABLE_TRACKER_AXES'});$a.Controls.Add($b);$b=Button 'Hide' 536 274 190 40;$b.Add_Click({Send 'DISABLE_TRACKER_AXES'});$a.Controls.Add($b)
$h=Label 'Selected tracker object axis' 20 349 290;$a.Controls.Add($h);$q=Label 'Uses the selected ID in Tracker Offsets.' 20 375 290 24;$q.ForeColor=$muted;$a.Controls.Add($q);$b=Button 'Show Selected' 330 338 190 40;$b.Add_Click({try{$r=Row;Send "ENABLE_TRACKER_AXIS $([int]$r.Cells[0].Value)"}catch{Message $_.Exception.Message $true}});$a.Controls.Add($b);$b=Button 'Hide Selected' 536 338 190 40;$b.Add_Click({try{$r=Row;Send "DISABLE_TRACKER_AXIS $([int]$r.Cells[0].Value)"}catch{Message $_.Exception.Message $true}});$a.Controls.Add($b)
$h=Label 'Unity Scene view references' 20 421 290;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',10);$a.Controls.Add($h);$q=Label 'DeskOrigin, RedirectOrigin, and derived Spatial Anchor while editing.' 20 447 780 24;$q.ForeColor=$muted;$a.Controls.Add($q);$b=Button 'Show in Scene' 330 482 190 42 $true;$b.Add_Click({UnityEditorCommand 'SHOW_EDITOR_REFERENCE_AXES';Message 'Scene-view reference axes enabled.'});$a.Controls.Add($b);$b=Button 'Hide in Scene' 536 482 190 42;$b.Add_Click({UnityEditorCommand 'HIDE_EDITOR_REFERENCE_AXES';Message 'Scene-view reference axes disabled.'});$a.Controls.Add($b)

$s=Card 20 726 924 76;$form.Controls.Add($s);$unity=Label 'Unity: waiting for Play / PONG' 16 9 580;$unity.ForeColor=$muted;$s.Controls.Add($unity);$message=Label 'Start services, press Play in Unity, then complete the Anchor workflow.' 16 39 600;$message.ForeColor=$muted;$s.Controls.Add($message);$log=[Windows.Forms.TextBox]::new();$log.Multiline=$true;$log.ScrollBars='Vertical';$log.ReadOnly=$true;$log.BackColor=[Drawing.ColorTranslator]::FromHtml('#F8FAFC');$log.ForeColor=$muted;$log.Font=[Drawing.Font]::new('Consolas',8);$log.Location=[Drawing.Point]::new(628,8);$log.Size=[Drawing.Size]::new(278,58);$s.Controls.Add($log)
$mode.Add_SelectedIndexChanged({
    $standalone = $mode.SelectedItem -eq 'Standalone Quest'
    $ip.Enabled = $standalone
    $ipLabel.Enabled = $standalone
})
$start.Add_Click({
    try {
        if ([string]::IsNullOrWhiteSpace((TargetIp))) { throw 'Quest IP is empty.' }
        StartServices
        UnityEditorCommand 'PLAY'
        Message 'Services started and Unity Play requested. Continue with step 1.'
        Log 'Services start requested.'
        Status
    }
    catch { Message $_.Exception.Message $true; Log $_.Exception.Message }
})
$stop.Add_Click({ UnityEditorCommand 'STOP'; StopBridge; Status })
$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 250
$timer.Add_Tick({ Poll; Status })
$form.Add_Shown({
    $standalone = $mode.SelectedItem -eq 'Standalone Quest'
    $ip.Enabled = $standalone
    $ipLabel.Enabled = $standalone
    try {
        $script:statusClient = [Net.Sockets.UdpClient]::new($statusPort)
        $script:statusClient.Client.Blocking = $false
        Log "Listening on UDP :$statusPort"
    }
    catch { Log $_.Exception.Message }
    Status
    $timer.Start()
})
$form.Add_FormClosing({
    $timer.Stop()
    if ($script:statusClient) {
        $script:statusClient.Close()
        $script:statusClient = $null
    }
})
[void]$form.ShowDialog()
