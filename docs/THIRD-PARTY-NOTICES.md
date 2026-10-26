# Franca — Third-party licenses and acknowledgements

Copyright (c) 2026 Franca contributors。Franca 自有代码采用 MIT，见随附的 LICENSE，允许使用、商用、修改和再分发（包括闭源），须保留版权与许可声明。软件按原样提供，不作担保。第三方作品仍按各自许可证授权；本项目许可证不覆盖第三方作品、模型权重、游戏内容或翻译包，也不授予项目名称和 logo 的商标权。

## 代码来源与随附全文

- CityHash：MIT，Copyright (c) 2011 Google, Inc.。`Services/UePak/CityHash64.cs` 的 CityHash64 算法由 Google CityHash 改编为 C#，UE 字符串封装由 Franca 补充；[来源](https://github.com/google/cityhash)，完整声明见 `LICENSES/CityHash.txt`。pak 格式交叉核对参考 [repak](https://github.com/trumank/repak)（Apache-2.0），不随附其二进制。

- XUnity.AutoTranslator：MIT，Copyright (c) 2018 Bepis。`src/Franca/Services/TranslationProviders.Free.cs` 的 Bing / DeepL 端点流程改编自该项目，并由 Franca 修改。原始路径已在方法注释中注明。完整原始声明见 `LICENSES/XUnity.AutoTranslator.txt`。
- .NET/WPF：MIT，完整声明见 `LICENSES/wpf.txt`。依赖框架安装提供其自身第三方声明；发布自包含运行时时须同时保留运行时提供的 notices。
- UnityDoorstop：LGPL-2.1，[上游源码](https://github.com/NeighTools/UnityDoorstop)。`src/Loader/loader.c` 的 IL2CPP/CoreCLR 引导序列基于其 `src/bootstrap.c`，Franca 修改包括诊断、配置控制与 Unicode 路径；该改编文件不改标 MIT，完整许可见 `LICENSES/UnityDoorstop.txt`。自研代理仍为未纳入正式安装的实验，分发时须保留相应源码与许可义务。
- BepInEx：LGPL-2.1，见 `LICENSES/BepInEx.txt`。安装器下载原版 v5.4.23.5、v6.0.0-pre.2 或 be.788+5b766a3；源码及构建信息见 [上游](https://github.com/BepInEx/BepInEx)。
- Il2CppInterop：LGPL-3.0，见 `LICENSES/Il2CppInterop.txt` 及其引用的 `LICENSES/GPL-3.0.txt`。随 IL2CPP 运行环境按需获取；源码见 [上游](https://github.com/BepInEx/Il2CppInterop)。
- Harmony：MIT，见 `LICENSES/Harmony.txt`，由 Unity 插件生态引入；[上游](https://github.com/pardeike/Harmony)。
- thtk：BSD（三段式），[上游](https://github.com/thpatch/thtk)。随附预编译 Windows 二进制 `thdat.exe`/`thmsg.exe`/`thtk.dll`（ThirdParty/thtk/），东方 Project 自研引擎静态翻译适配器（`src/Franca/Services/TouhouStaticService.cs`）经其解包/回封 th10/th11 的 thdat 容器与 .msg 对话；二进制及 `COPYING*.txt` 原样保留，完整声明见 `LICENSES/thtk.txt`。
- thcrap（Touhou Community Reliant Automatic Patcher）：The Unlicense（公有领域），[上游](https://github.com/thpatch/thcrap)。随附经裁剪的预编译二进制子集（`ThirdParty/thcrap/bin/`：thcrap_loader、thcrap、thcrap_tsa、win32_utf8、jansson、libpng16、zlib-ng、bmpfont_create_*；已剔除自更新、Steam 集成与配置器，完全离线），东方路线经其运行时注入渲染译文；完整声明见 `LICENSES/thcrap.txt`。捆绑组件各自许可：jansson（MIT，`LICENSES/jansson.txt`）、libpng（`LICENSES/libpng.txt`）、zlib-ng（Zlib，`LICENSES/zlib-ng.txt`）。
- base_tsa（thpatch/thcrap-tsa）：**本项目不随附其内容**。上游仓库未声明许可证，且其内容包含东方 Project 原作格式的游戏数据；Franca 仅在首次启用东方路线时按仓库内清单 `ThirdParty/thcrap-repos/base_tsa.files.json`（固定上游 commit `c803fdc`、逐文件 SHA-256，另记 thpatch 镜像当前部署的哈希）从 [thpatch 镜像](https://mirrors.thpatch.net/nmlgc/base_tsa/) 或该 commit 的 GitHub raw 下载并逐个校验。下载源为上游公开分发，本项目不转发这些文件；清单只记录路径与哈希。
- Noto Sans SC：SIL Open Font License 1.1，见 `LICENSES/NotoSansSC.txt`（随附 `ThirdParty/thcrap/fonts/OFL.txt` 同文）。[上游](https://fonts.google.com/noto)。随附可变字重 TTF（`ThirdParty/thcrap/fonts/NotoSansSC.ttf`），用于东方路线简体中文字形渲染，字体文件本身不被修改。

许可证副本核对日期：2026-10-21（thtk 条目随静态适配器于 2026-10-24 补入；thcrap/base_tsa/Noto Sans SC 条目随运行时渲染路线于 2026-10-24 补入；llama.cpp 随本地大模型功能移除于 2026-10-24 摘除）。安装器下载的发行包及其所有附带组件应保留原有版权、许可证、NOTICE 和源码信息；此清单是顶层归因，不是下载包全部传递依赖的审计证明。LGPL 组件再分发时，须提供所分发确切版本的对应源码或许可证允许的源码获取方式，保留用户替换/修改组件及为调试这些修改进行逆向工程的权利。仅列上游主页不能代替这些分发义务。

## 启动鸣谢清单

以下为全部公开技术和设计来源的许可证标签；鸣谢本身不表示已包含其代码。许可证名称不改变各上游原有条款。

| 项目 | 许可证 | 来源 |
|---|---|---|
| BepInEx | LGPL-2.1 | https://github.com/BepInEx/BepInEx |
| Il2CppInterop | LGPL-3.0 | https://github.com/BepInEx/Il2CppInterop |
| XUnity.AutoTranslator | MIT | https://github.com/bbepis/XUnity.AutoTranslator |
| RapidOCR | Apache-2.0 | https://github.com/RapidAI/RapidOCR |
| PaddleOCR | Apache-2.0 | https://github.com/PaddlePaddle/PaddleOCR |
| manga-ocr | Apache-2.0 | https://github.com/kha-white/manga-ocr |
| .NET/WPF | MIT | https://github.com/dotnet/wpf |
| Ren'Py SDK | MIT（主体）；LGPL 等（所含组件） | https://www.renpy.org/doc/html/license.html |
| unrpyc | MIT | https://github.com/CensoredUsername/unrpyc |
| UE4SS | MIT | https://github.com/UE4SS-RE/RE-UE4SS |
| UAssetGUI | MIT | https://github.com/atenfyr/UAssetGUI |
| repak | Apache-2.0 | https://github.com/trumank/repak |
| GARbro | MIT | https://github.com/morkt/GARbro |
| gdsdecomp | MIT | https://github.com/GDRETools/gdsdecomp |
| UndertaleModTool | GPL-3.0 | https://github.com/UnderminersTeam/UndertaleModTool |
| Textractor | GPL-3.0 | https://github.com/Artikash/Textractor |
| LunaTranslator | GPL-3.0 | https://github.com/HIllya51/LunaTranslator |
| Harmony (0Harmony) | MIT | https://github.com/pardeike/Harmony |
| thtk | BSD | https://github.com/thpatch/thtk |
| qingjian（青简） | GPL-3.0-or-later | https://github.com/qingjian-team/qingjian |
| Real-time-translation-typing | 未声明许可证（保留所有权利） | https://github.com/sxzxs/Real-time-translation-typing |

复用 GPL 项目代码时须核对具体版本与授权兼容性，保留原版权、许可和修改声明；Franca 自有代码的 MIT 授权不会改变上游的独立授权。GPL 代码不能改标 MIT；如需吸收或链接，须按上游条款评估组合分发条件。无许可证项目不允许据此复用或分发代码。Ren'Py 的完整 SDK 包含多种许可证，须以其完整声明和具体文件为准。

## 授权范围与历史

2026-10-21 按维护者最终要求，Franca 自有代码采用 MIT；第三方作品保持上游授权。此前若已有合法授出的许可证，其效力不会因后续改换协议而自动撤回。该声明不授权重新许可其他版权持有人的代码。

本项目大量依赖上游开源组件，部分代码改编自上游，并非全部独立原创。再次修改前须核对文件头、原项目、版本及许可证并保留声明；修改可能引入注入失败、运行时不兼容、端点失效或数据损坏，应备份数据并在隔离环境验证。此为来源与风险提示，不是额外许可条件。

翻译包与游戏文本的许可由各作者或权利人另行指定；本项目不随附游戏文本、译文或模型权重。
