# UE 覆盖包安装事务落地（2026-11-03，路线图阶段四首批）

UE 游戏从"手工换包"进入事务化安装：`UePakInstallService` 提供覆盖 pak 的安装/停用/恢复，复用现有 `InstallationTransaction` 日志格式以获得共享的崩溃恢复能力。

## 设计

- **原 pak 零写入**：安装只新增/更新游戏 Paks 目录下一个自有覆盖 pak；原 pak 安装前做 SHA-256 基线校验（不符 = 游戏更新或目录不对，拒绝），整个流程只读它。
- **日志**：`.franca/installations/<guid>/manifest.json`，字段与 `InstallationTransaction.Journal` 同形（Status/Files/Directories）+ `UePak`/`OriginalPak`/`OriginalSha256`/`PreparedSha256` 扩展；pending 状态下共享 `Recover()` 会回滚（实证）。
- **冲突拒绝**：目标覆盖文件已存在且不属于任何 Franca 代次（哈希不匹配）→ 拒绝写入；卸载时目标被外部篡改（哈希不等于清单）→ 拒绝删除，保住外来内容。
- **事务目录双备份**：`backup/` 存被替换的旧覆盖（回滚用），`prepared.pak` 存本次安装内容（修复用）。
- CLI：`--ue-install <gameDir> <prepared.pak> <pakDirRelative> <overrideFileName> <originalPakRelative> <originalSha256>`、`--ue-uninstall <gameDir>`、`--ue-repair <gameDir>`。真实游戏写入，需游戏已停止。

## 探针（`--probe-ue-install`，合成游戏目录，12 项全过）

安装提交且原 pak 不变、幂等重复安装、卸载只删自有文件、卸载后无清单态、修复恢复/完好、基线不匹配拒绝、外来覆盖文件拒绝、外部篡改拒绝卸载、**崩溃恢复**（pending 清单 + 半成品自有覆盖 → 共享 Recover 回滚删除并标记 rolled-back）。

## 边界

- 尚未接 UI（GamesPage 按钮）；当前为 CLI + 服务能力。
- 与 `GameTranslationService` 的游戏停止检查、进程交互未接线（CLI 要求调用方保证游戏停止）。
- 歧路旅人 2 真机安装/停用/恢复整循环尚待用本服务重跑验收（当前部署的包仍是手工放置）。
