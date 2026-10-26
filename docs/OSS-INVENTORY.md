# Franca 依赖和参考清单

本文件记录当前使用关系。许可证全文见 [LICENSES](../LICENSES/)，来源说明见 [第三方声明](THIRD-PARTY-NOTICES.md)。列为参考不代表集成、推荐或第三方背书；本清单也不表示发布包已完成逐项审核。

## 当前使用的组件与来源

| 项目 | 实际关系 | 已记录许可 |
|---|---|---|
| .NET / WPF | Windows 桌面框架 | MIT |
| BepInEx | Unity 安装路径下载的插件加载器 | LGPL-2.1 |
| XUnity.AutoTranslator | 安装的上游翻译插件；生产翻译协议的部分实现改编自其端点代码，见 TranslationProviders.*.cs 文件头 | MIT |
| Il2CppInterop | IL2CPP 环境中的互操作组件 | LGPL-3.0 |
| Harmony | 上游 Unity 运行环境的相关组件 | MIT |
| UnityDoorstop | 上游加载路径；src/Loader/loader.c 引导序列的来源，自研部分仍为实验 | LGPL-2.1 |
| thcrap | 东方 Project 运行时注入引擎；随仓库分发裁剪后的离线二进制子集（jansson、libpng、zlib-ng 各自许可见 THIRD-PARTY-NOTICES） | The Unlicense |
| thtk | 东方数据包只读解包/回封工具链，二进制随附 | BSD（三段式） |
| base_tsa（thpatch/thcrap-tsa） | 东方补丁数据；**不随仓库分发**，首次启用时按清单从上游镜像下载并校验哈希 | 上游未声明许可证 |
| Noto Sans SC | 东方路线简体中文字形渲染字体，随仓库分发 | SIL OFL 1.1 |

具体发行包、适用平台与来源见安装器和第三方声明。东方补丁数据的上游仓库未声明许可证，因此本项目只保存文件清单（路径 + SHA-256），不转发内容。运行时路径和第三方组件不保证适用于所有游戏。

## 未接入的历史技术调研

| 项目 | 曾调研的主题 | 当前产品关系 |
|---|---|---|
| RapidOCR、PaddleOCR、manga-ocr | 屏幕文本识别 | 无 OCR 集成，不是降级路径 |
| Ren'Py SDK、unrpyc | Ren'Py 翻译及脚本提取 | 未接入翻译适配器或工具链 |
| UE4SS、UAssetGUI、repak | Unreal 文本处理 | 未接入 |
| GARbro | 资源格式研究 | 未吸收提取逻辑 |
| gdsdecomp | Godot 资源格式 | 未接入 |
| UndertaleModTool | GameMaker 资源格式 | 仅调研 |
| Textractor、LunaTranslator | 文本截取与翻译流程 | 仅技术参考 |
| qingjian（青简） | 词典设计 | 仅思路参考 |
| Real-time-translation-typing | 端点故障转移思路 | 仅思路参考，无代码复用许可声明 |

上述条目不构成支持路线、实现顺序、交付日期或性能承诺。若未来实际复用，需重新确认确切版本、原始声明和使用方式，再更新本清单与对应源码；不能根据此前的调研标签推断届时的分发条件。

## 维护依据

以源码、实际下载包与日期验收证据为准，保留上游版权和许可文件。已存在改编代码，不能声称从不修改上游，也不能将所有代码统一描述为 Franca 原创或 MIT。新增组件时记录来源、版本和用途；发布时核对实际包含的代码、二进制、模型和内容。
