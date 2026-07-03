# ファイルダウンロードの指数バックオフリトライ

Status: ready-for-agent

## Parent

`.scratch/clone-fetch-resilience/PRD.md`

## What to build

clone/fetch で最も高頻度な changeset ファイル内容ダウンロードをリトライ配下に置き、一時的なネットワーク障害で clone 全体が丸ごとやり直しにならないようにする。

- GitTfs core に、バックオフ間隔を計算する純粋関数を新設する。初期間隔・回数・上限(cap)から `min(initial * 2^n, cap)` の間隔列を導出する。恒久的エラーでリトライが分単位でハングせず有限時間で確定失敗するよう、上限で頭打ちにする。
- VsCommon の `Retry` に指数バックオフ付きの新規オーバーロード(例: `DoWithBackoff(action, initialInterval, retryCount, maxInterval)`)を追加する。catch ガードは issue 01 の `IsTransient` を再利用し、transient 判定は 01 と一致させる。既存の固定間隔オーバーロードは温存し、バックオフはこの新経路のみが使う。
- `WrapperForItem.DownloadFile` の `_item.DownloadFile(temp)` 呼び出しを、この新オーバーロードで包む(初期 2 秒・5 回・cap 30 秒を初期値とし定数化)。ダウンロードは同一 temp パスへの上書きで冪等なため、リトライ間で `TemporaryFile` を作り直さない。既存の「失敗時に temp を Dispose して rethrow、Trace ログ」は維持し、リトライ全滅時にのみそこへ到達する。

## Acceptance criteria

- [ ] core にバックオフ間隔計算の純粋関数が存在し、`min(initial * 2^n, cap)` に従う
- [ ] cap に到達したら間隔が頭打ちになり、間隔列の長さが指定回数どおりになる(ユニットテストで固定)
- [ ] `Retry` に指数バックオフ付きオーバーロードが追加され、transient 判定は issue 01 の `IsTransient` と一致する
- [ ] 既存の固定間隔オーバーロードの挙動は変わらない
- [ ] `DownloadFile` が一時的ネットワーク障害でリトライし、成功すれば完走する
- [ ] リトライが全滅した場合のみ temp を Dispose して例外を投げ、リソースリークしない
- [ ] 恒久的エラーでも最悪「初期2秒 × 5回 × cap30秒」程度で確定失敗しハングしない

## Blocked by

- `.scratch/clone-fetch-resilience/issues/01-transient-classification.md`(新オーバーロードの catch ガードが `IsTransient` を使うため)
