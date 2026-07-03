# 共有リトライの transient 判定強化

Status: ready-for-agent

## Parent

`.scratch/clone-fetch-resilience/PRD.md`

## What to build

共有リトライユーティリティ `Retry` の transient 判定を強化し、既存の全リトライ経路(QueryHistory・ワークスペース Get / 削除 等)が、より広い範囲の一時的ネットワーク障害を生き延びられるようにする。

- GitTfs core に TFS 非依存の純粋な分類ロジック `IsTransient(Exception)` を新設する。`System.Net.WebException` / `System.Net.Sockets.SocketException` / `System.IO.IOException` / `System.TimeoutException` を transient と判定し、`InnerException` を再帰的に辿って、いずれかの階層が transient なら true を返す。TFS クライアントが瞬断を別例外でラップして投げるケースを取りこぼさないことが狙い。
- VsCommon の `Retry` は、既存の型別 `catch`(`TeamFoundationServerException` / `WebException` / `GitTfsException`)を単一の `catch (Exception ex) when (...)` に置き換える。ガードは「`ex is TeamFoundationServerException` **または** `ex is GitTfsException`(いずれも既存挙動の温存)**または** core の `IsTransient(ex)`」。TFS 固有型はそれが可視な VsCommon 側に残すため、型名の文字列マッチは行わない。
- リトライループの「最終試行で失敗した後にも sleep してから `AggregateException` を投げる」無駄を除去する。最終試行後は待機しない。
- 既存の固定間隔オーバーロードの間隔・回数挙動は変えない(指数バックオフはこのスライスの対象外)。リトライ枯渇時に蓄積した全例外を含む `AggregateException` を投げる挙動は温存する。

## Acceptance criteria

- [ ] core に `IsTransient(Exception)` 相当の純粋関数が存在し、TFS SDK に依存しない
- [ ] `SocketException` / `IOException` / `TimeoutException` / `WebException` 単体で `IsTransient` が true を返す
- [ ] プレーンな `Exception` や `ArgumentException` では false を返す
- [ ] ネストした合成例外(例: `new Exception("wrap", new SocketException())`)や多段ネストで、内側が transient なら true を返す(`InnerException == null` 終端で無限ループしない)
- [ ] `Retry` が `SocketException` / `IOException` / `TimeoutException` およびラップされた瞬断でリトライするようになった
- [ ] `TeamFoundationServerException` / `GitTfsException` は従来どおりリトライされる(既存挙動の温存)
- [ ] 恒久的エラー(上記に該当しない例外)は従来どおり即座に伝播し、無駄なリトライで待たされない
- [ ] 最終試行後に sleep しないことがコード上明らか
- [ ] `IsTransient` と再帰の振る舞いを外部契約としてユニットテストで固定した(`GitTfsTest` に追加)

## Blocked by

None - can start immediately
