# Franca 开发进度

更新：2026-11-05。本文件是当前状态唯一入口；[路线图](ROADMAP.md) 维护执行顺序，[兼容性矩阵](COMPATIBILITY.md) 维护逐游戏验收边界。详细开发经过保留在内部归档（不随仓库公开）。

## 当前结论

2026-11-05 发布 `Franca0.1-1105`：RenPy 实验路线进游戏库（一键启用，DDLC 界面中文真机闭环）+ MapleStory 仅识别 + Unreal 备注更新（UE5 容器已实证）。版本同步五处（csproj 三元组、ReleaseUpdateProbe 常量、README/site、CHANGELOG、ANNOUNCEMENT），站点已测游戏表加 RenPy 行。GitHub Release `v0.1.20261105` 已上传 EXE + SHA-256。详见 [发布记录](../tests/2026-11-05-release.md)。

2026-11-05 **RenPy 游戏库一键启用落地（准备流程 GUI 化）**：未安装的 RenPy 游戏点「启用翻译」不再弹文件对话框——`GameTranslationService.RenPyPrepareAndInstallAsync` 自动接管：默认产物根 `%AppData%/Franca/renpy-prepared/<appId>/`（PackStore 加 `RootPath` 只读访问器以复用中继同一缓存根），有有效 manifest 直接事务安装；缺失则先自动跑翻译准备（提取→翻译→打包，行内进度接 `game.SetInstallProgress` 百分比+文案）；游戏更新哈希漂移（Install 抛 differ）自动删 manifest 重跑准备（缓存逐条复用、只译增量）再装。UiStrings 新增 `renpy.prepare.*` 5 key ×15 语言，`renpy.install.hint` 改为「自动准备（如需要）并安装」口径，`engines.note.renpy` 尾部「准备仅 CLI」同步改写。旧 `RenPyInstallAsync`（选外部 manifest 入口）随对话框流程移除，CLI 三命令不受影响。真机验证（DDLC，UIA 驱动）：游戏库搜 DDLC → 点「启用翻译」→ 行状态「已就绪」变「启动游戏」，磁盘 `game/tl/zh_cn`（strings.rpy/franca.rpy/NotoSansSC.ttf）落盘、`.franca` 新 committed renpy 日志在案（快路径：产物已备好秒装）。**冷准备路径（无产物时 GUI 内自动翻译）未真机跑过**——PrepareAsync 本体 CLI 侧已验证，GUI 仅包了一层编排，如实标注。Release 0 错误，renpy 14、navigation、blueprint、architecture 全过。

2026-11-05 **RenPy 接入游戏库 GUI（实验适配）：安装/卸载/状态识别全链路真实渲染与真实卸载验证**：引擎目录 RenPy 升「实验适配」（`engines.note.renpy` ×15 语言，队列位置与 UE 相邻）；GameInfo 增加 RenPy 状态块（RenPyInstalled 读事务日志、主按钮同 UE 位：未装「启用翻译」（选准备产物 manifest.json）→ 已装「启动游戏」），GamesPage 详情区加「启用翻译/卸载翻译」按钮组；`GameTranslationService` 新增 RenPyInstallAsync/RenPyUninstallAsync（游戏停止检查 + `_operations` 串行化）。UiStrings 新增 4 key ×15 语言（renpy.install.hint + 3 条日志）。真实验证：DDLC 行状态「已就绪」、详情区 Ren'Py 标签 + 卸载按钮、引擎页实验徽标与备注全部真实渲染；**GUI 卸载真实走通**（确认对话框 → rolled-back → tl 目录移除）。过程中又抓一个 bug：卸载只删 journal 登记的 .rpy/.ttf，RenPy 首启编译的 `tl/<语言>/*.rpyc` 不在 journal 里会残留并被下次启动加载——回滚现连同删除所卸 .rpy 的 .rpyc（与安装侧重装清 .rpyc 同款处理）。导航探针 `engine_renpy_supported_experimental` 替换 recognized-only 断言（13 项总数不变，RenPy 原已入列）；Release 0 错误，renpy 14、navigation、blueprint、architecture 全过。准备流程仍仅 CLI（与 UE 同口径）。当前游戏处干净未安装态，可 GUI 一键重装。

2026-11-05 **RenPy 屏幕串提取 + 字体三层覆盖真机验收通过（DDLC 界面中文闭环）**：①屏幕（screen）块文本提取落地——查明 RenPy 运行时对 screen 文本统一走 `substitutions.substitute()` → `translate_string()`（`renpy/substitutions.py`，Input 例外），提取器新增 SLDisplayable 处理（`renpy.text.text.Text`/`_textbutton`/`_label` 取 positional[0]，显式 `_()` 与裸字面量都收、含 `[` 插值跳过），DDLC 实测多收 40 条（History/Skip/Auto/Load/Settings/Yes/No 等）；②**字体 glue 三层方案**（6.99 上 `translate python` 不生效已实证）：`translate <语言> style default:` 块 + `init 9999 python` 遍历全样式覆盖 + **patch `Text.__init__` 强制 kwargs["font"]**——第三层为真机抓回的严重回归所必需：单选菜单按钮样式运行时延迟创建，样式遍历盖不到，字体缺 CJK 时文本零宽→按钮热区塌缩→整页卡死（"我同意。"页），patch 后按钮中文渲染且可点击；③引擎内置串补偿：`renpy/common` 里的 `empty slot` 等不在任何 game .rpyc，生成器内嵌固定映射补写（zh-CN，可随语言扩充），Load 页 9 槽全显"空槽位"（剩余英文仅 `Page {}` 页码插值串，边界如实记录）；④**真机抓回两个安装器 bug**：重装时 RenPy 上次编译的 `tl/<语言>/*.rpyc` 与新 `strings.rpy` 同时加载 → `A translation already exists` 启动崩溃，安装提交后现清除所装 .rpy 的过期 .rpyc；`--renpy-install/--renpy-uninstall` 传内层 `game` 目录会把产物装进 `game/game/tl` 并使后续扫描坠入 stray 目录（"No compiled scripts"），CLI 现归一化到安装根（认 `renpy/`  sibling 特征）。真机验收：免责声明三页中文、"我同意。"按钮中文可点、主菜单侧栏 5 钮全中文（此前口口口 tofu）、Load 页标题/侧栏/空槽全中文。合成探针 renpy 14/14、blueprint 14、architecture 51 全过。已知边界：主菜单按钮为图片贴图无文本可译；`Page {}` 页码未译；RenPy 8 无真机样本；GUI 启用入口未接（仅 CLI）。详见 [lab/015](../lab/015-renpy-static-adapter.md)。

2026-11-05 **RenPy 静态适配器真机验收通过（路线图排队项①闭环，样本 DDLC/698780）**：准备→安装→真机→卸载→重装全链路走通。准备二轮重跑 8376/8376 全译（一轮缓存复用 8354，免费端点）；事务安装（游戏脚本现场重扫哈希与清单比对防更新漂移）→ 真机窗口化启动：**开场免责声明三页全部中文渲染正常**（截图证据在 tools/，不进库）。过程中修复两个真机才暴露的 bug：①**字体 glue 在 RenPy 6.99.12 上 `translate <语言> python: style.default.font=...` 不生效**（中文全部零宽不渲染、只剩 URL 等 ASCII 尾巴），改 `translate <语言> style default: font "..."` 块后正常——生成器已改；②安装日志漏盖 `Engine="renpy"` 章导致卸载/已装判定恒 not-installed，已补盖章并对旧记录按 game/tl/ 路径前缀兼容识别。卸载往返验证：干净记录 rolled-back、tl 目录移除、被手改文件正确拒绝（Rollback conflict）。已知边界（如实记录）：**屏幕（screen）块内文本未提取**（`_("...")` 在 screen AST 而非 PyCode，如 Load 页 empty slot/Return/侧栏按钮保持英文）；主菜单按钮为图片贴图无文本可译。合成探针 renpy 9/9、blueprint 14、architecture 51 全过。当前游戏处已重装中文态，留待用户游玩验证。详见 [lab/015](../lab/015-renpy-static-adapter.md)。

2026-11-05 RenPy 静态适配器第一阶段落地（路线图排队项①启动，样本 DDLC）：选型决策——v1 不碰 translate 块标识复刻，只生成官方钦定的 `translate <语言> strings:` 块（无翻译块时对话运行时自动回退字符串翻译，RenPy 6/7/8 通用；块标识算法已存档在 [lab/015](../lab/015-renpy-static-adapter.md) 待未来需要时实现）。新组件 `src/Franca/Services/RenPy/`：`PyPickle`（pickle 协议 0-4 解析器，未知类收编 FakeClass 对象图）、`RpaFile`（RPA-3.0/2.0 归档，索引语义对照 renpy/loader.py）、`RpycFile`（v1/v2 解包）、`RenPyStringExtractor`（AST 遍历收 Say/菜单文本）+ 只读 CLI `--renpy-extract`（含节点类型直方图）。格式陷阱三则：proto2 布尔是独立 opcode 0x88/0x89；RenPy AST 节点 state 是 `(None, {attrs})` 二元组；RPA-3.0 索引值是异或后的段列表（2/3 元组、可拼接）。真包验证（DDLC，RenPy 6.99.12.4/py2.7，随包带引擎源码即权威格式依据）：35 个 rpyc 全解析 0 错误，10821 条 Say → 8378 条去重文本（插值保留）。Release 0 错误；architecture 51 项、blueprint 14 项全过（新模块自动吸收）。**仅为只读提取能力，尚不能安装翻译**，下一步：翻译管线接入→tl 产物生成（含强制语言与字体覆盖）→事务安装→真机验收。设计见 [lab/015](../lab/015-renpy-static-adapter.md)。

2026-11-05 **UE5/IoStore 判决性实验真机通过**（排队项②闭环）：MECCHA CHAMELEON（UE5，IoStore .ucas/.utoc 容器）+ 家园 3 两款真机走完全链路。结论：**UE5 容器游戏接受 legacy pak 覆盖包**（pakchunk99-Windows_P.pak 放 Paks 目录即生效，MECCHA 菜单"创建服务器/寻找服务器/退出游戏"出现简中译文）——IoStore 写入支持可长期回避，仅当某游戏文本确在容器内时才需评估 utoc/ucas 读取。配套代码落地：①`UePak/Oodle.cs` 新增 Oodle 解压（LoadLibrary 本机 oo2core dll + P/Invoke，**DllImport 必须 CharSet.Unicode**，默认 ANSI 编组致 LoadLibraryW 必败；dll 不随 Franca 分发，CLI 用 `--oodle=<路径>` 注入）；②`PakFileReader` Extract 按索引压缩方法名路由（"Oodle" 前缀），探针加 `oodle_method_routing`（ue-pak 10→11）；③`UeStaticService` 部分官方翻译修复——目标槽缺键不再拒绝、缺失键补入译文（新 `CreateNamespace`），共有键哈希漂移仍拒绝，探针 44→50；④`UePakInstallService` 两处真机 bug：`HashFile` 改流式 SHA256（>2GB 不炸）、基线哈希比较改 OrdinalIgnoreCase。真机插曲：MECCHA 游戏文件在测试期间被 Steam 热更新（appmanifest lastupdated 与文件 mtime 吻合），非安装器写入；卸载已验证（事务回滚、覆盖包移除），家园 3 同样安装→验证→卸载还原。Release 0 错误；ue-pak 11 / ue-static 50 / ue-install 18 / navigation 76 / startup 26 / blueprint 14 / architecture 51 / dictionary 33 全过。引擎页 `engines.note.unreal` ×15 语言同步（UE5 容器已实证可覆盖，加密 pak/IoStore 写入不支持）。未改版本号、不触发更新检查（实验验证非功能变更）。

2026-11-04 引擎识别新增 MapleStory（冒险岛，Nexon/Wizet 自研 2D 引擎）：应用户询问「能否支持冒险岛」调研后结论——经典端游为 Wizet 自研引擎 + 在线 MMO 反作弊环境（GameGuard/HackShield/NGS）+ 国服已有官中，三条现有翻译路线（Unity 注入/东方 thcrap/UE pak 覆盖）均不适用，明确**不支持**。落地为仅识别条目：`GameEngine.MapleStory`、检测签名 `MapleStory*.exe + 限深 2 的 *.wz 数据包`（依据公开客户端布局，暂无本机样本，签名注释已注明待真机复核）、`EngineSupportCatalog` 末尾追加（IsSupported=false 走「仅识别」徽标），新增备注 key `engines.note.online` ×15 语言（用户口径：网络游戏存在被视为外挂的风险，不支持网络游戏）。导航探针 `engine_catalog_covers_engines` 12→13 并加 `engine_maplestory_recognized_online_unsupported`（75→76 项全过）。Release 0 错误。本次不改版本号、不触发更新检查（识别条目非功能变更）。

2026-11-04 发布 `Franca0.1-1104`：左侧导航「公告」「更新」（启动自动检查、未读 ● / 可更新 !）+ 8 小时缓存窗口 + 页面手动刷新。版本同步四处（csproj、ReleaseUpdateProbe 常量、README/site、CHANGELOG），`ANNOUNCEMENT.md` 更新为 2026-11-04 条目（用户下次启动会看到 ●）。GitHub Release `v0.1.20261104` 已上传 EXE + SHA-256。`Test-Standalone` 全过（34 组件哈希一致、修复与哨兵保持）。详见 [发布记录](../tests/2026-11-04-release.md)。

2026-11-04 公告/更新降频（按用户反馈）：新增 8 小时缓存窗口（`GitHubHttp.IsFresh` + 设置持久化 `LastAnnouncementFetchUtc/LastReleaseCheckUtc`），窗口内选中页面/启动检查不再联网、直接展示缓存；公告正文缓存到设置（`CachedAnnouncementId/Text`），未读徽标重启后仍在；两页各加「刷新」按钮强制重查（带盲文动画）。`github.refresh` key ×15 语言；缓存窗口边界探针并入 `--probe-release-update`（24→26 项）。Release 0 错误；navigation 75、release-update 26、startup/architecture/blueprint 全过。

2026-11-04 公告/更新改为左侧导航项（按用户要求从设置页内按钮改为与设置平级）：导航顺序游戏/端点/设置/**公告**/**更新**/状态/引擎/日志/鸣谢（3、4 为新项，后四项索引 5–8 全量调整，导航探针同步）。每次启动自动检查；公告未读导航徽标 ●（`NavItem.Badge`），有新版徽标 !；选中页面即带盲文加载动画重新检查——公告页展示 `ANNOUNCEMENT.md` 全文并记已读，更新页显示当前版本 + 已是最新/新版 + 下载按钮。探针窗口（startServices=false）跳过真实网络只读缓存。公告解析/404/空态探针并入 `--probe-release-update`（20→24 项），导航探针加 `announcement_page_rendered`/`update_page_rendered`（75 项）。Release 0 错误，21 项合成探针全过。

2026-11-03 发布 `Franca0.1-1103`：Unreal 游戏库启用入口（manifest.json 事务安装）、引擎页实验适配徽标统一、设置页可选代理修复官方支持刷新。版本同步四处（csproj 三元组、ReleaseUpdateProbe 常量、README/site 下载链接、CHANGELOG）。本机发布环境两处变化需记录：pwsh 已卸载改用 Windows PowerShell 5.1，`Test-Standalone.ps1` 修了两处 5.1 兼容（ExitCode 恒空改输出三查含末行 PASS、Path.GetRelativePath 手工裁剪）；GUI 长开锁定 bin 输出曾致健康探针误跑旧 1027 二进制（流程：先关 GUI 再构建）。21 项合成探针全量体检约 670 PASS / 0 FAIL（1103 构建），Test-Standalone 全部通过（34 组件哈希一致、损坏修复、哨兵保持）。详见 [发布记录](../tests/2026-11-03-release.md)。

2026-11-03 按用户要求统一 UE 按钮文案与引擎页口径：UE 安装按钮不再单写「安装 UE 译文…」，与 Unity 行统一用「启用翻译」（`ue.install` key 全语言移除，`UeInstallLabel` 直接取 `game.enable`；选择产物清单的说明保留在 tooltip/对话框标题 `ue.install.hint`）。引擎页全部已支持引擎（Unity IL2CPP/Mono、Touhou、Unreal）统一标「实验适配」徽标（`engines.supported` key 移除，导航探针加 `engine_supported_all_experimental_badge` 锁定）；`engines.note.unreal` 四行说明精简为一行关键限制（未加密 pak v11；UE5/IoStore/加密未验证），其余引擎说明本已单行保留。Release 0 错误；navigation 73 / ue-install 18 / ue-fonts 23 / ue-static 44 / blueprint 14 / architecture 51 共 224 项全过。

2026-11-03 「刷新官方支持」失败根因与修复：官方支持/译名解析请求 Steam 商店 API，`GameNameService` 默认客户端硬编码 `UseProxy=false`（早前实测国内直连更稳的遗留），本机现已直连超时（实测 10s 超时 000，走本机代理 200）。修复为可选代理：新增 `AppSettings.ProxyUrl` + 设置页输入框（失焦/回车保存，15 语言文案），`GameNameService.ProxyOverride` 变更即重建默认客户端；默认仍直连，探针注入客户端不受影响。Release 0 错误，navigation 72 / ue-install 18 / ue-fonts 23 / ue-static 44 / blueprint 14 / architecture 51 共 222 项全过。设置项随既有 settings JSON 持久化，旧设置文件无此字段默认为空（直连）。

2026-11-03 UE 翻译选项对游戏库一等开放（同轮第 3 项）：Unreal 游戏主按钮与 Unity 行同位——未安装「安装 UE 译文…」（选 `--ue-prepare` 产物 manifest.json），已安装变「启动」（`CanLaunch` 认 UE 事务日志）；`IsSupported` 纳入 UE，注入式开关仍限 Unity/Touhou（新增 `HasToggle` 门控），UE 详情区保留安装/修复/卸载按钮组。引擎页 Unreal 升「已支持（实验徽标）」，`engines.note.unreal` 15 语言文案同步，导航探针加 `engine_unreal_supported_experimental`（72 项全过）。Release 0 错误，ue-install 18 / ue-fonts 23 / ue-static 44 / blueprint 14 / architecture 51 全过。真机事务整循环与 UE5 游戏验证按用户决定后续再测。详见 [UE 安装记录](../tests/2026-11-03-ue-install.md)。

2026-11-03 UE 安装接入 GamesPage（阶段四第二批）：Unreal 游戏详情区新增「安装 UE 译文…/修复/卸载翻译」操作组，安装入口选择 `--ue-prepare` 产物目录的 manifest.json，`UePakInstallService.ResolvePrepared` 从清单推导全部安装参数（appId、SourcePak 游戏内归属、原 pak 长度+索引 SHA1 基线、产物 pak 哈希逐项校验，基线 SHA-256 现场计算），工作流在 `GameTranslationService.UeInstallAsync/UeUninstallAsync/UeRepairAsync`（复用游戏停止进程检查、`_operations` 串行化、写操作离开 UI 线程）。UE 行状态显示已就绪/未启用，不再显示「暂未支持」。UiStrings 新增 6 key × 15 语言。探针 `--probe-ue-install` 扩至 18 项（ResolvePrepared 合法/拒绝五态）；Release 0 错误，ue-install/ue-fonts/ue-static/blueprint/architecture 共 150 项全过。真机事务整循环待验（当前手工部署的包会被服务正确拒绝为外来文件，需先删除再装）。详见 [UE 安装记录](../tests/2026-11-03-ue-install.md)。

下一步：嫁接手术移植决策、本作真机事务验收。详见 [字体健康检查记录](../tests/2026-11-03-ue-font-health.md)。

2026-11-03 UE 字体健康检查组件落地（阶段三静态检查目标）：应用内新增最小 TTF 解析器 `TtfFont` 与 `UeFontHealthService`（.ufont 即裸 TTF），合成夹具探针 `--probe-ue-fonts` 15 项，真实只读检查命令 `--ue-font-check`（JSON 报告、有缺字退出码 2）。真实数据交叉验证：25 款本作字体中 15 款符号字体（无假名无 CJK，含 KS_Meldir/Skech，**不能也不承接日文文本，嫁接需求闭环**）、10 款文本可用字体的缺字集合与此前 Python 分析完全吻合；8 款嫁接后字体复检 0 缺字。Noto 可变字体经权威源（google/fonts raw）与镜像哈希比对一致。Release 构建 0 错误，ue-fonts（19 项）/ue-static/blueprint/architecture 探针 128 项全过。Font 资产引用链解析同日落地：`UeCookedPackageRefs` 读取 cooked .uasset 导入表，`--ue-font-refs <pak>` 列出 112 个 Font 复合资产的有序 FontFace 回退链（80 个共享回退 YoonGothicPro770，与缺字矩阵吻合）。复合字体链级健康报告同日闭环（阶段三静态检查完整交付）：`AnalyzeChain` 回退链并集语义 + `--ue-font-report <pak> <textFile>... [--facesDir <dir>]`。本作实证：81 条字体链仅 3 条对译文缺字（Skech 装饰链 303 字——官方不路由 CJK 互证、TW Switch 链 178 字、Roboto 引擎字体 4 字——译文里只有 2 条开发测试串）；`--facesDir` 加载 8 款嫁接字体后复检仅剩 Roboto 测试串。TalkFontTable 路由同日闭环（`--ue-font-routes`）：本作五语言对话字体路由明确（活动槽 ZH_CN→FONT_MJ_CN_WeiBei_PC，默认字体 NewCinema 完整日语、对话链零缺字），路由级风险全部排除，嫁接修复范围确证充分。同日阶段四首批落地：`UePakInstallService` 覆盖 pak 事务化安装/停用/恢复（原 pak 零写入+基线哈希校验+外来冲突拒绝+修复从事务副本恢复），日志复用 InstallationTransaction 格式共享崩溃恢复；CLI 三命令（--ue-install/--ue-uninstall/--ue-repair），合成目录探针 12 项全过。详见 [UE 安装记录](../tests/2026-11-03-ue-install.md)。

2026-11-03 歧路旅人 2 字体嫁接**真机验收通过**：11-02 的"译文+字体"包（`fontfix-1030`）启动成功但观感不通过——补字与原字形深浅不一（诊断：共享回退字体原字形 28px 墨量 0.592 vs Noto Regular 补字 0.405，差 +0.187 量级）。修复为**逐字体字重匹配**：从 Noto Sans JP 可变字体实例化 250–900 候选档，按渲染墨量最接近原则逐款选字重（共享回退选 500，其余 250–500），8 款字体重嫁接后构建 `pakchunk99-fontfix-1102.pak`（SHA-256 `4841B371…`），真机确认补字笔画粗细与原字形一致。工具链陷阱记录：可变字体实例化须 `inplace=False` 取返回值，`inplace=True` 对本字体静默失败。覆盖包目前仍部署在游戏目录供用户游玩，恢复基线随时可执行（原 pak 哈希已存档）。详见 [字体嫁接记录](../tests/2026-11-02-octopath2-fonts.md)。

2026-10-30 歧路旅人 2 窗口化自动测试受阻：当前磁盘已有与完整准备代一致的正常日语包，原 pak 哈希及 build 未变；经 Steam 携带窗口化/静音参数启动后，Computer Use 两次捕获游戏画面超时，未操作菜单或剧情、未做停用恢复。已停止本次测试进程、恢复变动配置并保留原覆盖包；不增加真机验收结论。详见 [本轮记录](../tests/2026-10-30-octopath2-live.md)。

同轮首页问号排查：表内连续问号均来自原文占位（含标题页隐藏挑战项）；另已从真实字体 cmap 确认简中字库缺少部分日语菜单用字，实际首页字体引用与画面尚未核实。用户要求避免干扰办公，后续限后台检查；详细证据仍见同轮记录。

2026-10-27 对外入口调整：README 与说明网页增加玩家用途、已测游戏摘要、下载和测试反馈入口，新增玩家测试步骤与 GitHub 反馈表；同步自包含 EXE、Book of Hours 首次显示和歧路旅人 2 正常包准备完成的边界。仅文档与静态资源变化，验证及素材缺口见 [页面记录](../tests/2026-10-27-public-pages.md)。

2026-10-27 新增 Book of Hours（Unity Mono）首次启动检查：正式服务安装及 BepInEx/XUnity 加载成功，游戏产生印尼语菜单和多行提示缓存；用户随后确认实际翻译显示成功；自动抓图超时，具体页面覆盖及重启/停用恢复待验。游戏与独立测试中继暂保留运行，用户数据已有备份，收尾事项见 [本轮记录](../tests/2026-10-27-book-of-hours.md)。

2026-10-27 按用户要求已发布 `Franca0.1-1027` Windows x64 自包含单 EXE（自带 .NET、首启释放组件），README 增加直接下载入口；正常启动后台检查固定 GitHub 仓库的最新正式 Release，有新版显示下载按钮，断网不阻塞，不自动覆盖。发行包验证、更新探针及界面/架构验证见 [发布记录](../tests/2026-10-27-release.md)；未新增真实游戏验收。

当前已有 Unity IL2CPP / Mono 与东方 ZUN 系（thcrap 路线）的逐游戏真机证据。东方 th10/th11 已确认所记录对话与界面中文；其他作品仍按能力探测与逐款证据限定，th06 PKGL 不支持。2026-10-25 按用户决定先推进 UE 静态准备，Godot 暂缓；UE 已开始实验性 pak/locres 与游戏目录外准备流程，尚未接正式启用入口或完成游戏内验收，不能表述为已支持。执行门槛见 [路线图](ROADMAP.md)。

桌面 v0.1 原型已新增 BALL x PIT（IL2CPP）和 Potion Craft（Mono）的真机菜单闭环：无翻译运行时基线安装、英语源 → 印尼语画面、重启缓存复用、停用/恢复重启、配置注错修复重启。没有进入存档游玩，不能扩大为全游戏覆盖或长期稳定。验收源码基线为 `fcc1cce`，详细证据见 [真机记录](../tests/2026-10-23-live-games.md)。

2026-10-24 用户明确将长时间游玩留作手测。自动侧已修复默认布局下首次空缓存重载缺文件的准备顺序，构建及词典/同步 33 项、架构 51 项、蓝图 14 项通过；仅临时夹具，未补真机空缓存重验。退出码审计更正为 8 次启动中 2 次退出码 1，根因仍未确定；悬浮窗交互仍待验。见 [修整记录](../tests/2026-10-24-unity-cleanup.md)。

此前「只做后台检查」轮次未启动真实游戏；本轮用户明确授权执行并允许以非官方语言验收。两款游戏经 Steam 小窗口启动，传入禁用音频参数，仅操作指定菜单，未重启 Steam 或关闭其他应用。结束时恢复原用户数据/PlayerPrefs；BALL x PIT 恢复上游加载器；两游戏配置恢复日常英语 → 简中并停用新增运行时。原游戏文件保留结果与限制见真机记录。

2026-10-24 东方 Project 自研引擎静态翻译适配器 v1 落地：th10/th11（THA1）解包→thmsg 提取（cp932，常规 16;/17;、结局 3;，按 stderr "left over" 判定格式）→批量翻译（ja 源、PackStore 每游戏缓存）→回封→重打包→`.frorig` 备份与可逆停用/恢复，全程临时工作区、失败不留半成品；Touhou 引擎接入现有启用/停用/修复/语言应用门槛（`IsInstallable`），th06 PKGL 明确拒绝。thtk BSD 二进制随附 ThirdParty/thtk/ 并补第三方声明。烟测 `--probe-touhou-static`（临时副本、翻译打桩）与 library 96 项、blueprint、architecture 全过；真实 th10/th11 目录只读判定 Supports/IsReady 正确。该静态方案后来因 cp932 编码与字形限制被 thcrap 路线替代；本段只记录早期探针，不代表当前支持结论。该阶段实验记录保留在内部归档。

2026-10-24 真机试用反馈修整：日志取证确认首轮"启用无效果"非编码问题——th11 真实启用完整跑过解包提取，卡在批量翻译（2804 条）数分钟且无界面反馈、失败只进日志。已加：结构化安装进度（权重 = 解包/写回/回封各占数据包字节、翻译按条数；绿色进度条 + 操作列最右侧 "50.43%（200MB/400MB）"，完成/失败即清除）、行内失败红色错误文本、安装中主按钮禁用并切换进度面板（去掉 DataGridCell 键盘焦点 AccentBrush 浅蓝描边，选中底色与展开行为保留）；翻译单端点超时 20s→10s。`--probe-touhou-static` 23 项（含进度断言）、library 96 项、blueprint、architecture 全过；标准 Release 构建并启动验证通过（途中修复 ProgressBar TwoWay 绑定撞只读属性的启动崩溃）。

2026-10-24 翻译阶段卡死修整：免费端点限流导致逐条重试走满死链。落地端点熔断（连续失败 3 次移除、全熔断提前结束并明确日志、状态记 Incomplete 支持同语言续译且缓存逐条增量）、LLM 主用端点 20 条/批合并请求（行数不符回退逐条）、并发 4→8、进度按 max(已开始,已完成) 口径即时跳动。真实旁证：地灵殿重试启用成功，前次 1692 条缓存全命中，仅 1112 条未缓存 2.5 分钟译完（1111 成功 1 条留原文），备份与状态就位。探针 31 项（新增批量/回退/熔断/续译断言）、library 96 项、blueprint、architecture 全过；构建与启动验证通过。

2026-10-24 进度权重修正（33% 假卡死）：翻译阶段按字节计权在 27.7MB 数据包上不可见（贡献约 0.036%），改为阶段等额权重（解包/翻译/写回回封/重打包备份各 25%），翻译段进度文本改 "（翻译 N/M 条）"。静态停用文案去 winhttp.dll 化（新增引擎中立 key）。探针 31 项、library 96 项、blueprint、architecture 全过；构建与启动验证通过；th10/th11 均已被真实启用，IsReady 断言改为磁盘一致性判定。

2026-10-24 卸载翻译能力：停用（可逆互换）/修复（针对性恢复）之外新增卸载（不识别问题、整体还原到未安装基线）。Touhou 从 .frorig 恢复并清理全部产物（逐步容错、部分损坏可恢复、缓存保留可秒装重装）；Unity 按安装清单逆操作（清单外文件不动、冲突保守跳过、清单缺失拒绝）。UI 行详情区卸载按钮 + 确认对话框。--probe-touhou-static 40 项；--probe-touhou-live-uninstall 真实往返 th10/th11 各一次 9/9 PASS（卸载还原字节一致、产物清空、同语言再启用、SourceHash 与测试前一致）；library 96 项、blueprint、architecture 全过；构建与启动验证通过。

2026-10-24 本地大模型功能移除：CPU 推理速度不满足游戏翻译，模型权重与 llama.cpp 引擎已从磁盘删除，本轮摘除代码与 UI——端点页"本地"分组与 4 张模型卡、LocalModelCatalog/LocalModelService、EndpointCategory.LocalLlm、AppTranslator/GameNameService/RelayServer/TouhouStaticService 的本地模型分支（超时统一回退）、--probe-local-model/--probe-download/--probe-mirror 探针、LICENSES 与第三方声明/鸣谢条目。设置残留 LocalQwen* 等未知端点 id 读取时回退 AutoFree（备用清空）。探针全过（navigation 63 项含用户 Kimi/OpenAI 断言、library 96 项、blueprint、architecture、--probe-touhou-static 40 项）；构建与启动验证通过。

2026-10-24 东方静态路线字形验证与渲染层立项：实机解包确认简体译文经 cp932 写回已损毁（非 JIS 字符大量变 `?`），且字形精灵图（text.anm）无简体字模，静态路线到不了可读中文；用户认可"运行时注入 + 系统字体栅格化"（thcrap 式）方向并指出多引擎复用价值（复用仅限栅格化/缓存半层，注入点仍逐引擎），立项与历史方案见 [lab/013](../lab/013-runtime-glyph-rendering.md)；随后已在 Franca 接入 thcrap 路线并完成下述真机验收。

2026-10-24 东方 thcrap 路线**真机验收通过**：地灵殿局内对话简中渲染正常，用户实际游玩确认可用。真机过程修复：补回被误裁的 bin/update.json（缺失即退出码 1）、真实 exe 备份名不以 .exe 结尾被 loader 当游戏 ID（改 games.js 映射 + 存根透传裸 ID）、HasArtifacts 通配误匹配 custom.exe 备份致停用谎报（收窄 thNN 前缀，汉化备份改名 .zhbak）、游戏库 RefreshRow 收起展开详情（保持选中）。探针修正为识别真实目录启用态。首轮仅验对话，后续已补菜单图片与结局等画面；Steam 版符卡名仍跳过；启动器窗口模式对话框经 stringdefs.js 翻译并真机确认中文，杀软可能误报注入。证据与修复清单见 [验收记录](../tests/2026-10-24-touhou-thcrap.md)，矩阵已更新。th10 已同法迁移（局内对话用户确认）；菜单图片性取证：th11.exe 无菜单字符串、title01.png 精灵图含全部菜单项贴图，证实需图像替换而非文本翻译。

2026-10-24 东方路线扩展：结局中文实测通过（instant_ending 辅助，e06 jdiff 命中+用户确认"博丽神社"）；音乐室评论接入管线（dat 内 musiccmt.txt 提取翻译，th11 17 首）；符卡名因 Steam 版 exe 被 SteamStub 加密、静态提取物理不可行而自动跳过（仅无 DRM exe 生效，版权与运行时转储另议）；主菜单中文精灵图程序化生成（thanm 矩形表切格 + 手工校译词表 + 本地字体渲染，零运行时成本，启用时管线生成）。修正记录：th10 曾经磁盘核实仍为旧静态补丁态，随后完成 thcrap 迁移及真机验收，当前以迁移后状态为准。探针 touhou-thcrap 52 项、touhou-static、architecture、library 96 项、navigation、startup、blueprint 全过；构建 0 警告。

2026-10-24 界面精灵图翻译管线化（用户要求多语言可用、禁 LLM 运行时）：新服务 TouhouMenuImageService——thanm 矩形表切格 + 端点管线翻译词条（PackStore 缓存、熔断续译一致；title01/select01 大标题按英语源、其余按日语源分批）+ WPF 本地成像（描边/双色变体/字号自收缩/扩散修补保留底纹）；zh-CN 内置约 205 条人工校译覆盖（含按游戏同文异译），其他语言全量走管线。覆盖 title01（主菜单）、rank00（难度）、select01（画面标题）、result00（结算）、weapon 与 weapon_00a-01c（支援选择/详情）、sl_pl00b/01b（人物介绍卡）、pause（暂停/录像菜单）、loading（加载）、front00（结算时间）、st01-07logo（关卡标题卡）等 th11 23 张 / th10 16 张；全量 ANM 盘点完成（HUD/品牌 logo/结局 staff 表建议不翻，其余纯图）。换语言重建自动换图，同语言幂等零重复翻译。探针 63 项全过（含像素级换图差异断言），构建 0 警告。旧补丁目录缺图会在下次启用自动重建补齐。th11 custom.exe 亦经资源级汉化（39 项断言，备份 .zhbak）。

2026-10-24 新增独立「支持引擎」页（导航顺序：游戏库/端点/设置/状态/支持引擎/日志/鸣谢）：目录驱动（EngineSupportCatalog），已支持（Unity IL2CPP/Mono、东方 th10/th11 thcrap 路线）在前并带备注（不记游戏名与日期，仅能力说明），其余引擎标"仅识别"；鸣谢目录补 thtk/thcrap/Noto Sans SC 三条。另：启动器窗口模式对话框经 stringdefs.js 翻译并真机确认中文（生成逻辑已进 TouhouThcrapService，简中手工校译，其他语言暂不生成）。导航 69 项、启动 26 项、blueprint、architecture、library 全过；构建 0 警告。

## 已具备的能力

| 领域 | 当前能力与证据 |
|---|---|
| 桌面与游戏库 | Steam 清单扫描、搜索/排序/图标、声明式引擎识别、官方语言证据和第二语言；源/目标/第二语言改为标签在上、选择器同排，进度条对齐前两项；六主题、固定蓝发的静态像素风 PNG 品牌图及纯头像任务栏图标、Agent 配置导入与状态页；状态页的高级与诊断现为直接展示。见 [面板](../tests/2026-10-23-panel.md)、[主题](../tests/2026-10-23-themes.md) |
| 翻译运行时 | 每游戏路由和包隔离、请求快照；高速/质量模式、词典后台升级、Franca.txt 同步；长文 2500 入口 / 200 请求上限。见 [架构](../tests/2026-10-22-architecture.md)、[长文](../tests/2026-10-22-long-text.md)、[重载](../tests/2026-10-23-reload-probe.md) |
| 安装恢复 | 本轮落地两包暂存、逐文件清单与备份、异常/取消回滚、中断恢复、冲突保护及每游戏安装锁；未完成事务不再误判就绪。见 [安装机制](INSTALLATION.md)、[本轮证据](../tests/2026-10-23-acceptance.md) |
| 悬浮窗 | 修复首次显示激活与穿透抓手不可达；屏幕外真实 WPF、原生标志、前台句柄不变和中继实时计数通过；游戏内叠加/交互/DPI 待验 |
| 真实游戏 | Suzerain 菜单/序章中文、BALL x PIT 与 Potion Craft 印尼语菜单闭环，以及东方 th10/th11 对话与所记录界面中文通过；逐版本及页面边界见 [矩阵](COMPATIBILITY.md)，未验长时间游玩 |
| 工程导航 | 工程文档移入 docs，补文档/测试入口、路线图和兼容矩阵；源码布局、历史证据、本机缓存保留；生成蓝图与源码边界对账通过 |

2026-10-23 对外表述整理：项目页、引擎页、README、界面来源/端点说明和历史设计已按实际实现修订，清除未实现服务的交付安排及无依据保证。范围与验证见 [文案核对记录](../tests/2026-10-23-claims.md)，不改变上述真实游戏未验状态。

2026-10-24 东方路线泛化（用户决策：红魔乡 PKGL 保持不支持，其余做兼容性配置）：启用门槛从仅 th10/th11 改为能力探测（thdat 能列目录 + 至少一个 msg 能 dump），对话指令码按 thcrap msg_format_for 分档表解析（th06-th19 全档），结局/音乐评论/符卡/精灵图无数据时自动跳过不阻断；th06 PKGL 仍在数据包定位处明确拒绝。其余正作可启用但**未经真机验收**（本机无对应游戏）。引擎页 Touhou 备注按用户口径改为"部分游戏菜单等为图片资源，支持可能存在兼容性问题"（15 语言）。探针新增 msgfmt_table 分档断言，touhou-thcrap/static/architecture/library/navigation/startup/blueprint 全过；构建 0 警告。

## 主线状态与下一步

2026-10-26 支持引擎页按用户要求单列 UE“实验适配”，15 语言明确两款部分菜单的实际范围、八方完整包未完成、格式与未接入能力；说明网页同步。安装能力门槛保持不变。独立 Release 构建、启动与导航检查及页面图稿核对通过；当前运行程序占用标准输出，标准目录待关闭后重建。见 [范围标注记录](../tests/2026-10-26-engine-scope.md)。

2026-10-26 当日收尾：逸剑两张日语表已修复保护标记缓存问题，用户确认停用恢复官方中文、重新启用日语并操作正常。八方数据表已接现有端点/缓存批译，用户授权 Google/必应；收尾时停止本会话批译，保存 4,903 条有效片段缓存，完整无标记译文包尚未完成，游戏仍保留旧标记验证包。下次从缓存续译，再完成换包显示与恢复验收；本轮不再启动游戏。未来自动流程见 [UE 路线图](UE-ADAPTATION-ROADMAP.md)，数量和状态分别见两款日期证据。

2026-10-26 第二款 UE 验证靶子为本机《歧路旅人 2》：pak 读取复用成功，游戏文本为自定义 DataTable，不能直接使用现有 locres 准备流程。已在本地制作少量带 Franca 标记的日语菜单验证包，保留原包、单独新增覆盖并启动，用户已确认看到日语和 Franca 标记；后续端点批译已有收尾缓存，完整译文部署与停用恢复尚待验收；尚未接入正式启用。格式、数量及恢复证据见 [本轮记录](../tests/2026-10-26-octopath2.md)。

2026-10-27 歧路旅人 2 全表正常批译完成：10-26 的 4,903 条片段缓存与快照已确认丢失（本机不入库内容随临时输出清理），经用户授权全量重译。首轮约 31 分钟译完 12,607/12,618 片段（缺 1 条，Incomplete 拒绝发布），同命令续跑补译最后 1 条后完整：19,062 行英文表 11,150 条去重文本全译（11,108 机翻 + 42 纯字面），Missing=0，准备包 SHA256 `1EDC9C2F…A0B67` 在游戏目录外产出，原 pak 未动。缓存快照与 manifest 证据见 [批译记录](../tests/2026-10-27-octopath2-batch.md)。真机换包、日语显示与停用/恢复验收仍待用户在场执行，未接入正式启用。

2026-10-30 歧路旅人 2 日语包真机验证：部署后直连启动一次约 30 秒退出（生成崩溃报告但无 WER 事件，根因未定），二次启动用户确认**日语真实显示**，全表机翻被游戏正常加载。用户截图核对确认：菜单 `???` 是英文源即为 `???` 的正常占位符透传（17 行，官方简中同为 `？？？`），非失败填充；**同时确认简中槽位字体缺日文字形**（ー/続/設 不渲染），根因为 `KS_*` 子集 UI 字体不含日文字形，pak 内 `.ufont` 实为裸 TTF、另有 3.9MB DroidSansFallback 全覆盖回退字体。已产出占位符修复包（`???`→`？？？`，未部署），游戏目录已恢复官方基线。用户定调：字体支持属翻译软件自有责任，不走官方 JA 槽位捷径；字体方案候选为改 Font 资产回退链（B）或同路径换 OFL 完整 TTF（A），属 UE 路线图阶段三，下轮开工。详见 [真机验证记录](../tests/2026-10-30-octopath2-live.md)。

2026-11-02 字体专项第一轮落地：破解本作字体结构——`.ufont` 为裸 TTF，UI 复合字体按语言组合且共享回退 `MJ_KR_YoonGothicPro770`；cmap 覆盖矩阵证实缺字分布（简中字体缺 ー設続〜、共享回退缺 続〜）。实现**嫁接方案**（原字体优先、Noto Sans JP 补缺；fontTools Merger 因 VarStore/upem 不支持，改自研逐码点嫁接并做 1.024 坐标缩放），8 款地区字体全部通过 FreeType 验证；构建"译文+字体"覆盖包（40.9MB，SHA-256 `94275317…`，往返校验通过）与纯字体对照包。真机对照被当日环境不稳定性阻塞：无覆盖包基线直连启动 4/4 退出码 1（与 BALL x PIT 未解退出码 1 同特征）、Steam 间歇成功，带字体包的表现与基线失败不可区分，**不得出字体包致崩结论**；游戏目录已恢复基线，待安静环境重跑三级对照。详见 [字体嫁接记录](../tests/2026-11-02-octopath2-fonts.md)。

2026-10-25 接续 UE 静态适配：逸剑风云决首批核心表译文缓存与游戏外覆盖包已准备，支持取消、熔断续译及目标独有条目保留。独立 Release 构建、相关探针、UnrealPak 校验、独立 locres 解析和零网络纯缓存重建通过。用户随后授权手工放包启动：首轮压缩包在 UE4.26.2 中解压失败，已回退并改不压缩；修正版日志达到地图 play 并正常退出。后按用户选择完成日语菜单/系统验证包并换入重启，译文语言与简中槽位分离、缓存隔离，用户已确认设置面板显示日文；随后定位主菜单为图片按钮，另制四项日语按钮覆盖包并启动，用户已确认主菜单日语显示成功。任务/CG 表尚未翻译。数量、产物位置与未验边界统一见 [本轮记录](../tests/2026-10-25-ue-static.md)。

2026-10-25 修复官方支持刷新先清空标记、译名被网络补查阻塞的问题：缓存译名/证据先显示，官方支持独立查询，失败保留上次证据；新响应仍可增加或撤销支持。游戏库 117 项与导航探针通过。实际扫描 61 项、可见 57 款，未复现整张列表空白，见 [刷新记录](../tests/2026-10-25-library-refresh.md)。

2026-10-24 大模型端点增加醒目的「停用」入口，避免自动翻译落入计费模型：DeepSeek 面板按钮调整为 保存/停用/主用/验证/删除/平台，大模型卡片为 停用/主用/验证/备用，其余分类顺序不变；停用沿用既有 DisabledEndpoints 机制（排除自动测速候选、不可设主备，停用当前主用自动回落必应），简/繁中文按钮文案由「禁用」改为「停用」。Release 构建与端点配置 24 项探针通过，界面实机截图待人工核对。

2026-10-24 大模型区在 DeepSeek 面板下方新增 Kimi、OpenAI 两个同款专属面板（保存/停用/主用/验证/删除/平台，共用泛化的面板处理逻辑）：Kimi 走 `api.moonshot.cn/v1` 预设（默认 moonshot-v1-8k），OpenAI 为新增 `LlmOpenAI` 预设（`api.openai.com/v1`，默认 gpt-4o-mini，翻译路由/自动测速主机表/15 语言文案已接入）；已配置的存量 Kimi 凭证与主备选择自动落到新面板，不丢入口。导航 63 项（含面板保存→选用→删除回落检查）、端点配置 24 项、架构 51 项探针通过。真实 Key 是否可用取决于账户余额与订阅额度，协议连通性可用 `--probe-translate` 复验。

2026-10-24 平台按钮修正与 Kimi 双平台：三个面板的「平台」按钮原复用 DeepSeek 专属文案 `ui.platform`，现 DeepSeek 保持 `ui.platform`，Kimi 拆为国内/国际两个按钮（`platform.kimi.cn/intl`，15 语言新增键），OpenAI 用 `platform.openai`；Kimi Key 兼容性自动判断——对 `api.moonshot.cn` 返回 401/403 时自动改试 `api.moonshot.ai`，用户无需区分国内外 Key。

2026-10-24 新增东方 Project 自研引擎（ZUN/thdat 系）识别：`GameEngine.Touhou` 及三条互证文件签名（thbgm.dat `ZWAV` 头、th06 Steam 系 `PKGL` 容器头、th095+ `THA1` 解密头，算法移植自 BSD 许可的 thtk）。游戏由现有 Steam 清单扫描在本机发现（th06c/th06nc/th10/th11 官方版），全库 56 目录扫描零误报；初期仅识别；后续已接入 thcrap 翻译路线，当前范围以上方真机记录和兼容矩阵为准，红魔乡 PKGL 仍不支持。证据与边界见内部实验归档，library 96 项、blueprint、architecture 探针通过。

2026-10-24 按用户反馈实际重开 Suzerain，高速 / DeepSeek / 英语 → 简中，序章正文及选项中文画面与新生成的模型缓存对照成功，用户确认恢复正常。未复现上轮持续英文，原因尚未确定；内置词典覆盖常见界面完整文本，新剧情仍依赖在线翻译。见 [剧情实测](../tests/2026-10-24-suzerain-story.md)。

2026-10-24 修复大模型端点「主用」按钮及 DeepSeek 面板边框的选中高亮，切换主用即时取消旧高亮；六主题与切换路由验证通过。导航 59 项、端点配置 24 项通过，用户授权重启后标准 Release 已更新并重开，见 [端点主用记录](../tests/2026-10-24-endpoint-primary.md)。

2026-10-24 修复本机根目录快捷方式仍指向旧检出目录导致的图标/启动失效；标准 Windows Release 构建现自动刷新当前目录的快捷方式，自定义输出不改写。已通过修复后的快捷方式实际打开程序并确认窗口正常，见 [入口修复记录](../tests/2026-10-24-shortcut.md)。

2026-10-23 不支持启用翻译的游戏行在「操作」列直接显示「暂未支持」，包括状态列已有官方语言信息的条目；支持的游戏保留原操作按钮。构建、游戏库探针及行显示截图核对通过，见 [行操作提示记录](../tests/2026-10-23-unsupported-row.md)。

2026-10-23 根目录新增带品牌图标的「Franca」快捷方式，指向标准 Release 程序；网页归入 `docs/site/`，更新日志和第三方声明归入 `docs/`，同步修正导航和构建复制路径。见 [启动入口与目录整理记录](../tests/2026-10-23-root-launcher.md)。

2026-10-23 游戏库「隐藏非游戏软件」默认开启，现按本地 Steam appinfo 类型优先筛选，软件名称作为回退；无清单的 Glacier Events 按仅含 EA 反作弊启动器的目录特征隐藏，真实游戏捆绑反作弊程序仍保留。Game/Demo/Mod 优先保留，未知条目无明确软件特征时保留；取消勾选即时恢复，搜索、数量和最近启动入口同步。标准 Release 输出及类型解析/控件点击探针的结果见 [验证记录](../tests/2026-10-23-software-filter.md)。

| 主线 | 当前状态 | 下一步完成条件 |
|---|---|---|
| 可恢复安装事务 | 合成故障、两游戏磁盘检查及本机首次安装后的真实菜单闭环通过 | Unity 与东方已新增卸载；继续验证冲突、部分损坏和中断恢复边界，断电持久性待验 |
| 配置诊断与修复 | 两游戏目标语言注错、检出、备份修复与重启印尼语通过；5 秒语言应用流程见 [既有验证](../tests/2026-10-23-language-apply.md) | 继续补足批量语言应用的完整 UI 真机链路；不把直接服务调用当作全部按钮交互验收 |
| 支持矩阵与真实验收 | IL2CPP / Mono 两款游戏的真实菜单闭环已完成，见 [证据](../tests/2026-10-23-live-games.md) | 长时间游玩由用户手测；自动侧继续定位 BALL x PIT 两次退出码 1，补短时画面/交互及默认空缓存真机重验 |
| 悬浮窗交互 | 既有无输入检查通过；本轮完整应用自动启动等待超时，未取得有效叠加证据 | 仍需游戏内叠加、鼠标穿透、键盘不吞键、抓手拖动、真实位置记忆及多 DPI |
| 原生 Loader | 本机 16 项隔离用例通过；BALL x PIT 真机启动、印尼语、新文本请求、配置停用/恢复及重启缓存复用通过；恢复上游代理后仍维持实验状态 | 扩大启动时序/冷启动、异常场景与不同系统版本覆盖后再评估正式化；不支持 Mono/x86，默认安装器继续用上游 |
| Godot / UE | UE 两款游戏的少量菜单日语显示已获用户确认，尚未接正式启用；Godot 暂缓 | UE 格式与准备验证→授权端点缓存→用户在场部署/画面对照与还原；Godot 后续逐款闭环，详见路线图 |
| 社区翻译包 / 全局 Auto | 规划，暂不实施 | 先完成上述游戏闭环；历史规划见 [优化](OPTIMIZATIONS.md) 与历史快照 |

## 验收原则

构建、磁盘健康、模拟 HTTP 和缓存文件都不能替代真实游戏画面。识别引擎不等于支持注入翻译。所有游戏目录变更先确认目标并保留可恢复证据；已有游戏不批量覆盖配置。测试定义见 [测试计划](../tests/test-plan.md)。
