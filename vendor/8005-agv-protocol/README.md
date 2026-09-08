# vendor/8005-agv-protocol

`8005-agv-protocol` 里被本仓库当作契约读取的文件，按字节存一份副本。

## 为什么是副本而不是引用

`manifest/release.json` 是发布身份、63 条消息面与 11 条 denylist 的权威定义，`schemas/` 是
63 条消息与公共类型的权威形状，`errors/error-codes.json` 是 54 个错误码的权威集合——三者的
权威副本都在 `8005-agv-protocol`。本仓库的 `ProtocolIdentityArchitectureTests`、
`ProtocolMessageSurfaceArchitectureTests`、`ProtocolPayloadShapeArchitectureTests` 与
`ReasonCodeRegistryArchitectureTests` 要把它们当清单来源用，而**读兄弟目录不成立**：两个仓库
是各自独立的克隆，没有 submodule 也没有包，跑测试的机器上未必有协议仓。

所以取用方式是 vendor 一份副本，**并把它按字节钉住**。副本被改一个字符，那条测试立刻红——
这正是它不构成「第二份手抄清单」的原因：手抄清单会悄悄漂移，按字节绑定的副本不会。

## 钉法：一条常量，一张表，没有第二个哈希

**本目录没有引入任何新的「批准哈希」常量。** 钉法是两级的：

1. `manifest/release.json` 的 SHA-256 **按定义**就是
   `SQCD.Agv.Contracts.WireToGateRelease.ManifestSha256`——车载端每条报文的信封都带着这个值。
   `ProtocolIdentityArchitectureTests.TheVendoredManifestIsTheProtocolManifestByteForByte`
   直接拿那个常量去核副本：常量让副本可信，副本让常量可查，两边互为凭据。
2. 其余 70 个文件（69 个 schema ＋ 1 份错误码注册表）**逐个出现在该 manifest 自己的 `files`
   表里**，`role`／`bytes`／`sha256` 齐全，`sha256` 是原始字节摘要而非规范化摘要。
   `ProtocolIdentityArchitectureTests.EveryOtherVendoredFileIsPinnedByTheManifestFileTable`
   遍历本目录，逐个与那张表比对，并断言没有一个文件游离在表外。

于是整棵副本的可信度**追溯到线上那一个值**，链条上没有任何一处是人手抄进测试的。

`manifest` 里的 `errorRegistrySha256`（`ea538d59…`）不是这里用的那个：它由协议仓自己的
规范化算法（JCS）算出，与原始字节摘要（`6692bfd2…`）不同。本仓不复现那个算法——复现它就
变成了重新实现一个算法，而不是核对一份副本。`files` 表那一栏才是按字节的。

## 当前副本

三份都取自同一个提交。

| 项 | 值 |
| --- | --- |
| 来源仓库 | `8005-agv-protocol` |
| 来源提交 | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b`（分支 `fp/v2-candidate`） |
| 取用日期 | 2026-09-09 |

| 来源路径 | 内容 |
| --- | --- |
| `manifest/release.json` | `status CONTENT_SNAPSHOT`、`releaseVersion 1.0.0`、`protocolVersion 2`、`profileId AGV_FULL_PRODUCT`、63 条消息、11 条 denylist、1758 条文件表项 |
| `schemas/`（整棵树） | 69 个文件，`$id` 段 `agv-full-product/v2` |
| `errors/error-codes.json` | `registryVersion 2`、`appendOnly true`、54 个码（v1 的 43 个一个未删） |

那个提交即协议 v2 候选，G1 于 2026-09-08 在协议仓 self-hosted runner 上实跑通过（run
[34212719223](https://github.com/trytoreachpeak0/8005-agv-protocol/actions/runs/34212719223)）。

**它是候选，不是已批准发布。** `WireToGateRelease.ApprovalStatus` 写着
`SUPERSEDING_CANDIDATE`，`Tag` 写着 `protocol-v1.0.0` 而那个 tag 在协议仓里还没打——两个字段
一起读才是如实的。理由见 `src/SQCD.Agv.Contracts/WireToGateProtocol.cs` 的注释。

## 上游改了以后怎么刷新

副本与上游之间**没有自动同步**，也不可能有：测试机上看不到另一个仓库。上游动了任何一份，
就要有人跑一遍这四步。

1. 在同时有两个仓库的机器上，把上游文件整份拷过来：

   ```bash
   cp <8005-agv-protocol>/manifest/release.json vendor/8005-agv-protocol/manifest/release.json
   cp <8005-agv-protocol>/errors/error-codes.json vendor/8005-agv-protocol/errors/error-codes.json
   cp -r <8005-agv-protocol>/schemas/. vendor/8005-agv-protocol/schemas/
   ```

2. 算 manifest 的新哈希：

   ```bash
   sha256sum vendor/8005-agv-protocol/manifest/release.json
   ```

3. 把它填进 `src/SQCD.Agv.Contracts/WireToGateProtocol.cs` 的 `ManifestSha256`，**并把那里
   其余八个常量一起改到位**——那不是抄哈希，那是换一次协议身份，
   `ProtocolIdentityArchitectureTests` 会逐字段核对。最后更新上表的来源提交与取用日期。
   其余 70 个文件不需要任何人抄哈希：它们由新 manifest 的 `files` 表自动重新钉住。

4. 跑测试。三处会随之报缺或报多，**都不是测试写错了**：

   - **消息面增删** → `ProtocolMessageSurfaceArchitectureTests`：新消息车载端还没实现（钉进
     `MessagesWithoutAnImplementation` 并写明理由），或者某条已实现的消息还留着钉。
   - **payload 形状改动** → `ProtocolPayloadShapeArchitectureTests`：车载端发出的 payload 少了
     一个新增的必填字段，或者还带着一个已被删掉的字段，或者某个枚举值已不在表内。
   - **错误码增删** → `ReasonCodeRegistryArchitectureTests`：`IsProtocolErrorCode` 那份内联
     清单与注册表不再逐个相等。

**整份拷贝，不要手工编辑副本。**副本与上游的差异没有任何机制能自动发现，唯一的保障是
「它永远是 `cp` 出来的」这条纪律。

## 行尾

仓库根的 `.gitattributes` 给这个目录挂了 `-text`，禁止行尾转换。哈希是按字节绑定的，
checkout 时把 LF 换成 CRLF 会让全部摘要漂移。

## 这份副本替换了什么

`vendor/8005-agv-protocol/protocol-v0.1.1/errors/error-codes.json`（43 个码，取自
`1531489e…`）。路径里的版本段一并去掉：版本是 `manifest/release.json` 里的字段，写进目录名
就成了第二处需要人手同步的地方。
