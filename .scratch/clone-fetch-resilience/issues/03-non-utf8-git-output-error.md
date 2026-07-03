# 非 UTF-8 な git 出力への文脈付きエラー

Status: ready-for-agent

## Parent

`.scratch/clone-fetch-resilience/PRD.md`

## What to build

git プロセス出力の strict UTF-8 デコードが不正バイトでクラッシュしたとき、原因が分かるエラーに変え、遭遇時の調査時間を短縮する。

- `GitHelpers` は git の入出力を strict UTF-8(`new UTF8Encoding(false, true)` = 不正バイトで例外)でデコードしている。ここで `DecoderFallbackException` が発生した場合に、「git 出力に不正な UTF-8 バイトが含まれる。非 UTF-8 エンコーディング(例: Shift-JIS 時代)の履歴が原因の可能性がある」旨を伝える `GitTfsException` にラップして投げ直す。
- **デコード方針は strict のまま変えない。** 置換フォールバック(`UTF8Encoding(false, false)`)は不正バイトを黙って置換文字に化けさせ、変換後の git 履歴にサイレントな文字化けを恒久的に焼き込むため採用しない。このスライスの目的は挙動変更ではなく、クラッシュ時のメッセージ改善のみ。

## Acceptance criteria

- [ ] git 出力のデコードで不正な UTF-8 バイトに遭遇したとき、生の `DecoderFallbackException` ではなく、原因を説明する `GitTfsException` が投げられる
- [ ] デコード方針は strict UTF-8 のまま(置換フォールバックは導入しない)
- [ ] 正常な UTF-8 出力の処理は従来どおり変わらない
- [ ] デコード経路が無理なく切り出せる場合は、不正バイト列を与えて文脈付き例外になることをユニットテストで検証する(構造変更が過大なら、挙動不変ゆえレビュー担保に留めてよい)

## Blocked by

None - can start immediately
