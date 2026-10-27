# Franca

Windows 游戏翻译桌面原型，使用 .NET 9 / WPF。免费、开源、非商业项目。当前提供 Steam 游戏库扫描、部分 Unity 游戏（BepInEx / XUnity.AutoTranslator）与东方 Project 自研引擎（thcrap 运行时注入）的翻译安装与配置、本机翻译中继和译文缓存；Unreal 静态适配仍在实验阶段。

Windows game-translation desktop prototype (.NET 9 / WPF), free and open source. It scans installed Steam games and provides translation setup for selected Unity games and Touhou Project games, plus a local translation relay and cache. Unreal support is experimental. Compatibility is verified per game and version; OCR, community package distribution and Steam Workshop integration are not implemented.

源码仓库：<https://github.com/hongweihu184/franca-preview>（问题与建议请开 Issue）。

## 下载即用（Windows 64 位）

**[点击下载 Franca0.1-1027.exe](https://github.com/hongweihu184/franca-preview/releases/download/v0.1.20261027/Franca0.1-1027.exe)** · [最新版本与更新说明](https://github.com/hongweihu184/franca-preview/releases/latest)

不需要会用 GitHub，也不需要安装 .NET 或编译源码：

1. 点击上面的下载链接，把 EXE 放进一个单独的、可写的文件夹（例如 `D:\Franca`）。
2. 双击 EXE 启动。首次运行会在同一文件夹释放 `ThirdParty`、许可证等必要文件，请保留这些文件。
3. 每次启动后会在后台检查本仓库的正式发布版本；有新版时，点击窗口中的「下载新版」打开发布页。离线或检查失败不影响使用。

更新时先关闭 Franca 和使用翻译运行时的游戏，把新版 EXE 放回原文件夹；保留 `franca-settings.json`、`thcrap` 等已有文件，更新后从新版 EXE 启动。不要删除或移动旧文件夹后再迁移：已启用的东方游戏可能仍引用其中的运行时路径。程序不会自动下载或覆盖更新。

此版本为未签名的桌面原型，仅提供 Windows x64 包；其他架构未经验证。GitHub 下载失败可稍后重试。[发布页](https://github.com/hongweihu184/franca-preview/releases/tag/v0.1.20261027)同时提供 SHA-256 校验文件。兼容范围仍以下方逐游戏证据为准。

## 实际状态

- Suzerain 既有环境有主菜单、选项页及序章正文中文画面证据；不代表干净安装、完整剧情或更新兼容性验收通过。
- BALL x PIT（IL2CPP）与 Potion Craft（Unity Mono）通过干净基线安装、英语 → 印尼语菜单、重启缓存复用、停用/恢复与配置注错修复；未进入存档游玩。BALL x PIT 8 次启动中 2 次退出码 1，根因未定。
- 东方 Project th10/th11 走 thcrap 运行时路线：交换 exe 存根并生成 jdiff 补丁，不改游戏数据包。th11 局内对话、结局、音乐室评论、主菜单与全部下级界面精灵图，以及 th10 对话与界面经用户真机确认；符卡名因发行版加密跳过，其余正作未逐款验收。
- Unreal：《逸剑风云决》核心数据表与主菜单图片、《歧路旅人 2》少量菜单日语显示经用户确认；未接入正式启用入口，第二款完整译文包未完成。
- 自研 Loader 有独立构建和 CoreCLR 隔离验证，真实 Unity 初始化时序未验收，未纳入默认安装路径。
- 引擎特征识别不等于翻译支持。尚无 OCR、社区上传订阅服务或 Steam／创意工坊集成，也未设发布日期。

具体版本与证据见 [兼容性矩阵](docs/COMPATIBILITY.md)；当前状态见 [开发进度](docs/DEVELOPMENT-PROGRESS.md)。

## 运行与使用条件

普通用户请使用上方自带运行环境的 EXE。以下步骤供源码开发者使用，需要 .NET 9 SDK；标准构建输出运行时需要 .NET 9 Desktop Runtime。

```powershell
dotnet build src/Franca/Franca.csproj -c Release
```

程序位于 `src/Franca/bin/Release/net9.0-windows/Franca.exe`。标准 Release 构建会在仓库根目录生成带 logo 的 **Franca** 快捷方式（快捷方式含本机路径，因此不进版本库）；移动目录后重新构建或手动运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File src/Launcher/Setup-Shortcut.ps1
```

安装及修复会改动选定游戏目录，应先保留备份。游戏侧支持停用、配置修复和卸载（卸载整体还原到未安装基线）；桌面程序提供免安装 EXE，暂不提供安装器和一键卸载。删除程序文件夹前请先停用/卸载依赖该目录的游戏翻译。测试用例的副作用按 [功能索引](architecture.json) 说明，真实游戏测试保持窗口化。

维护者可用 `pwsh -File src/Launcher/Publish-Release.ps1` 重建自带 .NET 的单 EXE，输出位于 `dist/Franca0.1-1027/`。后续发布需同步修改项目中的 `Version`（对应 GitHub 标签 `v<Version>`）、`InformationalVersion`（下载文件名）及 `FileVersion`，再上传 EXE 和校验文件到 GitHub Releases；只有带 Franca EXE 的正式 Release 才会触发新版提示。

东方路线首次启用时需要联网从上游镜像下载补丁数据（约 1 MB，逐个校验哈希）；下载失败会明确报错中止，不影响其他引擎。

网络翻译会发送待翻译文本给所用服务，游戏译名及官方语言信息查询也可能联网。API Key 本机明文保存，使用时按协议发送给配置端点；费用、额度与服务条款以提供者为准。缓存与词典只能覆盖各自可处理的文本，不保证全程离线、译文准确性、固定延迟、游戏稳定性或账号安全。当前验证面向单机环境，未实现可靠反作弊检测。

## 文档

目录分工：`src/` 源码与启动入口维护；`docs/` 说明、变更和网页（`docs/site/`）；`tests/` 测试记录；`lab/` 设计实验记录（部分内部记录不随仓库公开）；`LICENSES/` 上游许可证；`tools/` 本机下载、备份和证据缓存（不入库）。

- [项目说明网页](docs/site/index.html) · [兼容范围与依赖](docs/site/engines.html)
- [文档导航](docs/README.md) · [验收路线图](docs/ROADMAP.md) · [测试记录](tests/README.md)
- [开发与 AI 修改入口](AGENTS.md) · [功能索引](architecture.json) · [系统地图](docs/SYSTEM-MAP.md)
- [安装与恢复说明](docs/INSTALLATION.md) · [依赖和参考清单](docs/OSS-INVENTORY.md)
- [历史实验](lab/)：过去的设计和特定环境记录，不是当前功能或效果承诺。

## 来源与许可

Copyright (c) 2026 Franca contributors。Franca 自有代码采用 [MIT](LICENSE)；上游组件与改编代码保留各自许可，见 [第三方声明](docs/THIRD-PARTY-NOTICES.md) 和 [LICENSES](LICENSES/)。软件按原样提供，不作担保。名称与 logo 不包含在代码授权中。

代码许可不统一覆盖游戏文本、译文、模型权重或其他内容；目前没有统一的翻译包分发许可。东方路线的补丁数据（`base_tsa`）不随本仓库分发，仓库只保存文件清单与哈希，运行时从上游公开镜像获取。项目没有声明获得平台、游戏厂商或所列开源项目的背书。

## 免责声明

Franca 是独立的第三方工具，与 Valve、游戏开发商及发行商无隶属或授权关系，不代表任何官方立场。东方 Project 相关内容属于二次创作性质，东方 Project 及其角色归上海爱丽丝幻乐团所有。本软件不包含任何游戏本体文件、游戏文本或译文内容；翻译在用户本机生成并保存在本机。如权利人认为本仓库有不妥内容，请通过仓库 Issues 联系，我们会及时处理。
