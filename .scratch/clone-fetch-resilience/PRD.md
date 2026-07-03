# PRD: clone/fetch のネットワーク耐障害性ハードニング

Status: ready-for-agent

## Problem Statement

長時間の `git tfs clone` / `git tfs fetch` は、changeset ごとに大量のファイル内容を TFS からダウンロードする。ネットワークの一瞬の瞬断、VPN 再接続、プロキシの一時不調があると、この最も回数が多く最も失敗しやすい経路でツールが即座に落ちる。ユーザーは本格運用前で、まだ実際の障害には遭遇していないが、運用で確実に起きるこの種の失敗に対して予防的に固めておきたい。

具体的に、grilling で現状コードを裏取りした結果:

- **ファイルダウンロードにリトライが無い。** `QueryHistory` やワークスペース Get は `Retry.Do` で保護されているのに、最も高頻度な changeset ファイル内容ダウンロード(`WrapperForItem.DownloadFile`)だけが素通しで、一度の瞬断で clone 全体が落ちる。
- **共有リトライの transient 判定が弱い。** `Retry.Do` が catch するのは TFS 例外・`WebException`・`GitTfsException` の3種のみ。実運用で TFS クライアントが直接投げうる `SocketException` / `IOException` / `TimeoutException` は1回目で即失敗する。さらに TFS クライアントは `WebException` を別の例外でラップして投げることがあるが、現状は `InnerException` を辿らないため、ラップされた瞬断も transient と判定されない。間隔も固定で、数十秒続く障害には backoff が効かない。

## Solution

ユーザー視点では「長時間の clone/fetch が、一時的なネットワークの揺らぎ程度では途中で落ちず、リトライして完走する」状態にする。恒久的なエラー(認証失敗・ディスクフル等)は従来どおり速やかに失敗し、リトライで無用に固まらない。

そのために2つの独立した改善を行う:

1. **ファイルダウンロードをリトライ配下に置く。** `DownloadFile` を、指数バックオフ付きリトライで包む。ダウンロードは同一 temp パスへの上書きで冪等なので、リトライしても安全。
2. **共有リトライの transient 判定を強化する。** 判定対象に `SocketException` / `IOException` / `TimeoutException` を加え、`InnerException` を再帰的に辿る。加えて、恒久的エラーで無限に固まらないよう、指数バックオフには上限(cap)を設ける。

危険な純粋ロジック(transient 分類・バックオフ間隔計算)は GitTfs core の単一 seam に集約してユニットテストし、TFS 依存の薄い配線はレビューで担保する。

## User Stories

1. As a git-tfs ユーザー, I want changeset ファイル内容のダウンロードが一時的なネットワーク障害で失敗してもリトライされること, so that 長時間の clone が一瞬の瞬断で丸ごとやり直しにならない。
2. As a git-tfs ユーザー, I want TFS クライアントが直接投げる `SocketException` に対してもリトライされること, so that TCP レベルの瞬断で即死しない。
3. As a git-tfs ユーザー, I want ネットワーク I/O 由来の `IOException` に対してもリトライされること, so that 接続断が `IOException` として表面化するケースを取りこぼさない。
4. As a git-tfs ユーザー, I want `TimeoutException` に対してもリトライされること, so that 遅い応答が一時的なものであれば完走できる。
5. As a git-tfs ユーザー, I want ラップされて `InnerException` の奥にある瞬断も transient と判定されること, so that TFS クライアントが `WebException` を別例外で包んで投げても耐えられる。
6. As a git-tfs ユーザー, I want リトライ間隔が指数的に伸びること, so that 数十秒続く障害(VPN 再接続・プロキシ不調)でも待って回復できる。
7. As a git-tfs ユーザー, I want 指数バックオフに上限(cap)があること, so that 恒久的なエラーでもリトライが分単位でハングせず、有限時間で確定失敗する。
8. As a git-tfs ユーザー, I want 認証失敗やディスクフルのような恒久的エラーが従来どおり速やかに失敗すること, so that 無駄なリトライで待たされない。
9. As a git-tfs ユーザー, I want 既存の `Retry.Do` 呼び出し元(ワークスペース Get / 削除 / QueryHistory 等)の挙動が実質変わらないこと, so that 今回の変更で別の経路が退行しない。
10. As a git-tfs メンテナ, I want transient 分類ロジックがユニットテストで固定されていること, so that 将来の変更で分類が壊れたら気づける。
11. As a git-tfs メンテナ, I want バックオフ間隔計算がユニットテストで固定されていること, so that cap や初期値の回帰を検出できる。
12. As a git-tfs メンテナ, I want リトライ枯渇時に、蓄積した例外が失われず報告されること, so that 何が原因で最終的に落ちたのか追える。

## Implementation Decisions

### 新 seam: transient 分類とバックオフ計算(GitTfs core)

- GitTfs core に純粋な静的分類ロジックを新設する(例: `Core` 名前空間の新クラス)。責務は2つ:
  - `IsTransient(Exception)`: `System.Net.WebException` / `System.Net.Sockets.SocketException` / `System.IO.IOException` / `System.TimeoutException` を transient と判定し、`InnerException` を再帰的に辿って、いずれかの階層が transient なら true を返す。
  - バックオフ間隔の計算: 初期間隔・回数・上限(cap)から、`min(initial * 2^n, cap)` の間隔列を導出する純粋関数。
- このクラスは **TFS SDK に依存しない**(core は TFS 非依存を保つ)。TFS 固有型 `TeamFoundationServerException` は分類対象に含めず、後述のとおり VsCommon 側で合成する。

### 共有リトライ `Retry`(VsCommon)の変更

- 既存の型別 `catch`(`TeamFoundationServerException` / `WebException` / `GitTfsException`)を、単一の `catch (Exception ex) when (...)` に置き換える。ガード条件は「`ex is TeamFoundationServerException`(既存挙動の温存) **または** `ex is GitTfsException`(既存挙動の温存) **または** core の `IsTransient(ex)`」。
  - これにより既存の3種はすべて従来どおり捕捉されつつ、`SocketException` / `IOException` / `TimeoutException` とラップされた瞬断が新たに捕捉される。TFS 固有型はそれが可視な VsCommon 側に1行残すため、stringly-typed な型名マッチは不要。
- **既存の固定間隔オーバーロードは温存する。** `Retry.Do(action, retryInterval, retryCount)` の間隔挙動は変えない(例: `TryToDeleteWorkspace` の 5秒×25回、`GetRequests` 等)。今回の分類強化により追加で発生しうる最悪コストは「固定間隔 × 回数」に収まり、許容範囲。
- **指数バックオフは新規オーバーロードとして追加する**(例: `Retry.DoWithBackoff(action, initialInterval, retryCount, maxInterval)`)。バックオフを使うのはネットワーク重量級の新規経路(ファイルダウンロード)のみとし、既存呼び出し元は固定間隔のまま据え置く。
- **最終試行後の無駄な待機を除去する。** 現状のループは最後の試行で失敗した後にも `Thread.Sleep` してから `AggregateException` を投げる。最終試行後は sleep しないようにする。
- リトライ枯渇時は従来どおり、蓄積した全例外を含む `AggregateException` を投げる(挙動温存)。

### ファイルダウンロード `WrapperForItem.DownloadFile`(VsCommon)の変更

- `_item.DownloadFile(temp)` の呼び出しを `Retry.DoWithBackoff` で包む。初期間隔・回数・cap の初期値は「初期 2 秒・5 回・cap 30 秒」を想定(実装時に定数化)。
- 既存の try/catch(失敗時に temp を Dispose して rethrow、Trace ログ)は維持する。リトライが全滅したときにのみ Dispose+rethrow に到達する。
- ダウンロードは同一 `TemporaryFile` パスへの上書きで冪等。リトライ間で temp を作り直す必要はない。

### スコープ外だが関連する軽微修正(#5 由来)

- `GitHelpers` が git プロセス出力を strict UTF-8(`new UTF8Encoding(false, true)`)でデコードしている箇所で `DecoderFallbackException` が発生した場合、「git 出力に不正な UTF-8 バイトが含まれる。非 UTF-8 エンコーディングの履歴が原因の可能性がある」旨を伝える `GitTfsException` にラップして投げ直す。**デコード方針(strict 維持)は変えない** — 置換フォールバックは変換後の git 履歴にサイレントな文字化けを焼き込むため採用しない。目的は挙動変更ではなく、遭遇時の調査時間短縮のみ。

## Testing Decisions

- **良いテストの定義**: 外部から観測可能な振る舞いだけを検証する。分類ロジックは「例外を入れたら transient か否かの bool が返る」、バックオフ計算は「パラメータを入れたら間隔列が返る」という外部契約でテストし、`Retry` の内部ループ実装やスリープ回数には依存しない。
- **テスト対象は core の新 seam**(`GitTfsTest` から参照可能):
  - `IsTransient`: `SocketException` / `IOException` / `TimeoutException` / `WebException` 単体 → true。プレーンな `Exception` / `ArgumentException` → false。**ネストした合成例外**(例: `new Exception("wrap", new SocketException())`)→ true(再帰の検証)。多段ネスト、`InnerException == null` 終端も検証。
  - バックオフ間隔計算: 初期・回数・cap を与え、`min(initial * 2^n, cap)` の期待列を検証。cap に到達して頭打ちになること、回数どおりの長さになることを含む。
- **テストしない(レビュー担保)もの**: `Retry` のループ本体・`DownloadFile` の委譲・`GetWorkspace` 等の TFS 配線。危険な純粋ロジックを core seam に寄せた結果、ここは薄いアダプタになるため、実際の `Thread.Sleep` や TFS サーバを要するテストは書かない。
- **`GitHelpers` の `DecoderFallbackException` ラップ**: デコード経路が切り出せるなら、不正バイト列を与えて「生の `DecoderFallbackException` ではなく文脈付き `GitTfsException` が投げられる」ことを検証する。切り出しに構造変更が要るなら、テストは追わずレビュー担保に留めてよい(挙動変更が無く、投資対効果が低いため)。
- **prior art**: 既存テストは `GitTfsTest`(xunit)配下の `Core/`(例: `GitChangeInfoTests`, `GitTfsRemoteTests`)。純粋ロジックの直接テストとして同ディレクトリに新テストクラスを追加する。

## Out of Scope

- **サーバ側 orphaned workspace の掃除**(元指摘 #3)。前提が現状コードでは成立しない: ワークスペース名は `"git-tfs-" + Guid.NewGuid()` で毎回ユニークなため「workspace already exists」衝突は構造的に起きず、`MappingConflictException` は既に `GetWorkspace` でリカバリ済み、プロセス終了時は Janitor が `TryToDeleteWorkspace`(5秒×25回)を実行する。残るのは kill 時等にサーバ側へ `git-tfs-<guid>` が溜まる「ゴミ蓄積」のみで、既存の `git tfs cleanup-workspaces` で対応可能。予防段階での自動掃除追加は過剰投資として見送る。
- **git 出力の置換フォールバックデコード**(元指摘 #4 本体)。strict UTF-8 を維持する。置換フォールバックは変換後の git 履歴にサイレントな文字化けを恒久的に焼き込むため、履歴変換ツールとしては改善と言い切れない。上記のとおり `DecoderFallbackException` の文脈付きラップのみ行う。
- **ログ基盤の追加整備**(元指摘 #5 本体)。裏取りの結果すでに実装済み: NLog のファイル出力は常時有効で `%LocalAppData%\git-tfs\` に Debug 以上を出力し(`Program.cs`)、`NLogTraceListener` により全 `Trace.WriteLine` がファイルに届き、エラー終了時にはログファイルパスを表示する(`Program.cs` の `ReportException`)。指摘が提案した2点(ファイル出力の常時有効化・パス表示)は両方とも既存機能のため対象外。
- 既存 `Retry.Do` 固定間隔呼び出し元の間隔・回数チューニング。今回は分類強化のみで、間隔設定は変えない。

## Further Notes

- 元の指摘リストは5件。裏取りの結果、確定スコープは **#1(ダウンロードリトライ)** と **#2(transient 分類強化 + バックオフ)** の2件に絞られ、#4 は「strict 維持 + 例外ラップ」に縮小、#3・#5 は対象外(前提崩れ / 実装済み)となった。
- 着手順は #2 の core seam(分類・バックオフ計算)→ `Retry` 配線 → #1 の `DownloadFile` 委譲 → #4 のラップ、を想定。#1 は #2 の新オーバーロードに依存する。
- `IOException` を transient に含める判断について: ネットワーク断が `IOException` として表面化するため含めるが、恒久的ローカルエラー(ディスクフル等)でも投げられる。これに対する保険が cap 付き指数バックオフで、`DownloadFile` は最悪でも「初期2秒 × 5回 × cap 30秒」程度で確定失敗しハングしない。
