# 文档导航

从 [项目说明](../README.md) 进入；当前完成状态只在 [开发进度](DEVELOPMENT-PROGRESS.md) 维护。

| 要做的事 | 入口 |
|---|---|
| 本轮执行顺序与完成条件 | [验收路线图](ROADMAP.md) |
| UE 后续自动识别与恢复流程 | [UE 自动适配路线图](UE-ADAPTATION-ROADMAP.md) |
| 查看版本变更 | [CHANGELOG](CHANGELOG.md) |
| 浏览说明网页 | [项目说明](site/index.html)、[兼容范围与依赖](site/engines.html) |
| 查功能代码和安全验证方式 | [architecture.json](../architecture.json)、[AGENTS](../AGENTS.md) |
| 理解模块边界与数据流 | [系统地图](SYSTEM-MAP.md)、[生成蓝图](BLUEPRINT.md)、[时序图](BLUEPRINT-FLOWS.md) |
| 查真实兼容性 | [游戏兼容性矩阵](COMPATIBILITY.md) |
| 查用例、证据 | [测试导航](../tests/README.md)、[测试计划](../tests/test-plan.md) |
| 查安装恢复机制 | [安装事务](INSTALLATION.md) |
| 查设计与历史经验 | [实验记录](../lab/)、[交接与排障](HANDOFF.md)、[后续优化](OPTIMIZATIONS.md) |
| 查来源与许可证 | [第三方声明](THIRD-PARTY-NOTICES.md)、[依赖和参考清单](OSS-INVENTORY.md)、[许可证文件](../LICENSES/) |

目录约定：`src/` 源码与快捷方式生成脚本；`docs/` 工程说明、更新日志、第三方声明，`docs/site/` 静态说明网页；`tests/` 日期验收摘要，原始证据放忽略的 `tests/artifacts/`；`lab/` 编号设计实验；`tools/` 本机下载和备份缓存，保留且不纳入版本管理。根目录保留带 logo 的「Franca」快捷方式、README、AGENTS、功能索引、LICENSE 和隐藏的 Git 配置。快捷方式带本机绝对路径，标准 Release 构建会自动刷新；移动仓库后重新构建或按 README 手动重建。网页发布入口改为 `docs/site/`；网页内文档和许可链接仍需保留仓库相对目录。
