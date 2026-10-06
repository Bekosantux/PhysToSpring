# PhysToSpring

VRChat の PhysBone を VRM 1.0 の SpringBone に変換する Unity エディタ拡張です。
PhysBone の揺れ方になるべく近づくように、SpringBone のパラメータを計算して設定します。

> **実験的プロジェクトです。** 変換結果は PhysBone の動きと完全には一致しません。出力は必ず目視で確認してください。

## 動作環境

| | バージョン |
|---|---|
| Unity | 2022.3.22f1 |
| VRChat SDK - Avatars | 3.10.5 |
| UniVRM (com.vrmc.vrm / com.vrmc.gltf) | 0.131.3 |

ほかのバージョンでは確認していません。

## 導入

1. VRChat Creator Companion で VRChat SDK - Avatars を入れたプロジェクトを用意します。
2. [UniVRM](https://github.com/vrm-c/UniVRM/releases) 0.131.3 を導入します。
3. [Releases](https://github.com/Bekosantux/PhysToSpring/releases) から `PhysToSpring-*.unitypackage` をインポートします。

## 使い方

1. メニュー `Tools/PhysToSpring` でウィンドウを開きます。
2. 変換したいアバター (PhysBone を持つルート) を指定します。
3. 変換方法を「複製して変換」か「そのまま変換」から選びます。
4. チェック結果にエラーがなければ「変換」を押します。

変換すると次のことが行われます。

- アバターに `Vrm10Instance` と SpringBone / コライダーが追加されます。
- `VRM10Object` が無ければ `Assets/PhysToSpring_Generated/` に作られます。
- 元の `VRCPhysBone` / `VRCPhysBoneCollider` は削除されます (Undo できます)。

スクリプトからは `PhysToSpringConverter.ConvertCopy` / `ConvertInPlace` で呼べます。

## 制限事項

- **60fps 前提** です。UniVRM の SpringBone はフレームレートで挙動が変わるため、60fps で PhysBone に合うようにパラメータを決めています。
- **PhysBone Version 1.1 のみ** 対応します。1.0 のものはエラーになります。
- 長さ 1mm 未満のボーンがあると変換しません。ボーンを直してから変換してください。
- Stretch / Squish / Grab / Pose / Is Animated / Parameter は無視します。
- Limit の Hinge / Polar は近似です (軸の対応は未検証)。Limit Rotation のカーブは無視します。
- 無効化された PhysBone コンポーネントは変換しません。
- Immobile は 0.9 以上のときだけ SpringBone の center で近似し、それ未満は無視します。
  World タイプの Immobile はエディタ上では効果を確認できていないので、VRChat 内の見た目とは差が出る可能性があります。
- Multi Child Type: Average は First として扱います。枝分かれしたボーンは `{PhysBone名}_{番号}` の別 Spring に分けます。
- 実アバターで確かめたところ、耳・尻尾・ツインテールは PhysBone より 1.2〜1.7 倍ほど大きく振れ、リボン類は 0.3〜0.5 倍ほどしか振れませんでした。
- Inside Bounds のコライダーと Plane コライダーは拡張コライダー (VRMC_springBone_extended_collider) として出力します。拡張に対応していないビューアでは代替形状になります。

## パラメータ対応の求め方

PhysBone の内部実装は非公開なので、エディタの Play Mode で PhysBone と SpringBone を同じ動きで揺らして記録し、その差が小さくなるように変換式を合わせています。
VRChat SDK の逆コンパイルや解析はしていません。

このリポジトリには、その計測に使ったハーネス一式も入っています (変換だけ使う場合は不要です)。

- `Assets/Bekosan/PhysToSpringHarness/` — Play Mode でジョブを実行する計測ハーネス
- `HarnessJobs/tools/` — ジョブ投入・フィッティング用の Python スクリプト
- `HarnessJobs/maps/`, `HarnessJobs/fits/` — フィッティング結果

このリポジトリを Unity プロジェクトとして開く場合は、VRChat SDK と UniVRM を別途入れてください (どちらもリポジトリには含めていません)。

## 免責

- 本ツールは非公式のものであり、VRChat Inc. および VRM コンソーシアムとは関係ありません。
- 変換したモデルの利用にあたっては、元のアバターの利用規約を守ってください (VRM 化や VRChat 外での利用を禁止しているアバターもあります)。

## ライセンス

[MIT](LICENSE)
