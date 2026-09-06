# AsbMcpServer

ARK Smart Breedingのデータと計算処理を、MCP対応のAIクライアントから利用するためのstdioサーバーです。

## できること

- 日本語名・英語名・別名から生物を検索
- レベルと武器ダメージ率から、必要な麻酔矢・麻酔弾数を計算
- 餌の必要数とテイム時間を計算
- ナルコベリー、アスセリック・マッシュルーム、麻酔薬、バイオトキシンの必要数を計算
- 気絶維持用アイテムを一括投入できる場合は、投入基準となる昏睡値を計算
- テイム速度倍率、サングイン・エリクサー、武器ダメージ率の既定値を保存

## MCPクライアントに登録する

先に後述の手順でARK Smart Breedingの場所を設定し、`AsbMcpServer.exe` の絶対パスをMCPクライアントのstdioサーバーとして登録します。

JSON形式のMCP設定を使うクライアントでは、例えば次のように設定します。実際の設定ファイルと項目名は、使用するクライアントの説明に従ってください。

```json
{
  "mcpServers": {
    "asb": {
      "command": "C:\\path\\to\\AsbMcpServer.exe"
    }
  }
}
```

### 質問例

- `レベル150のティラノサウルスのテイムに必要なものは？`
- `298%のクロスボウならレベル145のアルゲンに麻酔矢は何本必要？`
- `羊肉でテイムする場合の餌の数と時間を教えて`
- `麻酔薬を使うなら、昏睡値がいくつ以下になったら全部投入できる？`
- `今回だけテイム速度5倍で計算して`
- `今後の既定のテイム速度を5倍にして`

## 公開ツール

| ツール | 用途 |
| --- | --- |
| `get_taming_info` | 生物、レベル、餌、倍率から昏睡・餌・時間・気絶維持情報を計算します。 |
| `resolve_species_name` | 生物名を検索し、一意に決まらない場合は候補を返します。 |
| `get_saved_settings` | 保存されている既定の倍率と武器ダメージ率を確認します。 |
| `update_saved_settings` | 明示した項目だけを今後の既定値として保存します。 |

`get_taming_info` の省略可能な設定は、その呼び出しだけに使う一時上書きです。保存された既定値は変更しません。今後の呼び出しにも使う既定値を変更するときだけ `update_saved_settings` を使います。

気絶維持用アイテムの必要数は、それぞれのアイテムだけを使う場合の代替案です。表示された4種類の個数を合計して用意する必要はありません。一括投入で最大昏睡値に達して薬効を無駄にする可能性がある場合は、一括投入不可とし、分割方法や投入基準は返しません。

## データの出典

`Data/species-names.generated.json` の日本語名と英語名の対応は、[ARK: Survival Ascended Wikiの「ASA全生物一覧表」](https://wikiwiki.jp/arksa/ASA%E5%85%A8%E7%94%9F%E7%89%A9%E4%B8%80%E8%A6%A7%E8%A1%A8)を参照しています。取得した名称は、ARK Smart Breedingの基本データおよびASA追加データに存在し、テイム計算に利用できる生物に限定して収録しています。

このプロジェクトはARK: Survival Ascended、ARK: Survival Evolved、ARK Smart Breedingおよび上記Wikiの公式プロジェクトではありません。

## ASBの場所を設定する

ARK Smart Breedingを別途インストールしてください。

実行時は、exeと同じフォルダに `asb-settings.json` を置きます。`asb-settings.example.json` をコピーし、ARK Smart Breedingのインストールフォルダを指定してください。

```json
{
  "asbInstallPath": "C:\\path\\to\\ARKSmartBreeding"
}
```

`ASB_INSTALL_PATH` 環境変数が設定されている場合は、設定ファイルより優先されます。

## ビルドする

ビルドには.NET 10 SDKとARK Smart Breedingが必要です。ASBのDLLを参照するため、環境変数を設定してからビルドします。

```powershell
$env:ASB_INSTALL_PATH = 'C:\path\to\ARKSmartBreeding'
dotnet build
```

または、MSBuildプロパティで直接指定できます。

```powershell
dotnet build -p:AsbInstallPath='C:\path\to\ARKSmartBreeding'
```

## 保存設定

既定のテイム速度倍率、サングイン・エリクサー設定、クロスボウとライフルの武器ダメージ率は次の場所に保存されます。

```text
%LOCALAPPDATA%\AsbMcpServer\settings.json
```

`ASB_MCP_SETTINGS_PATH` 環境変数で保存先を変更できます。通常はファイルを直接編集せず、MCPクライアントから `get_saved_settings` と `update_saved_settings` を使って確認・更新してください。
