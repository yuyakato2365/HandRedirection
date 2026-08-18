# ハンドリダイレクション簡単起動手順

## 通常の起動

1. QuestをPCへ接続し、Quest Linkを開始します。
2. プロジェクト直下の`StartHandRedirection.cmd`をダブルクリックします。
3. `Quest Link / Unity Editor`を選びます。Quest単体ビルドへ送る場合だけ`Standalone Quest`を選び、QuestのIPを入力します。
4. `Start SteamVR + Tracker Bridge + Anchor Control`を押します。
5. SteamVRでHMDと使用するVive Trackerが認識されるまで待ちます。
6. Unityで対象Sceneを開き、Playを押します。
7. 自動で開いた`Spatial Anchor - Quick Setup`を上から順に操作します。
   - `1. Begin Anchor Placement`
   - Quest内で位置を合わせる
   - `2. Confirm Anchor`
   - 必要なら角度を`-5 / -1 / +1 / +5`で微調整
   - `4. Confirm Desk + Start Spatial Anchor Mode`

以上でハンドリダイレクションを開始できます。

## 終了

1. UnityのPlayを停止します。
2. Launcherの`Stop Tracker Bridge`を押します。
3. SteamVRが不要ならSteamVR側で終了します。

## 補助操作

- 視線の先の確認表示: Quick Controlの`Gaze Debug ON / OFF`
- Anchorの全コマンド: `StartSpatialAnchorAdvancedControl.cmd`
- Anchorだけ開く: `StartSpatialAnchorControl.cmd`

## 起動できないとき

- LauncherのSteamVRとTracker Bridgeが`RUNNING`か確認します。
- SteamVRでTrackerがすべて緑色か確認します。
- Quest単体の場合は、PCとQuestが同一ネットワークか、Quest IPが変わっていないか確認します。
- Tracker Bridgeのログは`Logs/TrackerBridge`に保存されます。
- 使用ポートはTracker送信がUDP 9000、ACKが9001、Anchorコマンドが9101、Anchor状態が9102です。
