# ぽこぽこファーム 釣り自動化(.NET 10・単一 exe)

使い方と仕組みの概要は [../README.md](../README.md)。ここはビルド、構成、運用の詳細。

## 作る・動かす

```powershell
cd csharp
dotnet test -c Release                                                  # 単体テスト
dotnet publish src/Pocofarm -c Release -r win-x64 --self-contained -o dist   # 単一 exe(約 90MB)
dist\pocofarm.exe all --root ..                                          # 釣り+ダッシュボードを常駐(落ちたら自動再起動)
```

| コマンド | 内容 |
|---|---|
| `pocofarm all [--root <dir>]` | `run` を子プロセスで起動し、落ちたら 5 秒後に再起動 |
| `pocofarm run [--root <dir>] [--host <ip>] [--port <n>] [--dry-run]` | 釣り+ダッシュボード。`--dry-run` はクリックを送らない(並走して挙動を比べる用) |
| `pocofarm bench-ocr [dir] [-v]` | OCR エンジン(Windows OCR / PP-OCR ONNX)を固定サンプルで比較 |
| `pocofarm capture-test [out.png]` | VRChat を数秒キャプチャし、サイズ・fps・ウキ検出を表示 |
| `pocofarm ocr-image <png>` | 画像 1 枚を Windows OCR で読み、トースト分類を表示 |
| `pocofarm cleanup` / `init-config` | 証拠画像の掃除 / 既定の config.json 出力 |

## 設定(config.json / ダッシュボード)

動作に関わる数値はすべて `config.json`(snake_case のキー、無いものは既定値)で変えられる。ダッシュボードの「設定」カードで一覧・説明つきで編集でき、保存すると `config.json` へ書き出される。「再起動」付きの項目は、釣りプロセスを再起動すると反映される(それ以外は即反映)。

```bash
curl http://<host>:10000/api/config                                   # 全項目(キー・説明・現在値・既定値・再起動の要否)
curl -X POST http://<host>:10000/api/config -d '{"farm_shot_interval":900,"pecks_to_reel":null}'   # null は既定値へ戻す
```

不正な値(未知のキー・負数・配列の要素数違い・型違い)は 400 で拒否し、何も変えない。変更は LAN(プライベートアドレス)からのみ。

`--root` は `config.json` と `data/` がある場所(省略時はカレント)。

## 自動入れ替え(常駐の exe を止めずに更新)

常駐(`pocofarm all`)は、exe と同じ場所に `pocofarm.new.exe` が置かれると、釣りプロセスを止めて自分を入れ替え(旧版は `pocofarm.exe.old`。起動時と `exe_cleanup_interval` ごとに、残った `*.old`・`*.failed`・放置された `.tmp` を自動で削除する)、同じ引数で新しい常駐を起動する。Windows は実行中の exe を改名できるので、手で止める必要はない。前回が一時停止中なら一時停止のまま起動する。

- 手動: 新しい exe を `pocofarm.new.exe` という名前で置く(コピー完了後に名前を付けるなら、`.tmp` で置いてから改名)。
- 自動: `config.json` の `update_source` に新しい exe のパス(UNC 可)を指定すると(config.json を直接編集する。セキュリティ上、ダッシュボードからは変更できない)、`update_check_seconds` ごとに確認し、更新日時が新しければ取り込んで入れ替える。

## 構成

| 場所 | 役割 |
|---|---|
| `src/Pocofarm/Program.cs`, `Cli/` | エントリポイントとサブコマンド(`Supervisor`=常駐、`RunCommand`=本体、`Diagnostics`=点検、`Maintenance`=掃除・設定) |
| `Core/` | 画面に依存しない部分: `Config*.cs`(設定。領域ごとに分割)、`Layout`(解像度ごとの範囲)、`Toast`(分類)、`Stats`、`EventLog`、`Status`、`AtomicFile`、`Paths`(data 以下の場所) |
| `Vision/` | `BobberDetector`(赤い棒+白い玉の検出)、`DipDetector`(静止と沈みの判定)、`BobberParams`(検出・判定のしきい値。設定から作る)、`ViewMotion`(視点移動)、`Hud`(HUD の範囲)、`IconFinder`(UI アイコンの形検出) |
| `Fishing/` | `Fisher`(状態機械)と、そこから切り出した協調クラス: `AutoTuner`(投げ待ち・沈み回数・当たり待ちの自動調整)、`AttemptRecorder`(結果の確定記録)、`ToastReader`(トースト OCR)、`HudMonitor`(レベル・経験値)、`ControlChannel`(停止キー・control.json・リセット)、`ClickSender`、`Auditor`、`NekoCollector`、`LoopProfiler`、`EvidenceStore` |
| `Farm/` | 定点写真(`FarmShots`、`SpoutCamera`)と畑の解析(`FarmGrid`) |
| `Platform/` | `WinApi`(SendInput でカーソルを動かさないクリック、前面化、キー監視)、`WgcCapture`、`Imaging`、`Notify`、`Firewall`、`SelfUpdate` |
| `Ocr/` | `WinOcr`(Windows 標準 OCR、約 15ms)、`PpOcr`(PP-OCRv6 を ONNX Runtime で。約 0.9 秒、誤読が最少。モデルは exe に埋め込み) |
| `Web/` | Kestrel のダッシュボード。画面は `Web/Static/index.html` を exe に埋め込む |
| `tests/Pocofarm.Tests` | xUnit。検出・判定・設定・自動調整・OCR 固定サンプル(`tests/fixtures`) |

設計の方針: しきい値や間隔は `Config*.cs` の項目にして `[Desc]` で説明を付ける(検出器には `BobberParams` などの record で渡す)。`Fisher` は状態遷移だけを持ち、記録・OCR・調整・操作の受け口は協調クラスに任せる。

## 実装メモ

- キャプチャは **Windows.Graphics.Capture**。ウィンドウ単位で撮るのでモニター構成が変わっても再列挙が要らず、他のウィンドウが被っても撮れる。クリックは OSC(`/input/UseRight`)で送るので VRChat が背面でも動く(最小化だけは不可)。フレームは画面が変わったときだけ届く。
- RapidOCR 相当は PP-OCRv6 の ONNX を直接動かす実装。横書きのトースト専用(縦書き判定・180 度判定は省略)。
- 視点移動の検出は縮小画像のブロックマッチング。
- 投げの受付ロック: ゲームは引き(釣果)から約 1.8 秒は投げを受け付けない(実測)。引きクリックからの間隔(`reel_to_cast_min`、既定 2.0 秒)を上限に、`AutoTuner` が成功の続く限り縮める。竿先にウキが残った場合は `cast_failed` として記録し、すぐ投げ直す。
- 試行間隔が `slow_cycle_seconds`(既定 30 秒)を超えると `slow_cycle` イベントを記録し、ダッシュボードの注意イベントに出る。
- ねこちゃんの通知(トーストの「ねこちゃん: 本文」)は、釣果の分類とは別に拾って `neko` イベントに記録する(表示中に読めた最長の本文を 1 件にまとめ、画面証拠付き)。
- `--paused` で一時停止状態のまま起動できる(手動で遊んでいるときの再起動用)。
- ダッシュボードのバインド先を `--host` で絞れる(`127.0.0.1` ならファイアウォールの確認が出ない)。

## LAN に公開する

初回のみ `pocofarm.exe setup`(または `scripts\setup_firewall.ps1 -Exe csharp\dist\pocofarm.exe`)を実行する(UAC の承認が要る)。exe を作り直してパスが変わらなければ再設定は不要。

## 別マシンで使うとき

- `pocofarm.exe doctor` : 初回点検。VRChat を起動して(釣り竿を投げた状態だとウキ検出も確認できる)実行すると、OS の版・解像度・キャプチャ速度・レベル/経験値バー/通知領域・ウキ検出・LAN 公開設定を点検して問題を報告する。`--image <png>` で保存画像に対しても実行できる。
- `pocofarm.exe setup` : LAN からダッシュボードを開くファイアウォール規則を作る(UAC が出る。プライベートネットワークのローカルサブネットのみ許可)。起動時に規則がなければログで案内する。
- HUD 位置の自動補正: レベル表示が 3 回連続(約 30 秒)読めないと、画面下部から「Lv N」の文字位置を OCR で探し、レベル領域・経験値バー領域をその位置と大きさに合わせ直す(`data/calibration.json` に保存、画面サイズごと)。通常の領域で読めているときは何もしない。補正したときは `hud_calibrated` イベントとログに残る。
- 当たり待ちの打ち切りは、実測の最大待ち時間の 2.5 倍(20 秒〜`no_bite_timeout`)に自動調整される(`bite_wait_min_samples` 件溜まってから)。


## ファーム定点写真(Spout)
`farm_shot_interval`(既定 600 秒、0 で無効)ごとに、釣れた直後(cooldown)に釣りを約 7 秒止め、Esc → カメラ → Stream Camera を画面確認つきで操作してカメラを開き、OSC で `Streaming` を入れて Spout の共有テクスチャを自分で読み取って JPEG にし、`Streaming` を切ってカメラを閉じて再開する。写真は直近 `farm_keep_shots` 枚(既定 12)だけ `data/farm/` に残る。VRChat の OSC を有効にしておくこと。

実測した仕様: `/usercamera/*` の OSC はカメラが開いている間だけ効く。Spout にファームが映るのはカメラが開いている間だけ。配信中は VRChat の HUD(レベル・釣果)が消えるので、撮影の間以外は配信しない。想定外の画面なら何もクリックせず中止する。ダッシュボードの `POST /api/farm/shoot` で即時撮影もできる。
