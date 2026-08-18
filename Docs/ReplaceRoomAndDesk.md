# 部屋3DGS・机を新調する手順

## 事前条件

- 新しい部屋データは、全体をまとめる親GameObjectを1つ作ります。名前は`3DGS_Room`など任意です。
- Environment SetupのRoomには、Gaussian Splat Rendererなど実際の部屋表示を持つGameObject、またはそれだけを内包する親を指定します。机まで含む共通親は指定しません。
- 新しい机は、見た目の幅を取得できるRendererを子孫に持たせます。
- 机の回転、原点、初期Scaleを完成状態にしてから登録します。この状態がスケールバーの基準になります。

## 設定

1. 新しい部屋3DGSと机モデルをProjectへImportし、対象Sceneへ配置します。
2. Unityメニューの`Tools > Hand Redirection > Environment Setup`を開きます。
3. `Room 3DGS`へ新しい部屋の表示GameObjectをドラッグします。現在のSceneでいう`計算機室`に相当します。
4. `Desk`へ新しい机のGameObjectをドラッグします。
5. 古い部屋を自動で無効化したい場合は`Disable Previously Configured Rooms`をONにします。
6. `Apply + Validate`を押します。
7. Consoleの結果が成功であることを確認し、Sceneを保存します。

この操作で次がまとめて更新されます。

- Play開始時に有効化する部屋
- 手元パススルーの対象となる部屋
- スケールバーが基準幅を取得する机
- スケール変更と同時に水平変形する部屋3DGS
- 日時形式の旧Scaniverse名に依存する自動検索の無効化

## 動作確認

1. Playして新しい部屋だけが表示されることを確認します。
2. 手を部屋の前へ出し、手元がパススルー表示になることを確認します。
3. スケールバーを動かし、机の幅と部屋の机部分が同時に水平方向へ変形することを確認します。
4. Anchor Quick Controlで位置合わせを行い、机と実空間が一致することを確認します。
5. ConsoleにMissing ReferenceやNullReferenceがないことを確認します。

## 名前変更について

親を`3DGS_Room`という名前へ変更したことは問題ありません。現在の設定では、実際の処理対象である子の`計算機室`と机の`minitable`がTransform参照で登録されています。さらに別の名前へ変更しても動作します。ただし、複製や入れ替えによって参照先が変わった場合は、もう一度Environment Setupを実行してください。

## 机の幅が合わないとき

- `Desk`に机全体ではなく天板だけを登録していないか確認します。
- 非表示の大きなRendererが机の子に入っていないか確認します。
- 登録前に机のScaleが意図した基準値になっているか確認します。
- 修正後に`Apply + Validate`を再実行し、Sceneを保存します。
