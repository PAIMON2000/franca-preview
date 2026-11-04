# UE 覆盖包安装事务落地（2026-11-03，路线图阶段四首批）

UE 游戏从"手工换包"进入事务化安装：`UePakInstallService` 提供覆盖 pak 的安装/停用/恢复，复用现有 `InstallationTransaction` 日志格式以获得共享的崩溃恢复能力。

## 设计

- **原 pak 零写入**：安装只新增/更新游戏 Paks 目录下一个自有覆盖 pak；原 pak 安装前做 SHA-256 基线校验（不符 = 游戏更新或目录不对，拒绝），整个流程只读它。
- **日志**：`.franca/installations/<guid>/manifest.json`，字段与 `InstallationTransaction.Journal` 同形（Status/Files/Directories）+ `UePak`/`OriginalPak`/`OriginalSha256`/`PreparedSha256` 扩展；pending 状态下共享 `Recover()` 会回滚（实证）。
- **冲突拒绝**：目标覆盖文件已存在且不属于任何 Franca 代次（哈希不匹配）→ 拒绝写入；卸载时目标被外部篡改（哈希不等于清单）→ 拒绝删除，保住外来内容。
- **事务目录双备份**：`backup/` 存被替换的旧覆盖（回滚用），`prepared.pak` 存本次安装内容（修复用）。
- CLI：`--ue-install <gameDir> <prepared.pak> <pakDirRelative> <overrideFileName> <originalPakRelative> <originalSha256>`、`--ue-uninstall <gameDir>`、`--ue-repair <gameDir>`。真实游戏写入，需游戏已停止。

## 探针（`--probe-ue-install`，合成游戏目录，18 项全过）

安装提交且原 pak 不变、幂等重复安装、卸载只删自有文件、卸载后无清单态、修复恢复/完好、基线不匹配拒绝、外来覆盖文件拒绝、外部篡改拒绝卸载、**崩溃恢复**（pending 清单 + 半成品自有覆盖 → 共享 Recover 回滚删除并标记 rolled-back）。

清单推导（`ResolvePrepared`，5 项）：合法清单→安装参数全部正确（相对目录统一正斜杠）、IsInstalled 助手、appId 不符拒绝、原 pak 被换（长度变）拒绝、产物 pak 被改拒绝、SourcePak 指向游戏外拒绝。

## UI 接线（GamesPage，一等选项）

- Unreal 游戏在游戏库与 Unity 行同位：主按钮 = 未安装「安装 UE 译文…」（文件对话框选择准备产物目录的 `manifest.json`）→ 已安装「启动」（经 Steam）；状态列显示已就绪/未启用。`IsSupported` 含 UE，注入式启用开关（`HasToggle`）仍只给 Unity/Touhou，UE 详情区用按钮组（安装/修复/卸载）。
- 安装参数全部由 `UePakInstallService.ResolvePrepared(gameDir, manifestPath, appId)` 推导：清单状态、appId、SourcePak 游戏内 containment、原 pak 长度与索引 SHA1（=准备时基线，防游戏更新后张冠李戴）、产物 pak 哈希逐项校验，基线 SHA-256 现场计算。
- 工作流在 `GameTranslationService.UeInstallAsync/UeUninstallAsync/UeRepairAsync`：复用 `IsGameStopped` 进程检查（运行中拒绝并提示），`_operations` 信号量串行化，写操作 `Task.Run` 离开 UI 线程；页面沿用 `_busy` + `IsToggling` + `SetInstallError` 行内错误模式。
- UE 安装态读已提交事务日志（`UePakInstallService.IsInstalled`），随 `RefreshRow` 重建行时重新求值。
- 引擎页 Unreal 升为「已支持（实验徽标）」，`engines.note.unreal` 文案更新为游戏库安装译文包启用；导航探针增 `engine_unreal_supported_experimental` 锁定该状态（72 项全过）。

## 边界

- 真机整循环尚待用本服务重跑验收（当前部署的包对服务而言是外来文件，会正确拒绝——需先手工删除再用 UI/CLI 安装；这是特性不是缺陷）。
- 准备（`--ue-prepare`）仍为 CLI 实验命令，UI 只消费其产物。
