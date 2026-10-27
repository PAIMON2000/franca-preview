# 歧路旅人 2 全表正常批译完成（2026-10-27）

本轮把 10-26 中断的歧路旅人 2 英文全表批译跑完并产出正常（无标记）日语译文包。范围仅限游戏目录外准备与证据核对；未写游戏目录、未启动游戏、无真机画面验收。

## 前提变化：10-26 缓存已丢失

续译前核查发现 10-26 落盘的 4,903 条片段缓存与 end-of-day 快照均已不在磁盘上：缓存当时在自定义输出 `bin/ue-final/packs/1971650/ja`（随临时输出清理），快照在 `tests/artifacts/ue-octopath2/`（已消失）；D 盘按目录名与 checkpoint 文件名搜索均无命中。两处均属本机不入库内容，无法从仓库找回。故本轮为全量重译而非续译。游戏本体与原始 pak 完好：安装目录 `D:\steam\steamapps\common\Octopath_Traveler2`，基线前 pak 4,119,648,130 字节，目录内无残留覆盖包，为干净基线。

## 执行

- 源码基线：`edbbc79`（franca-preview 主检出）。标准 Release 输出被正在运行的 Franca.exe 占用，按 10-26 惯例用自定义输出 `src/Franca/bin/ue-final` 构建，0 警告 0 错误。
- 只读盘点 `--ue-table-inspect`：`GameTextEN.uasset` ↔ `GameTextZH_CN.uasset`，19,062/19,062 行，身份零失配，与 10-26 记录一致。
- 用户已授权英文文本发 Google/Bing。批译命令为 `--ue-table-prepare`（仅启用 Google/Bing，其余端点显式停用），appId 1971650，目标语言 ja、写入简中槽位（zh-Hans），packs 根指向稳定的 `D:\franca-preview\packs`（不再随自定义输出丢失），准备输出在游戏目录外的 `tools/ue-prepared-octopath2/menu-ja`。
- 首轮约 31 分钟译完 12,607/12,618 片段，报告 Translated=11,107、Missing=1、Incomplete=true（退出码 2），按熔断/缺译口径拒绝发布完整包；逐片段缓存已全部落盘。
- 同命令续跑：11,107 条缓存秒过，补译剩余 1 条，最终报告 **Translated=11,108、Missing=0、Incomplete=false**（退出码 0）。11,108 + 42 条纯字面（无需翻译）= 11,150 去重文本全覆盖。

## 产物与证据

- 准备代 `prepared-ea6fe9d1322648dbbff57f9c51373ebb`：Status=PreparedNotInstalled，源索引 SHA1 `4FD9DA5F6DD62E854BFDEDC6717DEA1523922B93` 与 10-26 一致；准备包 `pakchunk99-WindowsNoEditor_P.pak` 3,370,690 字节，SHA256 `1EDC9C2F88F7DB189D536F7AFE0B0E529EA4C19A1E6C42590E50B903F49A0B67`；manifest 与包均在 `tools/ue-prepared-octopath2/menu-ja/`。
- 首轮 Incomplete 的 `prepared-20e66f1e9f2c441a9413e3fe7faddd06` 保留未删，同样标记 PreparedNotInstalled 且 manifest 注明不完整发布永不可安装；避免误装请以 ea6fe9d1 为准。
- 片段缓存快照 `tests/artifacts/ue-octopath2/cache-snapshot-1027/`（256 个分片文件，共约 7.4 MB）+ manifest 副本，防止再次随构建输出丢失。
- 游戏目录只读未写；原 pak SHA256 复核为 `B7BC9A835A8F74BCD12034AF92C000A231DF04DCFD414AB84FF9AA5DE7F2B726`，与 10-26 基线逐字节一致。

## 待验（不在本轮）

真机门槛按 UE 路线图仍需用户在场：停止游戏 → 备份并新增覆盖包 → 窗口化静音验证加载、假名字体、菜单/剧情日语显示与官方译文对照 → 停用（移出覆盖包）恢复官方简中 → 再恢复译文，重复操作幂等。此前不接正式启用按钮，不把本记录写成游戏内验收通过。
