# Franca 系统地图

这是一张给开发者和 AI 共用的系统地图。日常修改从 `AGENTS.md` 开始，再按根目录 `architecture.json` 的 `features` 定位源码与验证入口。本文维护边界和已知耦合；功能入口及探针只在 JSON 中维护，工作状态只在 `docs/DEVELOPMENT-PROGRESS.md` 中维护。修改模块边界或关键状态文件时，同时更新本文与 JSON。模块依赖图与契约（输入/输出/不变量）见自动生成的 `docs/BLUEPRINT.md`（`--probe-blueprint` 对账，未声明边 FAIL），关键数据流时序见 `docs/BLUEPRINT-FLOWS.md`。

## 一条用户操作的路径

本机启动入口为根目录「Franca」快捷方式，由 `src/Launcher/Setup-Shortcut.ps1` 生成，指向标准 Release 输出，使用现有品牌图标。仓库网页在 `docs/site/`，更新日志与第三方声明归入 `docs/`；构建仍将声明以 `THIRD-PARTY-NOTICES.md` 原文件名复制到程序目录，许可证窗口的读取契约不变。

```text
游戏库开关（Desktop）
  -> GameTranslationService（Application）
  -> TranslationInstaller（Installer）
  -> BepInEx / XUnity 配置（Game Adapter）
  -> 游戏内 CustomTranslate 请求 /translate/<appid>
  -> RelayServer（Runtime）
  -> PackStore 命中，或 AppTranslator / 端点池（Translation）
  -> 翻译包 JSONL
```

## 模块边界

| 模块 | 当前代码 | 责任 | 不负责 |
|---|---|---|---|
| Desktop | `MainWindow`、`Views/`、`Desktop/EndpointAutoController`、`Desktop/LanguageApplyController`、`Themes/` | 组合服务、显示状态、接收用户操作、WPF 定时调度 | 协议细节、游戏目录写入 |
| Application | `GameTranslationService`、`KnownIssueRepairService` | 游戏启停/修复编排、运行时准备、故障修复流程 | 页面控件、提供者协议 |
| Game | `Models/GameInfo`、`SteamScanner`、`SteamAppInfoReader`、`EngineDetector`、`GameNameService`、`NameSegmenter`、`GameLibraryOrder`、`SteamIconService` | 游戏发现、本地 Steam 类型、引擎识别、译名和语言支持、排序和图标 | 安装具体运行时 |
| Installer | `TranslationInstaller`、`TranslationHealthService` | 安装、配置、健康检查、备份/修复；接收显式中继地址 | 启动中继、翻译文本调度 |
| Runtime | `RelayServer`、`GameRuntimeMonitor`、`KnownIssueCatalog`、`TranslationTextSegmenter`、`SegmentedTranslationService`、`GameTextSyncService`、`OverlayStatusModel` | 中继边界、并发、失败开放、运行进程监控与故障识别、长文分段与重组、游戏文本同步、悬浮窗状态模型 | UI、Steam 扫描 |
| Translation | `AppTranslator`、`TranslationProviders.*`、`Endpoint*`、`YoudaoWebTranslator`、`DictionaryService`、`DictionarySeeds` | 共享提供者协议、健康探测、端点选择与调用、内置词典 | WPF 定时器、主窗口、游戏文件写入 |
| Pack | `PackStore`、`XxHash64` | 翻译包读写和键规范 | 调用外部翻译服务 |
| Shared | `AppSettings`、`AppLog`、`UiStrings`、`AttributionCatalog`、`DnsWarmup` | 设置、日志、文本与鸣谢资源、DNS 预热 | 业务流程编排 |
| Diagnostics | `Diagnostics/ProbeRunner.*`、`LibraryProbe`、`ArchitectureProbe` | 命令行分发、按功能验证；App 只保留启动入口 | 正式业务流程 |
| Loader（实验） | `src/Loader` | IL2CPP 进程内引导与可诊断日志 | Franca 桌面端业务逻辑 |

## 目标依赖与当前例外

主要方向为 `Desktop -> Application -> Game / Installer / Runtime / Translation / Pack / Shared`；桌面可直接读取状态、调用查询服务，`GameNameService` 依赖 Translation，Installer 依赖端点定义。JSON 中的模块依赖用于导航，并非自动生成的完整调用图；真实引用由 `--probe-blueprint` 扫描并与 dependsOn 对账。`GameInfo` 仍包含展示属性，`SteamIconService` 返回 WPF 图像；当前是单程序集 WPF 应用，没有声称整个 Services/Models 已成为独立跨平台内核。

首次蓝图对账（2026-10-23）补入四条此前未声明但真实存在的边，均为既有设计而非新增耦合：desktop→diagnostics（App 分发 `--probe-*` 命令）、game→installer（`SteamScanner` 用 `TranslationInstaller.IsReady` 显示就绪状态）、installer→runtime（安装器写配置时读取 `TranslationTextSegmenter.MaxTextLength` 上限）、translation→pack（`DictionaryService` 复用 `PackStore.IsValidLanguage`）。

游戏库软件过滤在 Game 内完成：`SteamAppInfoReader` 只读本地 `appcache/appinfo.vdf` 的类型与安装目录元数据（v39–v41），扫描器按清单 appId 关联；无清单目录仅按无歧义的安装目录名关联类型，不据缓存补造安装身份。Game/Demo/Mod 类型优先于名称回退；明确软件类型、仅含 EA 反作弊启动器的辅助目录及既有软件名称特征可逆隐藏。缓存缺失、未知类型和损坏记录保留为未知，不把未知引擎当成软件。扫描条目不删除，设置只影响显示、计数与最近启动入口；不请求联网分类、不写 Steam 或游戏目录。

新增代码不得让 `Views` 直接解析 ini、调用 HTTP 或写翻译包，不得让 `Pack` 依赖 UI 或具体翻译端点。未来 Unreal、Godot 等实现应作为游戏适配器接入，避免在中继核心增加引擎特例；当前尚无正式适配器层。

端点配置由 `EndpointConfigurationService` 负责校验模型 JSON、生成不含密钥的 Agent 提示词及保存/移除凭证；仍使用 `AppSettings` 的既有本机明文文件。页面仅接收输入并调用服务，导入不执行代码、不发网络请求、不自动切换主用；删除当前 DeepSeek 凭证会回落到免费自动模式并发布新的运行时快照。`GameTranslationService.LaunchGame` 返回 Steam 启动协议是否成功提交，页面据此记录最近启动的 appId；不把协议提交当作游戏启动或翻译成功。

源语言/目标语言变化由 Desktop 的 `LanguageApplyController` 维护共享 5 秒倒计时，两个页面的 `LanguageApplyBar` 只显示和转发按钮。倒计时结束调用 `GameTranslationService.ApplyLanguagesAsync`，捕获本轮设置快照，与启停/手动修复串行；新选择取消旧轮后排队，旧结果不能覆盖新选择。仅修复已安装、进程确认未运行的 Unity 游戏；逐游戏保留 INI 备份、使用安装锁并保留停用标记，运行中/状态未知跳过、单游戏失败继续。缓存文件按目标语言准备，不把每个游戏轮流激活为当前同步游戏。跳过项需退出游戏后用进度条按钮重新批量应用（开始重新计时，快进立即应用）；不承诺游戏进程内热切换。第二语言仅供官方支持展示，不触发此流程。

## 本轮结构整改（2026-10-22）

本轮处理了已确认的五项耦合。验证记录见 `tests/2026-10-22-architecture.md`；真实游戏部署和多引擎扩展仍需各自验收。

| 项目 | 现在的边界 | 验证重点 |
|---|---|---|
| 自动端点 | 实例选择器接收候选、确认函数和取消信号；WPF 定时器在 Desktop；旧轮次不能覆盖新选择 | 确认失败、无变化、取消、重置时在途结果 |
| 游戏流程 | 页面调用 `GameTranslationService`；安装器不持有中继，已有 INI 仍需显式修复 | 启停可逆、修复后检查、自定义字段和独立备份 |
| 启动与诊断 | App 负责启动/退出；命令分发和原探针迁到 Diagnostics 的分功能文件 | 原命令兼容、真实界面与导航倒计时 |
| 协议与测速 | TranslationProviders 按 Core / Free / Byok / Custom 分文件；正式翻译与测速复用协议 | 本机模拟 GET/OpenAI、文本转义、语言、鉴权、限流及取消 |
| 中继身份与状态 | 中继和包存储按实例创建；`/translate/<appid>` 决定归档，单次请求捕获配置快照 | 同文不同游戏隔离、并发包读写、配置变化、实例启停 |

新安装/显式配置修复写入带游戏编号的地址。旧 `/translate` 请求仍可翻译，但不读写游戏包；旧 `ActiveGameAppId` 字段只兼容保留，不再决定缓存归属，设置页已移除手选入口。已有游戏不会被批量改写，需要逐游戏显式修复后使用新缓存路由。中继端口变化仍需检查游戏 INI 是否匹配。

长文由 Runtime 的 `TranslationTextSegmenter` 和 `SegmentedTranslationService` 处理：总输入保留上游 2500 限制，按段落/标点拆为至多 200 的提供者请求；片段共享队列，保留成功片段缓存，完成后整篇返回。未完成的分段请求以 503 触发重试，不提交半成品；游戏侧仍一次接收重组结果。配置健康检查同时管理总长度与段落保留设置。边界与证据见 `tests/2026-10-22-long-text.md`。

窗口拥有服务实例、自动选择定时器和事件订阅，关闭时释放；中继、后台同步任务与探针进程随窗口关闭停止。此轮未引入新框架或拆分程序集。

`UiStrings.cs` 的体积主要来自 15 种语言资源，不能仅按行数判定业务耦合；通过 key 定位即可减少阅读。源码集中在单个 WPF 项目和 `Services/` 中也不直接等于架构失败，应先解决职责和依赖，再按收益决定是否分目录或程序集。

东方 thcrap 路线暂属 Installer 侧的 `TouhouThcrapService`：首次启用时把 `ThirdParty/thcrap` 复制到 `<程序目录>/thcrap/`，补丁数据按仓库内清单 `ThirdParty/thcrap-repos/base_tsa.files.json` 从上游镜像下载并逐个校验 SHA-256（不随仓库分发）；生成 `repos/franca/<patchid>/` 补丁与运行配置，只交换游戏 exe 存根（真身 `.frorig`），不改游戏数据包。旧静态补丁态在启用前自动还原，停用/卸载走同一服务。离线或下载失败时以明确错误中止，不产生半成品。

## 可观察性约定

UE 静态准备暂属 Installer：`UePak/` 负责 pak v11 与 locres 格式，`UeStaticService` 只读指定原包并在游戏目录外生成覆盖包与完成清单；`StaticTextTranslationService` 按实例持有缓存和熔断状态，调用既有 Translation 提供者并复用 Runtime 的分段规则。不依赖 MainWindow，不启动中继，不接正式启用按钮，不写游戏目录；CLI 准备默认只用未停用的免费端点。缓存逐片段保留，完整文档全部成功才存为完整译文；准备包缺项明确标记 Incomplete 并保留英文。正式安装、停用/恢复及画面验收另行实施。

UE 准备的译文语言与文化槽位分离：`--language=ja` 使用 `packs/<appid>/ja`，`--slot=zh-Hans` 表示在原简中资源路径展示该日语译文，manifest 同时记录二者；不修改全局端点/界面语言设置。当前覆盖包不压缩：目标 UE4.26.2 首轮读取压缩覆盖包失败，故不能用 UE5 UnrealPak 校验替代目标引擎兼容性验证。

译文同步在发布 `Franca.txt` 前补建缺失的默认 `_AutoGeneratedTranslations.txt`，避免 XUnity 5.6.2 首次空缓存重载直接打开不存在的输出文件；只用 CreateNew 建空文件，已有缓存及并发创建的内容均保留。自定义 OutputFile 仍由 XUnity 管理，Franca 不据配置路径扩展写入范围。冷启动文件事件顺序及用户缓存保护由 `--probe-dictionary` 覆盖。

- 安装与修复：记录目标游戏、步骤、写入结果和可恢复备份位置。
- 运行时：记录中继启动端口、缓存命中率、端点失败/冷却原因，但不得写入 API Key 或游戏文本全文到常规日志。
- 游戏内故障：先检查配置健康度，再检查缓存、加载器日志、最后检查端点。

2026-10-26：`UeStaticService.Tables` 复用同一实例翻译服务，对已验证的 `GameText/Database/*EN.uasset` 配对结构提供 `--ue-table-inspect` / `--ue-table-prepare[-cached]`。`UeTextDataTable` 严格解析单导出、Id/Text 字段、Base/空 FText 历史；保持行身份与未修改字节，仅在明确目标命名空间和源文本键时为目标空项建立 Base 文本。其他表结构不自动兜底。富文本匿名结束标记 `</>` 不外发，完整/片段缓存及提供者结果均核对保护标记，损坏项重新续译。仍只在游戏外准备，正式启停边界不变；未来流程见 [UE 自动适配路线图](UE-ADAPTATION-ROADMAP.md)。

## 安装事务边界（2026-10-23）

Installer 内新增 `InstallationTransaction`，负责暂存归并、文件备份、清单、失败回滚与下次启用的未完成事务恢复。原文件与清单保留在游戏 `.franca/installations/<id>/`；恢复冲突停止并保留外部改动。Application 的启停入口及中继地址注入保持原边界。详细语义与限制见 [安装事务](INSTALLATION.md)，真实支持见 [兼容性矩阵](COMPATIBILITY.md)。
