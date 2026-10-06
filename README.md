# PhysToSpring

VRChat の PhysBone を VRM 1.0 の SpringBone に変換する Unity エディタ拡張です。
PhysBone の揺れ方になるべく近づくように、SpringBone のパラメータを計算して設定します。

> 実験的プロジェクトです。**両者の実装の違いにより、動作は完全に一致しません。**  
> パラメーターによる揺れ（バネや減衰）の特徴は可能な限り再現していますが、**SpringBone に Immobile の中間値が存在しない** ため、全体的に動きが大きくなります。

## 動作環境

| | バージョン |
|---|---|
| Unity | 2022.3.22f1 |
| VRChat SDK - Avatars | 3.10.5 |
| [UniVRM](https://github.com/vrm-c/UniVRM/releases) (com.vrmc.vrm / com.vrmc.gltf) | 0.131.3 |

## 使い方

1. メニュー `Tools/PhysToSpring` でウィンドウを開く
2. 変換したいアバター (PhysBone を持つルート) を指定
3. 変換方法を「複製して変換」か「そのまま変換」から選ぶ
4. チェック結果にエラーがなければ「変換」を押す

変換すると次のことが行われます。

- アバターに `Vrm10Instance` と SpringBone / コライダーが追加される
- `VRM10Object` が無ければ `Assets/PhysToSpring_Generated/` に作られる
- 元の `VRCPhysBone` / `VRCPhysBoneCollider` は削除される (Undo 可能)

スクリプトからは `PhysToSpringConverter.ConvertCopy` / `ConvertInPlace` で呼べます。

## 制限事項

- **60fps 前提** です。UniVRM の SpringBone はフレームレートで挙動が変わるため、60fps で PhysBone に合うようにパラメータを決めています。
- **PhysBone Version 1.1 のみ** 対応します。1.0 のものはエラーになります。
- 長さ 1mm 未満のボーンがあると変換しません。ボーンを直してから変換してください。
- Stretch / Squish / Grab / Pose / Is Animated / Parameter は無視します。
- Limit の Hinge / Polar は近似です (未検証)。Limit Rotation のカーブは無視します。
- 無効化された PhysBone コンポーネントは変換しません。
- Immobile は 0.9 以上のときだけ SpringBone の center で近似し、それ未満は無視します。
  World タイプの Immobile はエディタ上では効果を確認できていないので、VRChat 内の見た目とは差が出る可能性があります。
- Multi Child Type: Average は First として扱います。枝分かれしたボーンは `{PhysBone名}_{番号}` の別 Spring に分けます。
- Inside Bounds のコライダーと Plane コライダーは拡張コライダー (VRMC_springBone_extended_collider) として出力します。拡張に対応していないビューアでは代替形状になります。

## パラメータ対応の求め方

PhysBone の内部実装は非公開なので、エディタの Play Mode で PhysBone と SpringBone を同じ動きで揺らして記録し、その差が小さくなるように変換式を合わせています。
VRChat SDK の逆コンパイルや解析はしていません。

このリポジトリには、その計測に使ったハーネス一式も入っています (変換だけ使う場合は不要)。

- `Assets/Bekosan/PhysToSpringHarness/` — Play Mode でジョブを実行する計測ハーネス
- `HarnessJobs/tools/` — ジョブ投入・フィッティング用の Python スクリプト
- `HarnessJobs/maps/`, `HarnessJobs/fits/` — フィッティング結果

このリポジトリを Unity プロジェクトとして開く場合は、VRChat SDK と UniVRM を別途入れてください (どちらもリポジトリには含めていません)。

## 免責

- 本ツールは非公式のものであり、VRChat Inc. および VRM コンソーシアムとは関係ありません。
- 変換したモデルの利用にあたっては、元のアバターの利用規約を守ってください。

## ライセンス

[MIT](LICENSE)
