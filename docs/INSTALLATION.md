# 安装事务与恢复

入口：`TranslationInstaller.EnableAsync`；实现：`Services/InstallationTransaction.cs`；安全验证：`Franca.exe --probe-installation`（只操作随机临时目录，不访问网络或真实游戏）。

## 写入顺序

1. 检查并恢复先前 pending / recovery-required 事务，再判断运行环境是否已就绪。
2. 每游戏安装锁防止同时安装。两份运行时 ZIP 均下载至缓存、验证可读取后，暂存解压到独立临时目录。拒绝越界、绝对路径、保留事务目录和路径上的重解析点。下载/暂存失败不写入游戏运行时。
3. 包的根 changelog.txt 归档到 BepInEx/franca-package-notices/，避免覆盖游戏同名说明。暂存配置，保留游戏已有 INI；检查必需的 XUnity.Common.dll。
4. 写入前对每个目标文件保存 SHA-256、原文件备份、新文件哈希及本轮新目录清单。证据保存在 `<game>/.franca/installations/<id>/`。
5. 发布 pending 清单，再逐文件通过同卷临时文件移动替换；写入前核对目标仍与基线一致。运行环境复检通过后标记 committed。
6. 异常、取消或校验失败，逆序恢复原文件、删除本轮新文件，只移除本轮创建且为空的目录，保留备份和 rolled-back 清单。

## 中断与冲突

进程中断后，下次启用先恢复未完成事务。若现文件已被外部修改、原备份损坏或不可写，停止恢复，清单标记 recovery-required；不会用旧备份覆盖外部改动。日志给出事务目录。此时关闭游戏，保留现文件和备份，对照 manifest.json 手动确认冲突后再继续；不要直接删除清单以绕过检查。

committed 表示运行时文件事务完成，不等于游戏内翻译可用。完成的备份不会自动清理。上游 ZIP 沿用固定 HTTPS 下载地址，可读性检查不能替代上游签名/可信摘要验证。

安装器不负责启动中继；应用服务提供显式地址。停用仍通过 `winhttp.dll` ↔ `winhttp.dll.frdisabled` 可逆重命名；配置修复仍保留独立 INI 备份。

## 卸载（整体还原）

卸载是「不识别问题、整体还原」的通用恢复手段，与停用（可逆互换、文件全保留）和修复（识别问题针对性恢复）互补。入口：游戏行折叠详情区的「卸载翻译」按钮（存在 Franca 产物时可见），确认后执行；卸载后可重新启用，翻译缓存（AppData `packs/`）保留，重装走增量、很快。

- **东方静态路线**（`TouhouStaticService.UninstallAsync`）：安装锁 + 事务恢复后，用 `<名>.dat.frorig` 覆盖回数据包（备份哈希与状态记录不符仅警告、仍恢复）；清理 `.frorig`/`.frpatched`/`.frold`/状态文件/install.lock/空的 `.franca`。逐步容错，结尾汇总「已恢复/未能恢复」清单；state 丢失但备份在、或数据包被人为损坏，都能完成恢复。
- **Unity 注入路线**（`TranslationInstaller.Uninstall`）：按 `.franca/installations/<id>/manifest.json` 逆操作——Franca 安装时新建的文件核对哈希后删除，被覆盖的原文件从备份恢复；清单外文件一律不动（用户自己的 BepInEx 模组不会被误删），与磁盘不符的条目保守跳过并报告，绝不强制覆盖外部改动。清单缺失/不可读时拒绝卸载（无法安全区分 Franca 产物与用户文件）；全部逆操作成功后才清理安装记录。
- 崩溃后无人值守恢复或断电持久性保证仍未实现。

安全验证：`--probe-touhou-static`（临时夹具：卸载字节级还原、产物清空、缓存保留、再启用零请求、state 丢失/数据包损坏容错、Unity 合成清单逆操作与冲突保守）；`--probe-touhou-live-uninstall` 对真实 th10/th11 做一次「已安装→卸载→校验→同语言再启用」往返。
