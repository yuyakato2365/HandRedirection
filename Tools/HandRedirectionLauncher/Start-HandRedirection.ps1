Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$cfg=Get-Content (Join-Path $PSScriptRoot 'launcher.config.json') -Raw|ConvertFrom-Json
$pidFile=Join-Path $root 'Temp\tracker_bridge4.pid'; $logDir=Join-Path $root 'Logs\TrackerBridge'
$editorCommandFile=Join-Path $root 'Temp\hand_redirection_editor_command.txt'
$launcherStateFile=Join-Path $root 'UserSettings\HandRedirectionLauncherState.json'
$script:selectedRingPattern='A'
$script:ringLayouts=@{
 A=[PSCustomObject]@{X=0.0;Z=0.82;RadiusX=0.12;RadiusZ=0.12}
 B=[PSCustomObject]@{X=-0.72;Z=0.0;RadiusX=0.16;RadiusZ=0.16}
 C=[PSCustomObject]@{X=0.0;Z=-0.82;RadiusX=0.21;RadiusZ=0.21}
}
$script:characterLayouts=@{
 PANDA=[PSCustomObject]@{Pattern='A';X=0.0;Z=1.14;Radius=0.12;Multiplier=1.0}
 GORILLA=[PSCustomObject]@{Pattern='B';X=-1.12;Z=0.0;Radius=0.16;Multiplier=1.0}
 ELEPHANT=[PSCustomObject]@{Pattern='C';X=0.0;Z=-1.32;Radius=0.21;Multiplier=1.0}
}
$script:characterYawOffset=0.0
$script:ringMapPixelsPerMeter=100.0;$script:ringMapCenterX=0.0;$script:ringMapCenterY=0.0;$script:ringMapRange=1.35;$script:dragRing=$null
$cmdPort=9101; $statusPort=9102; $statusClient=$null
$bg=[Drawing.ColorTranslator]::FromHtml('#F3F6FB');$white=[Drawing.Color]::White
$navy=[Drawing.ColorTranslator]::FromHtml('#17233C');$ink=[Drawing.ColorTranslator]::FromHtml('#1F2937')
$muted=[Drawing.ColorTranslator]::FromHtml('#64748B');$blue=[Drawing.ColorTranslator]::FromHtml('#2563EB')
$green=[Drawing.ColorTranslator]::FromHtml('#16845B');$red=[Drawing.ColorTranslator]::FromHtml('#C2414B')
$line=[Drawing.ColorTranslator]::FromHtml('#D8E0EC');$font=[Drawing.Font]::new('Segoe UI',9.5)
function ResolveLauncherPath($p){if([IO.Path]::IsPathRooted($p)){return $p};[IO.Path]::GetFullPath((Join-Path $root $p))}
$bridgeDir=ResolveLauncherPath $cfg.trackerBridgeDirectory;$python=ResolveLauncherPath $cfg.trackerBridgePython;$script=ResolveLauncherPath $cfg.trackerBridgeScript;$bridgeCfg=ResolveLauncherPath $cfg.trackerBridgeConfig
$bridgeAckPort=9001
if(Test-Path -LiteralPath $bridgeCfg){try{$bridgeRuntimeCfg=Get-Content -LiteralPath $bridgeCfg -Raw|ConvertFrom-Json;if($null-ne$bridgeRuntimeCfg.ackPort){$bridgeAckPort=[int]$bridgeRuntimeCfg.ackPort}}catch{}}
function Label($s,$x,$y,$w,$h=24){$c=[Windows.Forms.Label]::new();$c.Text=$s;$c.Location=[Drawing.Point]::new($x,$y);$c.Size=[Drawing.Size]::new($w,$h);$c.Font=$font;$c.ForeColor=$ink;$c.BackColor=[Drawing.Color]::Transparent;$c}
function Button($s,$x,$y,$w,$h=38,$primary=$false,$danger=$false){$c=[Windows.Forms.Button]::new();$c.Text=$s;$c.Location=[Drawing.Point]::new($x,$y);$c.Size=[Drawing.Size]::new($w,$h);$c.FlatStyle='Flat';$c.Font=[Drawing.Font]::new('Segoe UI Semibold',9.5);$c.Cursor='Hand';if($primary){$c.BackColor=$blue;$c.ForeColor=$white;$c.FlatAppearance.BorderSize=0}else{$c.BackColor=$white;$c.ForeColor=if($danger){$red}else{$ink};$c.FlatAppearance.BorderColor=if($danger){$red}else{$line}};$c}
function Card($x,$y,$w,$h){$c=[Windows.Forms.Panel]::new();$c.Location=[Drawing.Point]::new($x,$y);$c.Size=[Drawing.Size]::new($w,$h);$c.BackColor=$white;$c.BorderStyle='FixedSingle';$c}
function Log($s){if($log){$log.AppendText("[$(Get-Date -Format HH:mm:ss)] $s`r`n");$log.SelectionStart=$log.Text.Length;$log.ScrollToCaret()}}
function Message($s,$bad=$false){$message.Text=$s;$message.ForeColor=if($bad){$red}else{$green}}
function TargetIp{if($mode.SelectedItem -eq 'Quest Link / Unity Editor'){'127.0.0.1'}else{$ip.Text.Trim()}}
function BridgeProcess{
 if(Test-Path $pidFile){$n=0;$v=(Get-Content $pidFile -Raw -ErrorAction SilentlyContinue).Trim();if([int]::TryParse($v,[ref]$n)){$p=Get-Process -Id $n -ErrorAction SilentlyContinue;if($p){return $p}}}
 # Recover an already-running bridge when its PID file was lost. The bridge
 # exclusively owns the configured UDP ACK port while it is alive.
 $pattern=('^\s*UDP\s+\S+:{0}\s+\S+\s+(\d+)\s*$' -f $bridgeAckPort)
 $line=netstat -ano -p udp 2>$null|Where-Object{$_ -match $pattern}|Select-Object -First 1
 if($line -and $line -match $pattern){$ownerPid=[int]$Matches[1];return Get-Process -Id $ownerPid -ErrorAction SilentlyContinue}
 return $null
}
function StartServices{
 if(!(Get-Process vrserver,vrmonitor -ErrorAction SilentlyContinue)){if(!(Test-Path $cfg.steamVrMonitorPath)){throw 'SteamVR was not found.'};Start-Process $cfg.steamVrMonitorPath}
 if(BridgeProcess){return};foreach($p in @($python,$script,$bridgeCfg)){if(!(Test-Path $p)){throw "Required file not found: $p"}}
 New-Item -ItemType Directory (Split-Path $pidFile) -Force|Out-Null;New-Item -ItemType Directory $logDir -Force|Out-Null;$stamp=Get-Date -Format yyyyMMdd-HHmmss
 $args=@(('"{0}"' -f $script),'--config',('"{0}"' -f $bridgeCfg),'--quest-ip',(TargetIp),'--wait-for-openvr','60','--pid-file',('"{0}"' -f $pidFile))
 $p=Start-Process $python -ArgumentList $args -WorkingDirectory $bridgeDir -RedirectStandardOutput (Join-Path $logDir "tracker-$stamp.log") -RedirectStandardError (Join-Path $logDir "tracker-$stamp.error.log") -WindowStyle Hidden -PassThru
 for($i=0;$i-lt20 -and !(BridgeProcess);$i++){Start-Sleep -Milliseconds 100;if($p.HasExited){throw "Tracker Bridge failed to start. Check Logs\TrackerBridge\tracker-$stamp.error.log"}}
 if(!(BridgeProcess)){throw 'Tracker Bridge started but did not publish its process ID.'}
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
function GroupOffsetStatus($s){$p=$s -split '\s+';if($p.Count -ne 4 -or $p[0] -ne 'TRACKER_GROUP_OFFSET'){return};$groupX.Text=$p[1];$groupY.Text=$p[2];$groupZ.Text=$p[3];$groupState.Text='Loaded and saved in Unity';$groupState.ForeColor=$green}
function HandScaleStatus($s){$p=$s -split '\s+';if($p.Count -ne 2 -or $p[0] -ne 'HAND_MAPPING_SCALE_MULTIPLIER'){return};$handScale.Text=$p[1];$handScaleState.Text='Loaded and saved in Unity';$handScaleState.ForeColor=$green}
function GazeRadiusStatus($s){$p=$s -split '\s+';if($p.Count -ne 3 -or $p[0] -ne 'GAZE_TARGET_RADIUS'){return};foreach($r in $gazeGrid.Rows){if([string]$r.Cells[0].Value -eq $p[1]){$r.Cells[1].Value=$p[2];$r.DefaultCellStyle.BackColor=[Drawing.ColorTranslator]::FromHtml('#ECFDF5')}};$gazeRadiusState.Text='Loaded and saved in Unity';$gazeRadiusState.ForeColor=$green}
function DeskScaleStatus($s){$p=$s -split '\s+';if($p.Count -ne 2 -or $p[0] -ne 'DESK_SCALE'){return};$manualDeskScale.Text=$p[1];$deskScaleState.Text='Applied in Unity';$deskScaleState.ForeColor=$green}
function TargetRingStatus($s){$p=$s -split '\s+';if($p.Count-ne3-or$p[0]-ne'TARGET_RING_PATTERN'){return};if($p[1]-eq'OFF'){$ringPatternDisplay.Text='RING CHALLENGE OFF';$ringPatternDisplay.BackColor=$muted;$ringPatternState.Text='Disabled in Unity';$ringPatternState.ForeColor=$muted;return};$script:selectedRingPattern=$p[1];$ringPatternDisplay.Text="PATTERN $($p[1])";$ringPatternState.Text=if($p[2]-eq'complete'){'Completed'}else{'Active in Unity'};$ringPatternDisplay.BackColor=if($p[2]-eq'complete'){[Drawing.ColorTranslator]::FromHtml('#0891B2')}else{$blue};$ringPatternState.ForeColor=$green}
function RingSettingsRow($id){foreach($r in $ringSettingsGrid.Rows){if([string]$r.Cells[0].Value-eq[string]$id){return $r}};$null}
function SelectedRingSettingsRow{if($ringSettingsGrid.SelectedRows.Count-gt0){return $ringSettingsGrid.SelectedRows[0]};$r=RingSettingsRow $script:selectedRingPattern;if($r){return $r};throw 'Select one pattern row first.'}
function TargetRingSettingsStatus($s){$p=$s -split '\s+';if($p.Count-ne12-or$p[0]-ne'TARGET_RING_SETTINGS'){return};$r=RingSettingsRow $p[1];if(!$r){return};$r.Cells[1].Value=$p[2];for($i=0;$i-lt9;$i++){$r.Cells[$i+2].Value=$p[$i+3]};$r.DefaultCellStyle.BackColor=[Drawing.ColorTranslator]::FromHtml('#ECFDF5');if($script:selectedRingPattern-eq$p[1]){$ringUniformScale.Checked=([math]::Abs([double]$p[3]-[double]$p[4])-lt0.000001-and[math]::Abs([double]$p[3]-[double]$p[5])-lt0.000001)};$ringPatternState.Text='Pattern and target object loaded from Unity';$ringPatternState.ForeColor=$green}
function TargetRingLayoutStatus($s){$p=$s -split '\s+';if($p.Count-ne7-or$p[0]-ne'TARGET_RING_LAYOUT'){return};$id=$p[1];if(!$script:ringLayouts.ContainsKey($id)){return};$layout=$script:ringLayouts[$id];$layout.X=[double]$p[3];$layout.Z=[double]$p[4];$layout.RadiusX=[math]::Max(0.01,[double]$p[5]);$layout.RadiusZ=[math]::Max(0.01,[double]$p[6]);RefreshApproxCharacterLayouts;if($ringMap){$ringMap.Invalidate()};$ringPatternState.Text="Layout $id loaded: X=$($p[3]) Z=$($p[4]) m";$ringPatternState.ForeColor=$green}
function TargetRingCharacterStatus($s){$p=$s -split '\s+';if($p.Count-ne7-or$p[0]-ne'TARGET_RING_CHARACTER'){return};$id=$p[1];if(!$script:characterLayouts.ContainsKey($id)){return};$v=$script:characterLayouts[$id];$v.Pattern=$p[2];$v.X=[double]$p[3];$v.Z=[double]$p[4];$v.Radius=[math]::Max(0.01,[double]$p[5]);$v.Multiplier=[double]$p[6];if($characterSettingsGrid){foreach($r in $characterSettingsGrid.Rows){if([string]$r.Cells[0].Value-eq$id){$r.Cells[1].Value=$p[2];$r.Cells[2].Value=$p[6];$r.DefaultCellStyle.BackColor=[Drawing.ColorTranslator]::FromHtml('#ECFDF5')}}};if($ringMap){$ringMap.Invalidate()}}
function TargetRingCharacterYawStatus($s){$p=$s -split '\s+';if($p.Count-ne2-or$p[0]-ne'TARGET_RING_CHARACTER_YAW'){return};$script:characterYawOffset=[double]$p[1];if($characterYawOffset){$characterYawOffset.Text=$p[1]}}
function TargetRingCompletedStatus($s){$p=$s -split '\s+';if($p.Count-ne2-or$p[0]-ne'TARGET_RING_COMPLETED'){return};$script:selectedRingPattern=$p[1];$ringPatternDisplay.Text="PATTERN $($p[1]) - COMPLETE";$ringPatternDisplay.BackColor=[Drawing.ColorTranslator]::FromHtml('#0891B2');$ringPatternState.Text='Correct size and position';$ringPatternState.ForeColor=$green}
function Poll{if(!$script:statusClient){return};try{while($script:statusClient.Available-gt0){$e=[Net.IPEndPoint]::new([Net.IPAddress]::Any,0);$s=[Text.Encoding]::UTF8.GetString($script:statusClient.Receive([ref]$e)).Trim();$unity.Text="Unity: $s";$unity.ForeColor=if($s.StartsWith('ERROR')){$red}else{$green};OffsetStatus $s;GroupOffsetStatus $s;HandScaleStatus $s;GazeRadiusStatus $s;DeskScaleStatus $s;TargetRingStatus $s;TargetRingSettingsStatus $s;TargetRingLayoutStatus $s;TargetRingCharacterStatus $s;TargetRingCharacterYawStatus $s;TargetRingCompletedStatus $s;Log "Received: $s"}}catch [Net.Sockets.SocketException]{}catch{Log $_.Exception.Message}}
function Row{if($grid.SelectedRows.Count-eq0){throw 'Select one tracker row first.'};$grid.SelectedRows[0]}
function GazeRow{if($gazeGrid.SelectedRows.Count-eq0){throw 'Select one gaze object row first.'};$gazeGrid.SelectedRows[0]}
function Num($r,$i){$n=0.0;if(![double]::TryParse([string]$r.Cells[$i].Value,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$n)){throw 'Use numbers such as 0.02 or -5.'};$n.ToString('R',[Globalization.CultureInfo]::InvariantCulture)}
function TextNum($box){$n=0.0;if(![double]::TryParse($box.Text,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$n)){throw 'Use meters such as 0.05 or -0.02.'};$n.ToString('R',[Globalization.CultureInfo]::InvariantCulture)}
function UpdateRingMapSizeFromRow($r){if(!$r){return};$id=[string]$r.Cells[0].Value;$target=[string]$r.Cells[1].Value;$sx=[double]$r.Cells[2].Value;$sy=[double]$r.Cells[3].Value;$sz=[double]$r.Cells[4].Value;$base=@{'1'=0.12;'3'=0.094;'4'=0.137};$longest=if($base.ContainsKey($target)){[double]$base[$target]}else{0.12};$layout=$script:ringLayouts[$id];if([math]::Abs($sx-$sy)-lt0.000001-and[math]::Abs($sx-$sz)-lt0.000001){$layout.RadiusX=$longest*$sx;$layout.RadiusZ=$layout.RadiusX}else{$layout.RadiusX=$longest*$sx;$layout.RadiusZ=$longest*$sz};RefreshApproxCharacterLayouts;if($ringMap){$ringMap.Invalidate()}}
function RefreshApproxCharacterLayouts{$sorted=@('A','B','C')|Sort-Object { [math]::Max($script:ringLayouts[$_].RadiusX,$script:ringLayouts[$_].RadiusZ) };$characters=@('PANDA','GORILLA','ELEPHANT');for($i=0;$i-lt3;$i++){$id=$characters[$i];$pattern=$sorted[$i];$ring=$script:ringLayouts[$pattern];$v=$script:characterLayouts[$id];$v.Pattern=$pattern;$ringRadius=[math]::Max($ring.RadiusX,$ring.RadiusZ);$v.Radius=$ringRadius*$v.Multiplier;$length=[math]::Sqrt($ring.X*$ring.X+$ring.Z*$ring.Z);if($length-lt0.0001){$dx=0.0;$dz=1.0}else{$dx=$ring.X/$length;$dz=$ring.Z/$length};$behind=$ringRadius+$v.Radius+0.08;$v.X=$ring.X+$dx*$behind;$v.Z=$ring.Z+$dz*$behind;if($characterSettingsGrid){foreach($r in $characterSettingsGrid.Rows){if([string]$r.Cells[0].Value-eq$id){$r.Cells[1].Value=$pattern}}}}}
function PaintRingMap($sender,$e){
 $g=$e.Graphics
 $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
 $w=$sender.ClientSize.Width;$h=$sender.ClientSize.Height;$range=$script:ringMapRange
 if(!$script:dragRing){$range=1.35;foreach($v in $script:ringLayouts.Values){$range=[math]::Max($range,1.10*[math]::Max([math]::Abs($v.X)+$v.RadiusX,[math]::Abs($v.Z)+$v.RadiusZ))};foreach($v in $script:characterLayouts.Values){$range=[math]::Max($range,1.10*[math]::Max([math]::Abs($v.X)+$v.Radius,[math]::Abs($v.Z)+$v.Radius))};$script:ringMapRange=$range}
 $scale=[math]::Min(($w-70)/(2*$range),($h-54)/(2*$range));$cx=$w/2;$cy=$h/2
 $script:ringMapPixelsPerMeter=$scale;$script:ringMapCenterX=$cx;$script:ringMapCenterY=$cy
 $gridPen=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#D8E0EC'),1)
 $axisPen=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#64748B'),2)
 for($m=-1.0;$m-le1.0;$m+=0.25){$px=$cx+$m*$scale;$py=$cy-$m*$scale;$g.DrawLine($gridPen,$px,18,$px,$h-18);$g.DrawLine($gridPen,18,$py,$w-18,$py)}
 $g.DrawLine($axisPen,18,$cy,$w-18,$cy);$g.DrawLine($axisPen,$cx,18,$cx,$h-18)
 $originBrush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#17233C'))
 $g.FillEllipse($originBrush,$cx-6,$cy-6,12,12)
 $small=[Drawing.Font]::new('Segoe UI Semibold',8.5)
 $g.DrawString('DeskOrigin',$small,$originBrush,$cx+8,$cy+6)
 $g.DrawString('+X RIGHT',$small,$originBrush,$w-86,$cy+5)
 $g.DrawString('+Z FORWARD',$small,$originBrush,$cx+8,2)
 $characterColors=@{PANDA='#16A34A';GORILLA='#7C3AED';ELEPHANT='#0891B2'}
 foreach($id in @('PANDA','GORILLA','ELEPHANT')){
  $v=$script:characterLayouts[$id];$px=$cx+$v.X*$scale;$py=$cy-$v.Z*$scale
  $radius=[math]::Max(5,$v.Radius*$scale);$color=[Drawing.ColorTranslator]::FromHtml($characterColors[$id])
  $fill=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(28,$color));$pen=[Drawing.Pen]::new($color,2)
  $pen.DashStyle=[Drawing.Drawing2D.DashStyle]::Dash
  $g.FillEllipse($fill,$px-$radius,$py-$radius,2*$radius,2*$radius)
  $g.DrawEllipse($pen,$px-$radius,$py-$radius,2*$radius,2*$radius)
  $labelBrush=[Drawing.SolidBrush]::new($color)
  $g.DrawString("$id ($($v.Pattern))`n$($v.Multiplier.ToString('0.00'))x Ring",$small,$labelBrush,$px-$radius,$py+$radius+2)
  $fill.Dispose();$pen.Dispose();$labelBrush.Dispose()
 }
 $colors=@{A='#2563EB';B='#F59E0B';C='#DB2777'}
 foreach($id in @('A','B','C')){
  $v=$script:ringLayouts[$id];$px=$cx+$v.X*$scale;$py=$cy-$v.Z*$scale
  $rx=[math]::Max(5,$v.RadiusX*$scale);$rz=[math]::Max(5,$v.RadiusZ*$scale)
  $color=[Drawing.ColorTranslator]::FromHtml($colors[$id])
  $fill=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(45,$color))
  $penWidth=if($id-eq$script:selectedRingPattern){4}else{2}
  $pen=[Drawing.Pen]::new($color,$penWidth)
  $g.FillEllipse($fill,$px-$rx,$py-$rz,2*$rx,2*$rz)
  $g.DrawEllipse($pen,$px-$rx,$py-$rz,2*$rx,2*$rz)
  $labelBrush=[Drawing.SolidBrush]::new($color)
  $g.DrawString("$id  X=$($v.X.ToString('0.00'))  Z=$($v.Z.ToString('0.00'))`nR=$($v.RadiusX.ToString('0.00'))m",$small,$labelBrush,$px-$rx,$py-$rz-31)
  $fill.Dispose();$pen.Dispose();$labelBrush.Dispose()
 }
 $gridPen.Dispose();$axisPen.Dispose();$originBrush.Dispose();$small.Dispose()
}
function HitTestRingMap($x,$y){foreach($id in @('C','B','A')){$v=$script:ringLayouts[$id];$px=$script:ringMapCenterX+$v.X*$script:ringMapPixelsPerMeter;$py=$script:ringMapCenterY-$v.Z*$script:ringMapPixelsPerMeter;$rx=[math]::Max(14,$v.RadiusX*$script:ringMapPixelsPerMeter+8);$rz=[math]::Max(14,$v.RadiusZ*$script:ringMapPixelsPerMeter+8);$dx=($x-$px)/$rx;$dy=($y-$py)/$rz;if($dx*$dx+$dy*$dy-le1){return $id}};$null}
function SaveLauncherState{
 try{
  $trackerRows=@();foreach($r in $grid.Rows){$trackerRows+=@{id=[int]$r.Cells[0].Value;values=@(2..7|ForEach-Object{[string]$r.Cells[$_].Value})}}
  $gazeRows=@();foreach($r in $gazeGrid.Rows){$gazeRows+=@{id=[string]$r.Cells[0].Value;radius=[string]$r.Cells[1].Value}}
  $ringRows=@();foreach($r in $ringSettingsGrid.Rows){$ringRows+=@{id=[string]$r.Cells[0].Value;values=@(1..10|ForEach-Object{[string]$r.Cells[$_].Value})}}
  $characterRows=@();foreach($r in $characterSettingsGrid.Rows){$characterRows+=@{id=[string]$r.Cells[0].Value;multiplier=[string]$r.Cells[2].Value}}
  $layoutRows=@();foreach($id in @('A','B','C')){$v=$script:ringLayouts[$id];$layoutRows+=@{id=$id;x=$v.X;z=$v.Z;radiusX=$v.RadiusX;radiusZ=$v.RadiusZ}}
  $state=@{version=9;mode=[string]$mode.SelectedItem;questIp=$ip.Text;groupOffset=@($groupX.Text,$groupY.Text,$groupZ.Text);handMappingScale=$handScale.Text;deskScale=$manualDeskScale.Text;disableDeskScaleBlackout=$disableDeskScaleBlackout.Checked;targetRingPattern=$script:selectedRingPattern;resetSizesOnRingSwitch=$resetSizesOnRingSwitch.Checked;ringUniformScale=$ringUniformScale.Checked;ringPatterns=$ringRows;ringLayouts=$layoutRows;ringCharacters=$characterRows;characterYawOffset=$characterYawOffset.Text;trackerOffsets=$trackerRows;gazeRadii=$gazeRows}
  $dir=Split-Path $launcherStateFile;New-Item -ItemType Directory -Path $dir -Force|Out-Null
  $temporary="$launcherStateFile.tmp";$state|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $temporary -Encoding utf8;Move-Item -LiteralPath $temporary -Destination $launcherStateFile -Force
 }
 catch{Log "Could not save launcher settings: $($_.Exception.Message)"}
}
function LoadLauncherState{
 if(!(Test-Path -LiteralPath $launcherStateFile)){return}
 try{
  $state=Get-Content -LiteralPath $launcherStateFile -Raw|ConvertFrom-Json
  if($mode.Items.Contains([string]$state.mode)){$mode.SelectedItem=[string]$state.mode}
  if($null-ne$state.questIp){$ip.Text=[string]$state.questIp}
  if($state.groupOffset.Count-ge3){$groupX.Text=[string]$state.groupOffset[0];$groupY.Text=[string]$state.groupOffset[1];$groupZ.Text=[string]$state.groupOffset[2]}
  if($null-ne$state.handMappingScale){$handScale.Text=[string]$state.handMappingScale}
  if($null-ne$state.deskScale){$manualDeskScale.Text=[string]$state.deskScale}
  if($null-ne$state.disableDeskScaleBlackout){$disableDeskScaleBlackout.Checked=[bool]$state.disableDeskScaleBlackout}
  if($null-ne$state.targetRingPattern){$script:selectedRingPattern=[string]$state.targetRingPattern;$ringPatternDisplay.Text="PATTERN $script:selectedRingPattern"}
  if($null-ne$state.resetSizesOnRingSwitch){$resetSizesOnRingSwitch.Checked=[bool]$state.resetSizesOnRingSwitch}
  foreach($saved in $state.ringPatterns){$r=RingSettingsRow $saved.id;if($r){if($saved.values.Count-ge10){for($i=0;$i-lt10;$i++){$r.Cells[$i+1].Value=[string]$saved.values[$i]}}else{for($i=0;$i-lt9-and$i-lt$saved.values.Count;$i++){$r.Cells[$i+2].Value=[string]$saved.values[$i]}}}}
  foreach($saved in $state.ringLayouts){$id=[string]$saved.id;if($script:ringLayouts.ContainsKey($id)){$v=$script:ringLayouts[$id];$v.X=[double]$saved.x;$v.Z=[double]$saved.z;$v.RadiusX=[double]$saved.radiusX;$v.RadiusZ=[double]$saved.radiusZ}}
  foreach($saved in $state.ringCharacters){$id=[string]$saved.id;if($script:characterLayouts.ContainsKey($id)){$v=$script:characterLayouts[$id];$v.Multiplier=[double]$saved.multiplier;foreach($r in $characterSettingsGrid.Rows){if([string]$r.Cells[0].Value-eq$id){$r.Cells[2].Value=[string]$saved.multiplier}}}}
  if($null-ne$state.characterYawOffset){$characterYawOffset.Text=[string]$state.characterYawOffset;$script:characterYawOffset=[double]$state.characterYawOffset}
  if($null-ne$state.ringUniformScale){$ringUniformScale.Checked=[bool]$state.ringUniformScale}
  foreach($saved in $state.trackerOffsets){foreach($r in $grid.Rows){if([int]$r.Cells[0].Value-eq[int]$saved.id){for($i=0;$i-lt6-and$i-lt$saved.values.Count;$i++){$r.Cells[$i+2].Value=[string]$saved.values[$i]}}}}
  foreach($saved in $state.gazeRadii){foreach($r in $gazeGrid.Rows){if([string]$r.Cells[0].Value-eq[string]$saved.id){$r.Cells[1].Value=[string]$saved.radius}}}
  RefreshApproxCharacterLayouts;if($ringMap){$ringMap.Invalidate()}
  Log 'Restored saved launcher settings.'
 }
 catch{Log "Could not load launcher settings: $($_.Exception.Message)"}
}

$form=[Windows.Forms.Form]::new();$form.Text='Hand Redirection Control Center';$form.Size=[Drawing.Size]::new(980,850);$form.MinimumSize=$form.Size;$form.StartPosition='CenterScreen';$form.BackColor=$bg;$form.Font=$font
$head=[Windows.Forms.Panel]::new();$head.Dock='Top';$head.Height=82;$head.BackColor=$navy;$form.Controls.Add($head)
$t=Label 'Hand Redirection Control Center' 24 13 650 36;$t.Font=[Drawing.Font]::new('Segoe UI Semibold',18);$t.ForeColor=$white;$head.Controls.Add($t);$t=Label 'SteamVR / Tracker Bridge / Spatial Anchor / Tracker Offsets' 26 50 650;$t.ForeColor=[Drawing.ColorTranslator]::FromHtml('#C7D2E5');$head.Controls.Add($t)
$tabs=[Windows.Forms.TabControl]::new();$tabs.Location=[Drawing.Point]::new(20,98);$tabs.Size=[Drawing.Size]::new(924,615);$tabs.Padding=[Drawing.Point]::new(18,8);$form.Controls.Add($tabs)
$setup=[Windows.Forms.TabPage]::new('  Setup & Anchor  ');$setup.BackColor=$bg;$tabs.TabPages.Add($setup);$offset=[Windows.Forms.TabPage]::new('  Tracker Offsets  ');$offset.BackColor=$bg;$tabs.TabPages.Add($offset)
$deskScaleTab=[Windows.Forms.TabPage]::new('  Desk Scale  ');$deskScaleTab.BackColor=$bg;$tabs.TabPages.Add($deskScaleTab)
$ringTab=[Windows.Forms.TabPage]::new('  Ring Challenge  ');$ringTab.BackColor=$bg;$tabs.TabPages.Add($ringTab)
$mapping=[Windows.Forms.TabPage]::new('  Hand Mapping  ');$mapping.BackColor=$bg;$tabs.TabPages.Add($mapping)
$gaze=[Windows.Forms.TabPage]::new('  Gaze Settings  ');$gaze.BackColor=$bg;$tabs.TabPages.Add($gaze)
$axes=[Windows.Forms.TabPage]::new('  Debug Axes  ');$axes.BackColor=$bg;$tabs.TabPages.Add($axes)
$c=Card 14 16 880 154;$setup.Controls.Add($c);$h=Label '1  CONNECTION' 20 12 250;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$c.Controls.Add($h);$c.Controls.Add((Label 'Run mode' 20 50 85))
$mode=[Windows.Forms.ComboBox]::new();$mode.DropDownStyle='DropDownList';$mode.Location=[Drawing.Point]::new(110,48);$mode.Size=[Drawing.Size]::new(250,28);[void]$mode.Items.Add('Quest Link / Unity Editor');[void]$mode.Items.Add('Standalone Quest');$mode.SelectedItem=[string]$cfg.defaultMode;$c.Controls.Add($mode)
$ipLabel=Label 'Quest IP' 385 50 70;$c.Controls.Add($ipLabel);$ip=[Windows.Forms.TextBox]::new();$ip.Text=[string]$cfg.standaloneQuestIp;$ip.Location=[Drawing.Point]::new(458,48);$ip.Size=[Drawing.Size]::new(175,27);$c.Controls.Add($ip)
$start=Button 'Start All + Unity Play' 655 39 205 44 $true;$c.Controls.Add($start);$stop=Button 'Stop Unity + Bridge' 655 94 205 34 $false $true;$c.Controls.Add($stop)
$steam=Label '[--] SteamVR  STOPPED' 20 101 220;$steam.Font=[Drawing.Font]::new('Segoe UI Semibold',9.5);$c.Controls.Add($steam);$bridge=Label '[--] Tracker Bridge  STOPPED' 250 101 250;$bridge.Font=$steam.Font;$c.Controls.Add($bridge);$ping=Button 'Ping Unity' 520 94 113 34;$ping.Add_Click({Send 'PING'});$c.Controls.Add($ping)
$w=Card 14 184 880 379;$setup.Controls.Add($w);$h=Label '2  SPATIAL ANCHOR WORKFLOW' 20 12 330;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$w.Controls.Add($h);$q=Label 'After pressing Play in Unity, complete these steps from top to bottom.' 365 15 470;$q.ForeColor=$muted;$w.Controls.Add($q)
$foregroundToggle=[Windows.Forms.CheckBox]::new();$foregroundToggle.Text='Foreground ON (hands / objects / handles / scale bar)';$foregroundToggle.Checked=$true;$foregroundToggle.Location=[Drawing.Point]::new(20,317);$foregroundToggle.Size=[Drawing.Size]::new(820,28);$foregroundToggle.ForeColor=$ink;$foregroundToggle.BackColor=[Drawing.Color]::Transparent;$foregroundToggle.Add_CheckedChanged({if($foregroundToggle.Checked){Send 'FOREGROUND_ON'}else{Send 'FOREGROUND_OFF'}});$w.Controls.Add($foregroundToggle)
$w.Height=389
$setup.AutoScroll=$true
$contactCorrectionToggle=[Windows.Forms.CheckBox]::new();$contactCorrectionToggle.Text='Object contact correction ON (uncheck: base expansion only)';$contactCorrectionToggle.Checked=$true;$contactCorrectionToggle.Location=[Drawing.Point]::new(20,347);$contactCorrectionToggle.Size=[Drawing.Size]::new(820,28);$contactCorrectionToggle.ForeColor=$ink;$contactCorrectionToggle.BackColor=[Drawing.Color]::Transparent;$contactCorrectionToggle.Add_CheckedChanged({if($contactCorrectionToggle.Checked){Send 'CONTACT_CORRECTION_ON'}else{Send 'CONTACT_CORRECTION_OFF'}});$w.Controls.Add($contactCorrectionToggle)
$b=Button '1. Begin Anchor Placement' 20 54 405 48 $true;$b.Add_Click({if($foregroundToggle.Checked){Send 'FOREGROUND_ON'}else{Send 'FOREGROUND_OFF'};if($contactCorrectionToggle.Checked){Send 'CONTACT_CORRECTION_ON'}else{Send 'CONTACT_CORRECTION_OFF'};Send 'BEGIN_ANCHOR_PLACEMENT'});$w.Controls.Add($b);$b=Button '2. Confirm Anchor' 445 54 405 48 $true;$b.Add_Click({Send 'CONFIRM_ANCHOR_PLACEMENT'});$w.Controls.Add($b)
$h=Label '3. Desk angle adjustment' 20 125 280;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$w.Controls.Add($h);$items=@(@('-5 deg','ROTATE_DESK_LEFT_LARGE'),@('-1 deg','ROTATE_DESK_LEFT'),@('+1 deg','ROTATE_DESK_RIGHT'),@('+5 deg','ROTATE_DESK_RIGHT_LARGE'));for($i=0;$i-lt4;$i++){$x=$items[$i];$b=Button $x[0] (20+$i*142) 160 126;$cmd=$x[1];$b.Add_Click({Send $cmd}.GetNewClosure());$w.Controls.Add($b)};$b=Button 'Reset angle' 588 160 126;$b.Add_Click({Send 'RESET_DESK_ROTATION'});$w.Controls.Add($b)
$b=Button '4. Confirm Desk + Start Spatial Anchor Mode' 20 222 830 52 $true;$b.Add_Click({Send 'CONFIRM_DESK_ALIGNMENT';[Threading.Thread]::Sleep(200);Send 'USE_SPATIAL_ANCHOR_REDIRECTION'});$w.Controls.Add($b)
$b=Button 'Gaze Debug ON' 20 279 160 34;$b.Add_Click({Send 'ENABLE_GAZE_DEBUG_VISUALS'});$w.Controls.Add($b);$b=Button 'Gaze Debug OFF' 192 279 160 34;$b.Add_Click({Send 'DISABLE_GAZE_DEBUG_VISUALS'});$w.Controls.Add($b)
$b=Button 'Next Participant / Reset Objects' 364 279 270 34 $false $true;$b.Add_Click({Send 'RESET_EXPERIENCE_FOR_NEXT_PARTICIPANT';Message 'Next Participant reset requested.'});$w.Controls.Add($b)
$b=Button 'Open Advanced Control' 650 279 200 34;$b.Add_Click({$p=Join-Path $root 'Tools\SpatialAnchorCalibration\pc_anchor_control_window.ps1';Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $p)) -WindowStyle Normal});$w.Controls.Add($b)

$ds=Card 14 16 880 310;$deskScaleTab.Controls.Add($ds);$h=Label 'MANUAL DESK SCALE' 24 18 480;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',13);$ds.Controls.Add($h);$q=Label 'Applies an exact scale through the same desk, 3DGS room, and hand-mapping path used by the VR slider.' 24 56 820 42;$q.ForeColor=$muted;$ds.Controls.Add($q)
$ds.Controls.Add((Label 'Target scale' 24 120 180));$manualDeskScale=[Windows.Forms.TextBox]::new();$manualDeskScale.Text='1';$manualDeskScale.Location=[Drawing.Point]::new(205,116);$manualDeskScale.Size=[Drawing.Size]::new(130,30);$manualDeskScale.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$ds.Controls.Add($manualDeskScale);$ds.Controls.Add((Label 'x' 342 120 30))
$disableDeskScaleBlackout=[Windows.Forms.CheckBox]::new();$disableDeskScaleBlackout.Text='Disable HMD blackout';$disableDeskScaleBlackout.Checked=$false;$disableDeskScaleBlackout.Location=[Drawing.Point]::new(400,116);$disableDeskScaleBlackout.Size=[Drawing.Size]::new(230,30);$disableDeskScaleBlackout.ForeColor=$ink;$disableDeskScaleBlackout.BackColor=[Drawing.Color]::Transparent;$ds.Controls.Add($disableDeskScaleBlackout)
$b=Button 'Load Current Scale' 24 180 220 44;$b.Add_Click({Send 'GET_DESK_SCALE'});$ds.Controls.Add($b);$b=Button 'Apply Manual Scale' 260 180 260 44 $true;$b.Add_Click({try{$v=TextNum $manualDeskScale;if([double]$v-le0){throw 'Scale must be greater than zero.'};$fade=if($disableDeskScaleBlackout.Checked){'0'}else{'1'};SaveLauncherState;Send "SET_DESK_SCALE $v $fade";$deskScaleState.Text=if($fade-eq'1'){'Blackout requested; waiting for Unity'}else{'Scale change requested'};$deskScaleState.ForeColor=$muted;Message "Manual desk scale sent: ${v}x"}catch{Message $_.Exception.Message $true}});$ds.Controls.Add($b)
$q=Label 'Default: fade to black for 1 second, change scale while black, then fade back for 1 second. Range: 1.00x - 3.00x.' 24 246 830 28;$q.ForeColor=$ink;$ds.Controls.Add($q);$deskScaleState=Label 'Unity Play is required' 550 190 280 24;$deskScaleState.ForeColor=$muted;$ds.Controls.Add($deskScaleState)

$ringTab.AutoScroll=$true;$rc=Card 14 16 880 1290;$ringTab.Controls.Add($rc);$h=Label 'SCALE PLACEMENT RING CHALLENGE' 24 14 600;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',13);$rc.Controls.Add($h);$q=Label 'DeskOrigin top view. Solid circles are Rings; dashed circles are characters behind each Ring.' 24 46 820 28;$q.ForeColor=$muted;$rc.Controls.Add($q)
$ringMap=[Windows.Forms.Panel]::new();$ringMap.Location=[Drawing.Point]::new(24,76);$ringMap.Size=[Drawing.Size]::new(820,342);$ringMap.BackColor=[Drawing.ColorTranslator]::FromHtml('#F8FAFC');$ringMap.BorderStyle='FixedSingle';$ringMap.Cursor='Hand';$ringMap.Add_Paint({param($s,$e) PaintRingMap $s $e});$ringMap.Add_MouseDown({param($s,$e)$id=HitTestRingMap $e.X $e.Y;if($id){$script:dragRing=$id;$script:selectedRingPattern=$id;$ringPatternDisplay.Text="PATTERN $id";$row=RingSettingsRow $id;if($row){$ringSettingsGrid.ClearSelection();$row.Selected=$true};$s.Invalidate()}});$ringMap.Add_MouseMove({param($s,$e)if($script:dragRing-and$e.Button-eq[Windows.Forms.MouseButtons]::Left){$v=$script:ringLayouts[$script:dragRing];$v.X=[math]::Round([math]::Max(-1.3,[math]::Min(1.3,($e.X-$script:ringMapCenterX)/$script:ringMapPixelsPerMeter)),3);$v.Z=[math]::Round([math]::Max(-1.3,[math]::Min(1.3,($script:ringMapCenterY-$e.Y)/$script:ringMapPixelsPerMeter)),3);RefreshApproxCharacterLayouts;$ringPatternState.Text="Dragging $script:dragRing : X=$($v.X) Z=$($v.Z) m";$s.Invalidate()}});$ringMap.Add_MouseUp({param($s,$e)if($script:dragRing){$id=$script:dragRing;$v=$script:ringLayouts[$id];$script:dragRing=$null;SaveLauncherState;Send ("SET_TARGET_RING_POSITION {0} {1} {2}" -f $id,$v.X.ToString('R',[Globalization.CultureInfo]::InvariantCulture),$v.Z.ToString('R',[Globalization.CultureInfo]::InvariantCulture));$ringPatternState.Text="Position $id sent to Unity";$s.Invalidate()}});$rc.Controls.Add($ringMap)
$ringPatternDisplay=Label 'PATTERN A' 24 436 820 50;$ringPatternDisplay.TextAlign='MiddleCenter';$ringPatternDisplay.Font=[Drawing.Font]::new('Segoe UI Semibold',20);$ringPatternDisplay.ForeColor=$white;$ringPatternDisplay.BackColor=$blue;$rc.Controls.Add($ringPatternDisplay)
$items=@(@('Pattern A - Right','A'),@('Pattern B - Front','B'),@('Pattern C - Left','C'));for($i=0;$i-lt3;$i++){$item=$items[$i];$b=Button $item[0] (24+$i*274) 502 254 42 $true;$pattern=$item[1];$b.Add_Click({try{$script:selectedRingPattern=$pattern;$row=RingSettingsRow $pattern;if(!$row){throw "Pattern $pattern row was not found."};$ringSettingsGrid.ClearSelection();$row.Selected=$true;SendRingSettingsRow $row;$reset=if($resetSizesOnRingSwitch.Checked){'1'}else{'0'};$ringPatternDisplay.Text="PATTERN $pattern";$ringPatternState.Text='Settings applied; switch requested';SaveLauncherState;Send "SET_TARGET_RING_PATTERN $pattern $reset";Send "GET_TARGET_RING_SETTINGS $pattern";Send 'GET_TARGET_RING_LAYOUT';$ringMap.Invalidate()}catch{Message $_.Exception.Message $true}}.GetNewClosure());$rc.Controls.Add($b)}
$resetSizesOnRingSwitch=[Windows.Forms.CheckBox]::new();$resetSizesOnRingSwitch.Text='Reset desk and object sizes when switching';$resetSizesOnRingSwitch.Checked=$true;$resetSizesOnRingSwitch.Location=[Drawing.Point]::new(24,554);$resetSizesOnRingSwitch.Size=[Drawing.Size]::new(390,28);$resetSizesOnRingSwitch.ForeColor=$ink;$resetSizesOnRingSwitch.BackColor=[Drawing.Color]::Transparent;$rc.Controls.Add($resetSizesOnRingSwitch)
$ringUniformScale=[Windows.Forms.CheckBox]::new();$ringUniformScale.Text='Copy X target scale to Y / Z when applying';$ringUniformScale.Checked=$true;$ringUniformScale.Location=[Drawing.Point]::new(458,554);$ringUniformScale.Size=[Drawing.Size]::new(370,28);$ringUniformScale.ForeColor=$ink;$ringUniformScale.BackColor=[Drawing.Color]::Transparent;$rc.Controls.Add($ringUniformScale)
$ringSettingsGrid=[Windows.Forms.DataGridView]::new();$ringSettingsGrid.Location=[Drawing.Point]::new(24,588);$ringSettingsGrid.Size=[Drawing.Size]::new(820,155);$ringSettingsGrid.BackgroundColor=$white;$ringSettingsGrid.BorderStyle='None';$ringSettingsGrid.RowHeadersVisible=$false;$ringSettingsGrid.AllowUserToAddRows=$false;$ringSettingsGrid.AllowUserToDeleteRows=$false;$ringSettingsGrid.SelectionMode='FullRowSelect';$ringSettingsGrid.MultiSelect=$false;$ringSettingsGrid.AutoSizeColumnsMode='Fill';$ringSettingsGrid.ColumnHeadersHeight=38;$ringSettingsGrid.RowTemplate.Height=36;$ringSettingsGrid.EnableHeadersVisualStyles=$false;$ringSettingsGrid.ColumnHeadersDefaultCellStyle.BackColor=$navy;$ringSettingsGrid.ColumnHeadersDefaultCellStyle.ForeColor=$white;$ringSettingsGrid.DefaultCellStyle.SelectionBackColor=[Drawing.ColorTranslator]::FromHtml('#DBEAFE');$ringSettingsGrid.DefaultCellStyle.SelectionForeColor=$ink
$patternColumn=[Windows.Forms.DataGridViewTextBoxColumn]::new();$patternColumn.Name='Pattern';$patternColumn.HeaderText='Pattern';$patternColumn.ReadOnly=$true;[void]$ringSettingsGrid.Columns.Add($patternColumn)
$targetColumn=[Windows.Forms.DataGridViewComboBoxColumn]::new();$targetColumn.Name='Target';$targetColumn.HeaderText='Target Object';$targetColumn.FlatStyle='Flat';[void]$targetColumn.Items.AddRange([object[]]@('1','3','4'));[void]$ringSettingsGrid.Columns.Add($targetColumn)
foreach($x in @(@('SX','Scale X'),@('SY','Scale Y'),@('SZ','Scale Z'),@('TX','Size Tol X'),@('TY','Size Tol Y'),@('TZ','Size Tol Z'),@('PX','Pos Tol X'),@('PY','Height ignored'),@('PZ','Pos Tol Z'))){[void]$ringSettingsGrid.Columns.Add($x[0],$x[1])};$ringSettingsGrid.Columns[9].ReadOnly=$true;$ringSettingsGrid.Columns[0].FillWeight=54;$ringSettingsGrid.Columns[1].FillWeight=92
foreach($r in @(@('A','1','1.35','1.35','1.35','0.10','0.10','0.10','0.09','0.10','0.09'),@('B','3','1.70','1.70','1.70','0.10','0.10','0.10','0.09','0.10','0.09'),@('C','4','2.10','2.10','2.10','0.10','0.10','0.10','0.09','0.10','0.09'))){[void]$ringSettingsGrid.Rows.Add($r)};$ringSettingsGrid.Rows[0].Selected=$true;$rc.Controls.Add($ringSettingsGrid)
function SendRingSettingsRow($r){$id=[string]$r.Cells[0].Value;$target=[string]$r.Cells[1].Value;if($target-notin@('1','3','4')){throw 'Select target object 1, 3, or 4.'};$v=for($i=2;$i-le10;$i++){Num $r $i};if($ringUniformScale.Checked){$v[1]=$v[0];$v[2]=$v[0];$r.Cells[3].Value=$v[0];$r.Cells[4].Value=$v[0]};if([double]$v[0]-le0-or[double]$v[1]-le0-or[double]$v[2]-le0){throw 'Target scales must be greater than zero.'};for($i=3;$i-le5;$i++){if([double]$v[$i]-lt0){throw 'Size tolerances cannot be negative.'}};if([double]$v[6]-le0-or[double]$v[8]-le0){throw 'X/Z position tolerances must be greater than zero.'};UpdateRingMapSizeFromRow $r;Send ("SET_TARGET_RING_SETTINGS {0} {1} {2}" -f $id,$target,($v-join' '))}
$b=Button 'Load Settings + Layout' 24 758 190 40;$b.Add_Click({Send 'GET_TARGET_RING_SETTINGS A';Send 'GET_TARGET_RING_SETTINGS B';Send 'GET_TARGET_RING_SETTINGS C';Send 'GET_TARGET_RING_LAYOUT';Send 'GET_TARGET_RING_CHARACTERS'});$rc.Controls.Add($b)
$b=Button 'Apply Selected' 228 758 190 40 $true;$b.Add_Click({try{$r=SelectedRingSettingsRow;SendRingSettingsRow $r;SaveLauncherState;Send 'GET_TARGET_RING_LAYOUT';$ringPatternState.Text="Pattern $($r.Cells[0].Value) settings sent";$ringPatternState.ForeColor=$muted}catch{Message $_.Exception.Message $true}});$rc.Controls.Add($b)
$b=Button 'Apply All + Save' 432 758 190 40 $true;$b.Add_Click({try{foreach($r in $ringSettingsGrid.Rows){SendRingSettingsRow $r};SaveLauncherState;Send 'GET_TARGET_RING_LAYOUT';$ringPatternState.Text='A / B / C settings sent';$ringPatternState.ForeColor=$muted}catch{Message $_.Exception.Message $true}});$rc.Controls.Add($b)
$b=Button 'Ring Task OFF' 636 758 208 40 $false $true;$b.Add_Click({Send 'DISABLE_TARGET_RING_CHALLENGE'});$rc.Controls.Add($b)
$q=Label 'Targets: 1 CubeWarped / 3 QuestCase / 4 Gun. Diagram size uses Unity actual ring radii after Play.' 24 815 820 26;$q.ForeColor=$ink;$rc.Controls.Add($q);$ringPatternState=Label 'Drag a ring to save its DeskOrigin-relative X/Z position' 24 859 800 24;$ringPatternState.ForeColor=$muted;$rc.Controls.Add($ringPatternState)

$h=Label 'CHARACTERS — PANDA / GORILLA / ELEPHANT' 24 902 600;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$rc.Controls.Add($h)
$q=Label 'Small / middle / large Ring assignment is automatic. Size is the character horizontal diameter divided by Ring diameter.' 24 930 820 24;$q.ForeColor=$muted;$rc.Controls.Add($q)
$characterSettingsGrid=[Windows.Forms.DataGridView]::new();$characterSettingsGrid.Location=[Drawing.Point]::new(24,958);$characterSettingsGrid.Size=[Drawing.Size]::new(820,148);$characterSettingsGrid.BackgroundColor=$white;$characterSettingsGrid.BorderStyle='None';$characterSettingsGrid.RowHeadersVisible=$false;$characterSettingsGrid.AllowUserToAddRows=$false;$characterSettingsGrid.AllowUserToDeleteRows=$false;$characterSettingsGrid.SelectionMode='FullRowSelect';$characterSettingsGrid.MultiSelect=$false;$characterSettingsGrid.AutoSizeColumnsMode='Fill';$characterSettingsGrid.ColumnHeadersHeight=38;$characterSettingsGrid.RowTemplate.Height=34;$characterSettingsGrid.EnableHeadersVisualStyles=$false;$characterSettingsGrid.ColumnHeadersDefaultCellStyle.BackColor=$navy;$characterSettingsGrid.ColumnHeadersDefaultCellStyle.ForeColor=$white;$characterSettingsGrid.DefaultCellStyle.SelectionBackColor=[Drawing.ColorTranslator]::FromHtml('#DCFCE7');$characterSettingsGrid.DefaultCellStyle.SelectionForeColor=$ink
[void]$characterSettingsGrid.Columns.Add('Character','Character');[void]$characterSettingsGrid.Columns.Add('Ring','Assigned Ring');[void]$characterSettingsGrid.Columns.Add('Multiplier','Character / Ring Size');$characterSettingsGrid.Columns[0].ReadOnly=$true;$characterSettingsGrid.Columns[1].ReadOnly=$true
foreach($r in @(@('PANDA','A','1.0'),@('GORILLA','B','1.0'),@('ELEPHANT','C','1.0'))){[void]$characterSettingsGrid.Rows.Add($r)};$characterSettingsGrid.Rows[0].Selected=$true;$rc.Controls.Add($characterSettingsGrid)
function SendCharacterMultiplierRow($r){$id=[string]$r.Cells[0].Value;$value=Num $r 2;if([double]$value-le0-or[double]$value-gt20){throw 'Character / Ring multiplier must be greater than 0 and at most 20.'};$script:characterLayouts[$id].Multiplier=[double]$value;RefreshApproxCharacterLayouts;Send "SET_TARGET_RING_CHARACTER_MULTIPLIER $id $value"}
$b=Button 'Load Character Settings' 24 1120 230 40;$b.Add_Click({Send 'GET_TARGET_RING_CHARACTERS'});$rc.Controls.Add($b)
$b=Button 'Apply Selected Character' 270 1120 250 40 $true;$b.Add_Click({try{if($characterSettingsGrid.SelectedRows.Count-eq0){throw 'Select one character row first.'};SendCharacterMultiplierRow $characterSettingsGrid.SelectedRows[0];SaveLauncherState;$ringMap.Invalidate()}catch{Message $_.Exception.Message $true}});$rc.Controls.Add($b)
$b=Button 'Apply All Characters + Save' 536 1120 308 40 $true;$b.Add_Click({try{foreach($r in $characterSettingsGrid.Rows){SendCharacterMultiplierRow $r};SaveLauncherState;$ringMap.Invalidate()}catch{Message $_.Exception.Message $true}});$rc.Controls.Add($b)
$rc.Controls.Add((Label 'Global character vertical-axis rotation offset' 24 1180 350))
$characterYawOffset=[Windows.Forms.TextBox]::new();$characterYawOffset.Text='0';$characterYawOffset.Location=[Drawing.Point]::new(380,1176);$characterYawOffset.Size=[Drawing.Size]::new(110,29);$characterYawOffset.Font=[Drawing.Font]::new('Segoe UI Semibold',10);$rc.Controls.Add($characterYawOffset);$rc.Controls.Add((Label 'degrees' 498 1180 70))
$b=Button 'Load Yaw' 580 1172 110 38;$b.Add_Click({Send 'GET_TARGET_RING_CHARACTER_YAW'});$rc.Controls.Add($b)
$b=Button 'Apply Yaw + Save' 704 1172 140 38 $true;$b.Add_Click({try{$v=TextNum $characterYawOffset;SaveLauncherState;Send "SET_TARGET_RING_CHARACTER_YAW $v";$ringPatternState.Text="Character yaw offset sent: $v degrees"}catch{Message $_.Exception.Message $true}});$rc.Controls.Add($b)
$q=Label '0 degrees faces RedirectionOrigin using each model local +Z. Use 180 if the imported models face backward.' 24 1224 820 26;$q.ForeColor=$muted;$rc.Controls.Add($q)

$m=Card 14 16 880 270;$mapping.Controls.Add($m);$h=Label 'HAND MAPPING SCALE CHANGE' 24 18 480;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',13);$m.Controls.Add($h);$q=Label 'Controls how strongly redirected-hand mapping responds to object enlargement or reduction.' 24 56 800 28;$q.ForeColor=$muted;$m.Controls.Add($q)
$m.Controls.Add((Label 'Hand mapping / object scale-change multiplier' 24 103 360));$handScale=[Windows.Forms.TextBox]::new();$handScale.Text='1';$handScale.Location=[Drawing.Point]::new(390,99);$handScale.Size=[Drawing.Size]::new(120,29);$handScale.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$m.Controls.Add($handScale)
$b=Button 'Load' 530 95 90 38;$b.Add_Click({Send 'GET_HAND_MAPPING_SCALE_MULTIPLIER'});$m.Controls.Add($b);$b=Button 'Apply + Save' 632 95 130 38 $true;$b.Add_Click({try{$v=TextNum $handScale;if([double]$v-lt0){throw 'Multiplier must be zero or greater.'};Send "SET_HAND_MAPPING_SCALE_MULTIPLIER $v";Message "Hand mapping scale-change multiplier sent: ${v}x"}catch{Message $_.Exception.Message $true}});$m.Controls.Add($b);$b=Button 'Reset 1x' 772 95 85 38;$b.Add_Click({$handScale.Text='1';Send 'RESET_HAND_MAPPING_SCALE_MULTIPLIER'});$m.Controls.Add($b)
$q=Label '1.0x = match object scale change    2.0x = twice the scale-change amount    0.0x = no scale-change mapping' 24 158 820 26;$q.ForeColor=$ink;$m.Controls.Add($q);$q=Label 'Example: if an object changes from 1.0x to 1.5x, 2.0x maps the hand as 2.0x (1 + (1.5 - 1) * 2).' 24 190 820 26;$q.ForeColor=$muted;$m.Controls.Add($q);$handScaleState=Label 'Unity Play is required' 24 229 360 24;$handScaleState.ForeColor=$muted;$m.Controls.Add($handScaleState)

$gr=Card 14 16 880 420;$gaze.Controls.Add($gr);$h=Label 'GAZE TARGET SPHERES — PER OBJECT' 24 18 520;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',13);$gr.Controls.Add($h);$q=Label 'Select an object and set its HMD gaze-recognition sphere radius in meters.' 24 52 800 28;$q.ForeColor=$muted;$gr.Controls.Add($q)
$gazeGrid=[Windows.Forms.DataGridView]::new();$gazeGrid.Location=[Drawing.Point]::new(24,88);$gazeGrid.Size=[Drawing.Size]::new(500,190);$gazeGrid.BackgroundColor=$white;$gazeGrid.BorderStyle='None';$gazeGrid.RowHeadersVisible=$false;$gazeGrid.AllowUserToAddRows=$false;$gazeGrid.AllowUserToDeleteRows=$false;$gazeGrid.SelectionMode='FullRowSelect';$gazeGrid.MultiSelect=$false;$gazeGrid.AutoSizeColumnsMode='Fill';$gazeGrid.ColumnHeadersHeight=38;$gazeGrid.RowTemplate.Height=42;$gazeGrid.EnableHeadersVisualStyles=$false;$gazeGrid.ColumnHeadersDefaultCellStyle.BackColor=$navy;$gazeGrid.ColumnHeadersDefaultCellStyle.ForeColor=$white;$gazeGrid.DefaultCellStyle.SelectionBackColor=[Drawing.ColorTranslator]::FromHtml('#DBEAFE');$gazeGrid.DefaultCellStyle.SelectionForeColor=$ink
[void]$gazeGrid.Columns.Add('Id','Object ID');[void]$gazeGrid.Columns.Add('Radius','Radius (m)');$gazeGrid.Columns[0].ReadOnly=$true;foreach($r in @(@('1','0.2'),@('3','0.2'),@('4','0.2'))){[void]$gazeGrid.Rows.Add($r)};$gazeGrid.Rows[0].Selected=$true;$gr.Controls.Add($gazeGrid)
$b=Button 'Load All' 550 88 270 42;$b.Add_Click({Send 'GET_GAZE_TARGET_RADIUS'});$gr.Controls.Add($b);$b=Button 'Apply Selected + Save' 550 146 270 42 $true;$b.Add_Click({try{$r=GazeRow;$id=[string]$r.Cells[0].Value;$v=Num $r 1;if([double]$v-lt0){throw 'Radius must be zero or greater.'};Send "SET_GAZE_TARGET_RADIUS $id $v";Message "Object $id gaze radius sent: $v m"}catch{Message $_.Exception.Message $true}});$gr.Controls.Add($b);$b=Button 'Reset Selected to 0.20 m' 550 204 270 42;$b.Add_Click({try{$r=GazeRow;$id=[string]$r.Cells[0].Value;$r.Cells[1].Value='0.2';Send "RESET_GAZE_TARGET_RADIUS $id"}catch{Message $_.Exception.Message $true}});$gr.Controls.Add($b)
$q=Label 'Only gaze selection changes. Orange/blue Near/Far blend shells are unchanged.' 24 306 800 28;$q.ForeColor=$ink;$gr.Controls.Add($q);$gazeRadiusState=Label 'Unity Play is required' 24 352 360 24;$gazeRadiusState.ForeColor=$muted;$gr.Controls.Add($gazeRadiusState)

$g=Card 14 16 880 139;$offset.Controls.Add($g);$h=Label 'DETECTED TRACKER POSES — DESKORIGIN LOCAL OFFSET' 20 10 500;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$g.Controls.Add($h);$q=Label 'Moves both detected-pose axes and objects. To raise them 5 cm, set Y to +0.05.' 20 38 590 24;$q.ForeColor=$muted;$g.Controls.Add($q)
$groupX=[Windows.Forms.TextBox]::new();$groupY=[Windows.Forms.TextBox]::new();$groupZ=[Windows.Forms.TextBox]::new();$boxes=@(@('X',$groupX),@('Y',$groupY),@('Z',$groupZ));for($i=0;$i-lt3;$i++){$g.Controls.Add((Label $boxes[$i][0] (20+$i*145) 76 22));$boxes[$i][1].Text='0';$boxes[$i][1].Location=[Drawing.Point]::new((43+$i*145),73);$boxes[$i][1].Size=[Drawing.Size]::new(105,28);$g.Controls.Add($boxes[$i][1])}
$b=Button 'Load' 475 69 105 36;$b.Add_Click({Send 'GET_TRACKER_GROUP_OFFSET'});$g.Controls.Add($b);$b=Button 'Apply + Save' 590 69 125 36 $true;$b.Add_Click({try{$x=TextNum $groupX;$y=TextNum $groupY;$z=TextNum $groupZ;Send "SET_TRACKER_GROUP_OFFSET $x $y $z";Message "Common DeskOrigin tracker offset sent: X=$x Y=$y Z=$z m"}catch{Message $_.Exception.Message $true}});$g.Controls.Add($b);$b=Button 'Reset' 725 69 115 36 $false $true;$b.Add_Click({$groupX.Text='0';$groupY.Text='0';$groupZ.Text='0';Send 'RESET_TRACKER_GROUP_OFFSET'});$g.Controls.Add($b);$groupState=Label 'Unity Play is required' 590 108 250 22;$groupState.ForeColor=$muted;$g.Controls.Add($groupState)

$o=Card 14 165 880 398;$offset.Controls.Add($o);$h=Label 'PER-OBJECT TRACKER OFFSETS' 20 10 360;$h.Font=[Drawing.Font]::new('Segoe UI Semibold',11);$o.Controls.Add($h);$q=Label 'Optional tracker-center and rotation correction for each object.' 390 13 450 24;$q.ForeColor=$muted;$o.Controls.Add($q)
$grid=[Windows.Forms.DataGridView]::new();$grid.Location=[Drawing.Point]::new(20,43);$grid.Size=[Drawing.Size]::new(838,240);$grid.BackgroundColor=$white;$grid.BorderStyle='None';$grid.GridColor=$line;$grid.RowHeadersVisible=$false;$grid.AllowUserToAddRows=$false;$grid.AllowUserToDeleteRows=$false;$grid.SelectionMode='FullRowSelect';$grid.MultiSelect=$false;$grid.AutoSizeColumnsMode='Fill';$grid.ColumnHeadersHeight=38;$grid.RowTemplate.Height=38;$grid.EnableHeadersVisualStyles=$false;$grid.ColumnHeadersDefaultCellStyle.BackColor=$navy;$grid.ColumnHeadersDefaultCellStyle.ForeColor=$white;$grid.DefaultCellStyle.SelectionBackColor=[Drawing.ColorTranslator]::FromHtml('#DBEAFE');$grid.DefaultCellStyle.SelectionForeColor=$ink
foreach($x in @(@('Id','ID'),@('Name','Unity target'),@('PX','Pos X'),@('PY','Pos Y'),@('PZ','Pos Z'),@('RX','Rot X'),@('RY','Rot Y'),@('RZ','Rot Z'))){[void]$grid.Columns.Add($x[0],$x[1])};$grid.Columns[0].ReadOnly=$true;$grid.Columns[0].FillWeight=40;$grid.Columns[1].ReadOnly=$true;$grid.Columns[1].FillWeight=145
foreach($r in @(@(1,'cubeRelativeToHmd',0,-0.04,-0.07,0,0,0),@(2,'Object ID 2',0,0,0,0,0,0),@(3,'cubeRelativeToHmd (2)',0.02,-0.02,-0.02,0,0,0),@(4,'cubeRelativeToHmd (3)',0,0,0,0,0,0))){[void]$grid.Rows.Add($r)};$grid.Rows[0].Selected=$true;$o.Controls.Add($grid)
$b=Button 'Load from Unity' 20 300 190 42 $true;$b.Add_Click({Send 'GET_TRACKER_OFFSETS'});$o.Controls.Add($b);$b=Button 'Apply selected row' 224 300 210 42 $true;$b.Add_Click({try{$r=Row;$id=[int]$r.Cells[0].Value;$v=for($i=2;$i -lt 8;$i++){Num $r $i};Send ("SET_TRACKER_OFFSET {0} {1}" -f $id,($v -join ' '));Message "Object ID $id offset sent and saved."}catch{Message $_.Exception.Message $true}});$o.Controls.Add($b);$b=Button 'Reset selected to zero' 448 300 210 42 $false $true;$b.Add_Click({try{$r=Row;Send "RESET_TRACKER_OFFSET $([int]$r.Cells[0].Value)"}catch{Message $_.Exception.Message $true}});$o.Controls.Add($b);$q=Label 'Green rows were received from Unity. ID 2 may report an error if the Scene has no target.' 20 356 820;$q.ForeColor=$muted;$o.Controls.Add($q)

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
        SaveLauncherState
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
    LoadLauncherState
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
    SaveLauncherState
    $timer.Stop()
    if ($script:statusClient) {
        $script:statusClient.Close()
        $script:statusClient = $null
    }
})
[void]$form.ShowDialog()
