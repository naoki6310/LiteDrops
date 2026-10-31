<p align="center"><img src="assets/icon.png" width="96" alt="LiteDrops icon"></p>

<h1 align="center">LiteDrops</h1>

<p align="center">Lightweight Twitch Drops miner for Windows — mines only the games you pick.<br>
選んだゲームだけを掘る、軽量な Twitch Drops マイナー（Windows 用）</p>

---

## 日本語

### 特徴
- **軽い**：メモリはウィンドウ表示中で約 10〜30 MB、トレイ格納中は約 3 MB まで解放します
- **選んだゲームだけ**：チェックを入れたゲームを上から順に掘ります
- **ドラッグで並べ替え**：左のつまみ（⋮⋮）をドラッグして優先順位を変更、× で削除（削除は「元に戻す」で取り消せます）
- **ゲーム検索**：ゲーム名を入力すると Twitch 上の正式名が候補に出るので、選ぶだけで追加できます
- **日本語 / 英語 UI**：Windows の表示言語に合わせて自動で切り替わります（⚙ 設定から変更も可）
- **Windows 起動時に開始**：⚙ 設定でオンにすると、PC の起動時にトレイに入った状態で採掘を始めます
- **複数アカウントを同時に**：`LiteDrops.exe` を別々のフォルダに置けば、コピーごとに別の Twitch アカウントで同時に動かせます。ウィンドウのタイトルとトレイアイコンのホバー表示に、ログイン中のアカウント名が出ます
- **アイテム優先**：ゲーム内アイテムがもらえるキャンペーンを、チャットバッジ・エモートだけのものより先に掘ります（⚙ 設定でバッジ・エモートを除外することも可）
- **アカウント連携チェック**：ゲームアカウントが未連携のキャンペーンは飛ばし、「アカウント連携 ›」から連携ページを開けます
- **自動で受け取り**：完了したドロップは自動で受け取ります（Claim）。受け取ったものは「履歴」で一覧できます
- **上位ゲームへすぐ戻る**：下位のゲームを視聴中に上位のゲームの配信が始まると、約 10 分以内に切り替えます
- **アップデート通知**：新しいバージョンが出ると画面下に表示します（1 日 2 回 GitHub に確認）
- **映像はダウンロードしない**：配信のプレイリストだけを取得して視聴扱いにするので、通信量はごくわずかです
- **ログイン情報は暗号化して保存**：Windows の DPAPI で暗号化するため、他の PC や他のユーザーでは使えません

### 動作環境
- Windows 10 / 11（64bit）

### ダウンロード
[Releases](../../releases) から、どちらか一方をダウンロードしてください。

| ファイル | サイズ | 説明 |
|---|---|---|
| `LiteDrops-standalone-win-x64.zip` | 約 42 MB | **迷ったらこちら**。展開して出てきた `LiteDrops.exe` をそのまま起動できます |
| `LiteDrops.exe` | 約 0.5 MB | [.NET 10 デスクトップ ランタイム](https://dotnet.microsoft.com/download/dotnet/10.0) が入っている PC 向け。入っていない場合は起動時に案内が出ます |

どちらも中身と動作は同じで、メモリ使用量もほぼ変わりません。

### 使い方
1. ダウンロードした `LiteDrops.exe` を好きなフォルダに置いて起動します
2. 「Twitch でログイン」を押します。ブラウザで Twitch の認証ページが開くので、表示されたコードを入力して許可します
3. 検索欄にゲーム名を入力し、候補から選んで追加します
4. チェックの入ったゲームのキャンペーンを自動で探して、視聴を始めます

- 上にあるゲームほど優先されます。行をドラッグして並べ替え、× で削除できます
- キーボード操作：Alt+↑/↓ で移動、Delete で削除、Space でオン/オフ
- 視聴中のチャンネル名をクリックすると、その配信をブラウザで開きます
- 右上の ⚙ から設定を開けます（Windows 起動時に開始・閉じたらトレイに格納・バッジ・エモートも掘る・言語）
- ウィンドウを閉じるとトレイに格納されます。終了するときは、トレイアイコンを右クリックして「終了」を選びます
- 設定（`litedrops.json`）・受け取り履歴（`litedrops-history.json`）・ログ（`litedrops.log`）は exe と同じフォルダに作られます（コピーごとに別々です）
- 複数のアカウントで掘るには、フォルダを分けて `LiteDrops.exe` をコピーし、それぞれ別のアカウントでログインします。同じフォルダの exe は 1 つしか起動できません。「Windows 起動時に開始」もコピーごとに設定できます

### 注意
- キャンペーンは、Drops が有効な配信中のチャンネルを探して見つけます。配信者がいないキャンペーンは見つかりません
- 報酬を受け取るには、事前にゲームのアカウントを Twitch と連携しておいてください
- 本ソフトは Twitch 非公式です。自動視聴は Twitch の利用規約に抵触する可能性があります。**自己責任で**使ってください
- 同じ Twitch アカウントを複数のコピーでログインしないでください（同じドロップの進行を取り合います）

---

## English

### Features
- **Light**: about 10–30 MB of RAM with the window open, about 3 MB while in the tray
- **Only your games**: checked games are mined top to bottom
- **Drag & drop priority**: drag the ⋮⋮ handle to reorder, × to remove (with undo)
- **Game search**: type a name and pick Twitch's exact spelling from the suggestions
- **English / Japanese UI**, following the Windows display language (changeable in ⚙ Settings)
- **Start with Windows** option in ⚙ Settings (starts minimized to the tray)
- **Several accounts at once**: put `LiteDrops.exe` in separate folders and run each copy with its own Twitch account. The window title and the tray icon tooltip show the logged-in account
- **Items first**: campaigns with in-game rewards are mined before chat badge / emote only ones (which can be turned off in ⚙ Settings)
- **Account link check**: campaigns whose game account isn't linked are skipped, with a "Link account ›" shortcut
- **Auto-claim** of finished drops, with a claim **History**
- **Back to your top game quickly**: switches within ~10 minutes when a higher-priority game goes live
- **Update notice** at the bottom when a new version is out (checks GitHub twice a day)
- **No video download**: only stream playlists are fetched, so bandwidth use is minimal
- **Encrypted login**: the token is protected with Windows DPAPI, so it can't be used on another PC or by another Windows user

### Requirements
- Windows 10 / 11 (x64)

### Download
Download one of these from [Releases](../../releases).

| File | Size | Description |
|---|---|---|
| `LiteDrops-standalone-win-x64.zip` | ~42 MB | **If you're unsure, pick this one.** Unzip it and run the extracted `LiteDrops.exe` as is |
| `LiteDrops.exe` | ~0.5 MB | For PCs that have the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). If it's missing, you're prompted to install it at launch |

Both contain the same app and behave the same, and their memory use is about the same.

### Usage
1. Put `LiteDrops.exe` in any folder and run it
2. Click **Log in with Twitch**, log in in the browser that opens, then paste the address it ends up on (it contains `#access_token=`) into LiteDrops
3. Add games with the search box (pick a suggestion), then leave it running

Drag rows to change the priority (top = first). Keyboard: Alt+↑/↓ to move, Delete to remove, Space to toggle. Click the channel name to open the stream. The ⚙ button opens the settings (start with Windows, close to tray, badges & emotes, language). Closing the window keeps LiteDrops in the tray; use **Exit** from the tray menu to quit.

### Notes
- Campaigns are discovered through your Twitch drops page and through live, drops-enabled channels of your games
- Link your game accounts on Twitch beforehand, or the rewards can't be claimed
- Not affiliated with Twitch. Automated viewing may be against Twitch's Terms of Service — use at your own risk

---

## Build

[![Build](https://github.com/naoki6310/LiteDrops/actions/workflows/build.yml/badge.svg)](https://github.com/naoki6310/LiteDrops/actions/workflows/build.yml)

Requires the .NET 10 SDK.

```
dotnet build -c Release
dotnet test -c Release
dotnet publish LiteDrops.csproj -c Release -o publish
dotnet publish LiteDrops.csproj -c Release --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -o standalone
```

## License

[MIT](LICENSE) © 2026 naoki6310
