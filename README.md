# Viscera Cleanup Detail 日本語化パッチ | Japanese Translation

Steam版 Viscera Cleanup Detail を日本語で遊べるようにするインストーラーです。

本編、Santa's Rampage、Shadow Warrior のメニュー、メモ、PDA、新聞、パンチアウトの書類などを翻訳しています。

## ダウンロード
[Releases](https://github.com/Skun821/VCD-Japanese-Translation/releases/latest) から `VisceraJapaneseInstaller-v1.1.4.zip` をダウンロードしてください。
Nexus Modsでも公開しています：https://www.nexusmods.com/visceracleanupdetail/mods/5

## 導入
1. ゲームを終了します。
2. zipを展開し、`VisceraJapaneseInstaller.exe` をダブルクリックします。
3. ゲームフォルダーが自動で表示されます。表示されない場合は「選択…」で Viscera のフォルダーを選んでください。
4. 「日本語を導入」を押し、Windowsの確認で「はい」を選びます。
5. 完了したら「コピー」を押し、Steamライブラリで Viscera Cleanup Detail を右クリック →「プロパティ」→「一般」→「起動オプション」に `-language=JPN` を貼り付けます。

- 元のファイルはゲームフォルダー内の `VCD_JPN_Backup\Original` に保存されます。
- 英語版に戻すときは、もう一度起動して「日本語を解除」を押し、起動オプションの `-language=JPN` を削除してください。

## 注意
- 有料DLC「House of Horror」「The Vulcan Affair」と、ポスター・看板など画像に描かれた文字は英語のままです。
- 未署名のツールのため、Windows SmartScreen が警告を出すことがあります（「詳細情報」→「実行」で起動できます）。
- 日本語の文字は Noto Sans CJK（SIL Open Font License 1.1）から作成しています。
- MOD制作の一部にAI（OpenAI Codex、Anthropic Claude）を使用しています。

## Installer source (English)

Source code of `VisceraJapaneseInstaller.exe`, the installer distributed in [Releases](https://github.com/Skun821/VCD-Japanese-Translation/releases) and on Nexus Mods
(https://www.nexusmods.com/visceracleanupdetail/mods/5).

## What the installer does
- Finds the Steam install of Viscera Cleanup Detail (AppID 246900) or lets the user pick the folder.
- Copies the untouched original game files it changes to `<game>\VCD_JPN_Backup\Original`.
- Rebuilds the Japanese versions of those files from the originals using the binary patches in `payload/patches`
  and verifies every result with SHA-256.
- Copies the 11 Japanese localization files in `payload/jpn` to `UDKGame\Localization\JPN`.
- Uninstall restores the backed-up originals and removes the added files.

It only reads and writes inside the selected game folder, has no network access, and only reads
(never writes) the registry value `HKCU\Software\Valve\Steam\SteamPath`.
Administrator rights are requested only because the game is usually under Program Files.

## Files
- `Program.cs` — the whole installer (WinForms, .NET Framework 4).
- `payload/manifest.tsv` — list of original files, targets, patches and SHA-256 hashes.
- `payload/patches/*.vcdp` — binary patches (format below). They contain no complete game files.
- `payload/jpn/*.jpn` — Japanese localization text (UTF-16).
- `tools/delta.py`, `tools/build_payload.py` — Python scripts that generated the patches from the original and translated files.
- `LICENSE_NotoSansCJK.txt` — SIL OFL 1.1 for the Noto Sans CJK glyphs used in the font packages.

## Build
Windows (no SDK needed, uses the compiler included with .NET Framework 4):
```
build.bat
```
Linux/macOS with Mono (`mono-devel`):
```
./build.sh
```
Both produce `VisceraJapaneseInstaller.exe` with the payload embedded as resources.

## Patch format (VCDP1)
```
"VCDP1\0" | u64 source size | 32-byte source SHA-256 | u64 target size | 32-byte target SHA-256 | zlib stream
zlib stream: sequence of ops
  0x01 COPY  u64 source offset, u32 length
  0x02 ADD   u32 length, <bytes>
  0x00 END
```
