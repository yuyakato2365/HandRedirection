# Scene配置オブジェクト説明書

対象Scene: `Assets/Samples/Meta XR Interaction SDK/78.0.0/Example Scenes/HandGrabExamples.unity`

## 主要なRootオブジェクト

| オブジェクト | 役割 | 取り扱い |
|---|---|---|
| `HandRedirection` | XR Rig、左右手の入力、リダイレクション計算をまとめる本体 | 削除しない |
| `CubeWarped` | リダイレクション対象1 | tracker_bridge4のObject ID 1に対応 |
| `QuestCase` | リダイレクション対象2 | Object ID 2に対応 |
| `Gun` | リダイレクション対象3 | Object ID 3に対応 |
| `cubeRelativeToHmd`、`(2)`、`(3)` | Vive Trackerから受信した実空間側の基準姿勢 | 対応する対象物との参照を保つ |
| `CubeWoeldEmpty`、`(1)` | キャリブレーション後の座標や中間Transform | 既存参照があるため、名称変更前にInspectorを確認する |
| `CalibrationManager` | Vive Tracker座標とUnity座標の対応付け | 削除しない |
| `DeskOrigin` | 机を基準にした座標、机モデル、スケールUI、色UIの親 | 新しい机を設定するときの基準 |
| `3DGS_Room` | 部屋関係をまとめる親。旧名は`Scaniverse 2026-04-07 210739` | 名前は任意。現在の実表示・変形対象は子の`計算機室` |
| `PassthroughRuntimeGuard` | 部屋表示、パススルー、手元だけをパススルーにする処理の起点 | `Scaniverse Targets`を空にしない |
| `SpatialAnchorCalibrationRoot` | Spatial Anchor配置、机との位置合わせ、UDP操作、モード切替 | Quest実機での位置合わせに使用 |
| `VRObjectTransform` | VR内オブジェクトの姿勢変換 | 参照を保つ |
| `GrabDetector` | 掴み状態の検出 | 参照を保つ |
| `KnobReceiver` | 外部ノブ入力のUDP受信 | ノブを使用しない場合も、現行Sceneではそのまま残す |

## Play中に自動生成されるもの

`HandLocalScaniverseOcclusion`、`PassthroughScaniverseModeController`、`AnchorPlacementSceneFader`、Anchor状態表示用オブジェクトなどは、必要に応じて実行時に生成されます。Play中にHierarchyへ現れても、Sceneへ手動保存したり削除したりする必要はありません。

## 名前を変更してよいもの

`3DGS_Room`、その子の部屋表示オブジェクト、机モデルの名前は変更できます。動作を決めるのは名前ではなく、`Tools > Hand Redirection > Environment Setup`で登録したTransform参照です。現在はRoomに`計算機室`、Deskに`minitable`が登録されています。黄色または赤の警告が出た場合は、RoomとDeskを指定して`Apply + Validate`を実行してください。

`CubeWarped`などリダイレクション対象の既存名は、コード・Inspector・運用上の対応表を同時に確認できるまで変更しないでください。
