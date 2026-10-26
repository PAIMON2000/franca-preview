# 2026-10-23 同步、安装恢复与后台验收

## 范围与环境

- 起点 `18f1652`，快进同步 GitHub `origin/main` 到 `1222963`，无已有工作区改动和合并冲突。提交范围限源码、文档与验收摘要；游戏备份、构建产物和原始证据保留本机，不纳入 Git。
- Windows / .NET 9 / Release。用户明确选择「先只做后台检查，交互测试留待空闲」，因此没有启动游戏、发送游戏键鼠输入或重启 Steam；不声称完整真机验收通过。
- 原始证据：`tests/artifacts/2026-10-23-acceptance/`（Git 忽略），含 build、各探针 stdout/stderr、overlay.png、游戏基线 SHA-256、安装日志和文件保留检查。

## 文档与目录

根目录 HANDOFF、OPTIMIZATIONS、oss-inventory 移至 docs，对应导航和实际链接已更新。新增文档/测试导航、路线图、安装说明与兼容矩阵。进度长日志归档到 docs/history，当前进度只维护简明状态。保留源码布局、法律文件、静态网站入口、lab 历史和 tools 缓存。

## 安装事务

- 两包先下载并读取验证，再临时解压；禁止路径越界和重解析点；逐文件写前备份与 SHA-256 清单，同卷替换。失败/取消/校验错误回滚；恢复冲突保留用户外部改动；下次启用恢复 pending 事务，每游戏锁防并发安装。
- 未完成清单纳入就绪与健康检查，开关流程先恢复，避免半安装被当作可启动。
- 文件保留检查发现 BepInEx 根 changelog.txt 会覆盖游戏同名文件。已从事务备份恢复 BALL x PIT 原文件，将包说明重定向至 BepInEx/franca-package-notices/changelog.txt；最终代码重新安装两靶子，故障回归增加两项。
- `--probe-installation`：34 项通过。覆盖提交、原备份、写失败/取消/校验失败回滚、中断恢复、健康提示、独占锁、外部编辑冲突保护、越界 ZIP、损坏 ZIP、暂存取消及同名说明文件保护。
- 不包含一键卸载或断电持久性保证。ZIP 可读性检查不等于可信来源签名。

## 悬浮窗

设置 ShowActivated=False；用只读取指针位置的定时器判断抓手区域，避免整窗 WS_EX_TRANSPARENT 时 MouseEnter 永远不可达。未移动指针或激活用户窗口。

`--probe-overlay <输出目录>` 14 项通过：开关/中继显隐模型、真实 HWND 的 NOACTIVATE / TRANSPARENT / TOPMOST、显示前后前台窗口句柄相同、在途变化、两次本机请求产生 served=2 / cache=1、三行实时更新、真实 WPF PNG 渲染及关闭隐藏。测试窗口在屏幕外，原始渲染图 overlay.png 已人工查看三行内容完整。

这些结果不覆盖真实游戏叠加、抓手实际拖动、鼠标键盘穿透、位置记忆或多 DPI；全部保留为待空闲交互验收项。

## 游戏磁盘验收

直接调用正式 TranslationInstaller 和 TranslationHealthService 的本机辅助程序位于 `tools/acceptance-runner/`（本机缓存，不提交）。命令形态为 `Acceptance.exe <确认的游戏绝对目录> <appid>`。辅助程序只执行安装/健康检查/重命名/配置注错修复，不启动游戏；不读取付费密钥、不调用在线翻译端点。安装包固定地址与版本沿用正式安装器，下载后先校验可读取。

| 项目 | BALL x PIT | Potion Craft |
|---|---|---|
| 路径 | `<SteamLibrary>\steamapps\common\BALLxPIT` | `<SteamLibrary>\steamapps\common\Potion Craft` |
| Steam App / build | 2062430 / 23150541 | 1210320 / 18969694 |
| Unity | IL2CPP 6000.0.62f1 | Mono 2023.1.13f1 |
| 基线 | 有旧停用 BepInEx；先保留旧运行时并移出，再安装 | 无 BepInEx / winhttp / doorstop 配置，保存基线后安装 |
| 安装包 | BepInEx be.788 + XUnity IL2CPP 5.6.2 | BepInEx 5.4.23.5 + XUnity Mono 5.6.2 |
| 最终事务 | 7f680a62333046df8d451cf64136bcf8 | 13d5aeca02984236a6abc4268496f075 |
| 安装与配置健康 | 通过 | 通过 |
| 停用/恢复 | winhttp 可逆重命名、恢复后 DLL 哈希一致 | 同左 |
| 注错/修复 | Language 改 ja 后检测 target-language；保留原 INI 与修复备份，复检健康 | 同左 |
| 原文件保留 | 564 项原文件在原位置或原环境备份中哈希匹配，0 差异 | 220 项原文件在原位置哈希匹配，0 差异 |
| 最终状态 | 翻译停用，未启动游戏 | 翻译停用，未启动游戏 |

BALL x PIT 原环境备份：`tools/game-backups/20261023-BALLxPIT-original/`，含 restore-plan.json 与 baseline.json。初次安装环境也分别保留在 `tools/game-backups/20261023-BALLxPIT-first-attempt/` 和 `20261023-PotionCraft-first-attempt/`。最终游戏事务和原文件备份保留在各游戏 `.franca/installations/<id>/`；修复 INI 备份在各游戏 `BepInEx/franca-backups/`。

基线只确认可识别注入环境是否移除，未通过 Steam 校验整个游戏原版完整性；BALL x PIT 仍有先前 WaveDumps/WaveDumperSrc 非运行时目录，未删除。不能声称已完成原版干净安装及真机中文验收。Stardew Valley 属于 MonoGame，不再作为 Unity Mono 靶子。

## 回归与下一步

构建零警告零错误。9 组探针共 254 项通过：安装 34、悬浮窗 14、架构 51、分段 48、词典 30、健康 1、已知故障 18、蓝图 14、导航 44，全部退出 0。73 个本地 Markdown 链接与功能索引入口/关联路径检查通过，git diff --check 通过。无截图参数运行导航时发现旧探针在布局前取 DataGrid 行而空引用，已修复为先布局、仅省略 PNG 输出，保持无前台窗口。

下一交互时段按 [路线图](../docs/ROADMAP.md) 用窗口化、小尺寸、静音运行两款游戏，保存实际译文画面及来源证据，再验证缓存复用、停用恢复重启、故障修复重启及游戏内悬浮窗。不将本轮磁盘检查勾成真实游戏通过。
