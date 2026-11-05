# Ren'Py 静态翻译适配器（实验设计）

立项日期：2026-11-05。动机见 [路线图](../docs/ROADMAP.md) 排队项①（明文引擎静态支持优先于 IoStore）。

## 为什么 Ren'Py 是明文引擎里最干净的静态目标

- 官方翻译框架就是"文件即配置"：游戏加载 `game/tl/<语言名>/*.rpy`，自动注册该语言，**不需要改任何原文件**——安装=放 tl 目录，卸载=删 tl 目录。
- 无需反编译回 rpy 源码：不需要完整反编译，只需要从 rpyc 提取「可译字符串 + 块标识」。

## rpyc 格式（依据 unrpyc v2.0.3，github.com/CensoredUsername/unrpyc）

- v2：文件头 magic `RENPY RPC2`（10 字节），随后 uint32 三元组槽位表 `(slot, start, length)`，直到 slot==0；slot 1 即目标 blob。
- v1（RenPy ≤6 老格式）：整个文件就是 blob。
- blob = zlib 压缩的 **pickle**（不是 marshal），loads 结果为 `(data, stmts)`；AST 节点为 `renpy.ast` 等模块类的 pickle 对象（GLOBAL opcode + BUILD 状态字典）。
- C# 侧实现：通用 pickle 解析器（协议 2/3/4 常用 opcode 子集：INT/LONG/BININT*、STRING/BINUNICODE、TUPLE/LIST/DICT 系列、OBJ/NEWOBJ、GLOBAL、BUILD、REDUCE 处理 PyExpr 等），未知 GLOBAL 收为 FakeClass(className, stateDict)。PyExpr 通常 REDUCE 自构造器，取字符串值即可。
- RenPy 6（Python 2）pickle 里字符串是 bytes 需按 utf-8/escape 解码；pickle_detect_python2 类检测。DDLC 即 RenPy 6.99 系，可覆盖旧格式。

## 翻译块标识

- RenPy `translate <语言> <块id>:` 的块 id 由 AST 节点位置派生（脚本文件名去扩展 + 行号）。**具体拼法以 DDLC 自带官方 tl 文件为 ground truth**（DDLC 带多语言 tl），不从记忆推断。
- 字符串（screen 文本）走 `translate <语言> strings:` 段的 `old "..."` / `new "..."` 对（无块 id）。

## 路线（2026-11-05 按 DDLC 6.99.12.4 实地修订）

**关键简化：v1 不走 translate 块，只生成 `translate <语言> strings:` 块。** 依据：
- 官方文档明确「String translations are also applied to dialogue strings that are not translated as dialogue」——无 translate 块时对话运行时回退字符串翻译，即官方钦定的 unsanctioned 翻译路径（RENPY_UPDATE_STRINGS 产物同款）。
- 免除块标识复刻（label 游标语义、md5(get_code+"\r\n")、_1 去重后缀全部不需要），且对 RenPy 6/7/8 全版本通用。
- 保留 [插值] 与 {#tag}；同名文本自动去重。损失：不能拆句/调序，同名异义文本会合并——v1 可接受。
- 强制语言：tl/<语言>/ 下的 rpy 是常规脚本（init 阶段加载执行），放 `init python: config.language = "<语言>"` 即默认进译文（DDLC 无语言菜单）。卸载即还原。
- 字体：**必须**用 `translate <语言> style default: font "tl/<语言>/xxx.ttf"` 块——真机实证 6.99.12 上 `translate python: style.default.font=...` 不生效（中文零宽不渲染，只剩 ASCII 尾巴）；随包装 ttf（Noto Sans SC）。

**块标识算法仍在文档中存档（本文件保留原路线描述），未来需要 translate 块时实现**：digest = md5(逐语句 get_code().encode utf-8 + b"\r\n") 取 hex[:8]；id = label.replace('.','_')+'_'+digest，冲突加 _1…（6.99.12 实地核对：无 preexisting/alternate/tlid_only_considers_say；Label 游标是全局可变状态，restructure 先递归嵌套块再分组）。

## 实施步骤

1. RPA 读取器（RPA-3.0/2.0 头 + zlib pickle 索引）→ 抽出 scripts.rpa 内 rpyc。
2. 通用 Python pickle 解析器（协议 2 为主，覆盖 0-5 常用 opcode）→ FakeClass 对象图。
3. rpyc v1/v2 解包（v2: RENPY RPC2 + 槽位表，见上）→ AST 遍历收集 Say.what / Menu 选项文本（去重）。
4. 走现有翻译管线（PackStore 每游戏缓存、批量、熔断）→ 生成单个 strings.rpy + main.rpy + ttf，目录外准备。
5. 事务安装/卸载/修复（tl 目录放置/移除，原文件零写入），CLI 先行。
6. 真机验收：DDLC（Steam 698780，免费）窗口化静音启动截图。

## 风险与未知

- DDLC 无语言菜单→强制 config.language 是唯一自动路径（用户无法在游戏内切回原文，卸载即可还原）。
- 单文件超大 strings 块（DDLC 全对话约万级）RenPy 解析耗时待真机观察。
- RenPy 8（py3 pickle 协议 4/5）解析器需支持但无样本验收。
- DDLC 的 meta/反篡改设计对 tl 目录的态度：模组生态普遍使用此路径，风险低但未实证。
- 字体：RenPy 默认字体无中文字形，style.default.font 覆盖是否覆盖全部衍生样式待真机确认。

## 实证记录（2026-11-05，DDLC 真包验证）

- DDLC（RenPy 6.99.12.4 / Python 2.7）随包带完整 `renpy/` 引擎源码——块标识算法与 encode_say_string 的权威 ground truth 就在游戏目录里，无需外查。
- **AST 节点 pickle 形态**：`NEWOBJ(GLOBAL, ())` + `BUILD`，state 是 `TUPLE2(None, {attrs})` 二元组（py3 unpickler 会因 "state is not a dictionary" 拒绝，C# 侧需解包）。属性名是 py2 str、对话文本是 BINUNICODE（PyExpr 走 `__getnewargs__` = (text, filename, line)，REDUCE 收编为字符串即可）。
- 踩过的 opcode 坑：NEWTRUE=0x88 / NEWFALSE=0x89（proto 2 bool 常态）；py2 大整数走 LONG 文本 opcode 变 Int64。
- RPA-3.0 索引值是**段列表**（2 元组或 3 元组带 start 前缀，可多段拼接），offset/dlen 与 key 异或。
- 提取实绩：scripts.rpa 35 个 rpyc 全解析 0 错误；10821 条 Say → **8378 条去重文本**（对话 8350 + 菜单 16 + 混合 10），含 `[player]` 插值。
- 新增 CLI：`--renpy-extract <gameDir> [--out <txt>] [--json]`（只读盘点 + 节点类型直方图）。tools/ 下证据：rpx.json、ddlc-strings.txt（不进库）。

## 验收记录（2026-11-05 真机闭环）

- 准备二轮：8376/8376 全译、Missing 0（界面串补充提取后重跑，一轮缓存 8354 复用）。
- 事务安装：游戏脚本现场重扫 SourceStringsSha256 与清单比对（防游戏更新漂移）→ State=installed。
- 真机（窗口化 1280x720）：开场免责声明三页（不适宜儿童/焦虑抑郁警告/13 岁同意）全部中文渲染正常。自动化截图走 PrintWindow(flags=2) 可行，无黑屏。
- **字体坑**：`translate python: style.default.font` 在 6.99.12 不生效（症状：中文零宽、整段只剩 URL 等 ASCII）；改 `translate <语言> style default:` 块后正常。生成器已修（RenPyStaticService 的 franca.rpy 模板双保险）。
- **三层字体覆盖**（同日补，真机抓回严重回归后定型）：①`translate <语言> style default:` 块；②`init 9999 python` 遍历 `renpy.style.styles` 全样式强制 font；③**patch `renpy.text.text.Text.__init__` 强制 kwargs["font"]**——单选菜单按钮（"我同意。"）的样式是运行时延迟创建的，②遍历盖不到；字体缺 CJK 时文本零宽 → 按钮热区塌缩 → 整页卡死无法点击/按键推进。三层齐备后主菜单侧栏 5 钮从 tofu（口口口）变正常中文。
- **屏幕（screen）串提取**（同日落地）：运行时路径查明——screen 文本统一走 `renpy.substitutions.substitute()` → `translate_string()`（引擎源码 `renpy/substitutions.py`，`translate=True` 默认，Input 例外 `substitute=False`），所以 strings 块对 screen 文本天然生效。提取器新增 SLDisplayable 处理：`renpy.text.text.Text`/`renpy.ui._textbutton`/`renpy.ui._label` 取 positional[0] 表达式——显式 `_('...')` 调用和裸字面量都提取，裸字面量含 `[`（插值）跳过。DDLC 实测多收 40 条（History/Skip/Auto/New Game/Load Game/Settings/Return/Yes/No 等）。
- **引擎内置串补偿**：`empty slot` 等 UI 串编译在 `renpy/common`（随引擎发布，不在任何 game .rpyc），扫描器永远看不到。生成器内嵌固定映射（`EngineBuiltinStringsZh`）在写 strings.rpy 时补 `old/new` 对（当前仅 zh-CN 一条，可随语言扩充；与提取结果去重）。真机 Load 页 9 槽全显"空槽位"。
- **重装崩溃坑**：RenPy 首启会把 tl/<语言>/*.rpy 编译成同名 .rpyc；下次安装只覆盖 .rpy 时，旧 .rpyc 与新 .rpy 被同时加载 → `A translation for ... already exists` 启动即崩。修复：安装提交后清除所装 .rpy 的过期 .rpyc。
- **安装根目录坑**：`--renpy-install/--renpy-uninstall` 的 journal 路径以安装根为基（`game/tl/...`），误传内层 `game` 目录会把产物装进 `game/game/tl`；且 Scan 的 `<gameDir>/game` 优先探测会坠入该 stray 目录，之后所有扫描报 "No compiled scripts"。CLI 现归一化（认 `renpy/` sibling 特征，内层 game 自动上溯父级）。
- **卸载坑**：安装事务日志漏盖 `Engine="renpy"`，RenPyInstallService.ReadJournals 过滤不到自己写的记录 → 卸载恒 not-installed、IsInstalled 同病。修复：安装时补盖章 + 旧记录按 `game/tl/` 路径前缀兼容识别。卸载往返 rolled-back、tl 目录移除验证通过；手改文件被正确判 Rollback conflict。
- **.rpyc 残留坑（安装/卸载双侧）**：Ren'Py 首启把 tl/*.rpy 编译成同名 .rpyc，而这些 .rpyc 不在安装 journal 里——安装侧重装时旧 .rpyc 与新 .rpy 同载报 "translation already exists" 启动崩；卸载侧回滚只删 .rpy，.rpyc 残留会被下次启动继续加载。修复：安装提交后与卸载回滚时均连同删除所涉 .rpy 的 .rpyc。
- **GUI 接入（同日）**：游戏库 RenPy 行与 UE 同模式——主按钮未装「启用翻译」（选择 `--renpy-prepare` 产物目录的 manifest.json）/已装「启动游戏」，详情区「启用翻译/卸载翻译」按钮组，状态读事务日志；`GameTranslationService.RenPyInstallAsync/RenPyUninstallAsync` 复用游戏停止检查与操作串行化。真实卸载走通（确认对话框 → rolled-back → tl 移除）。
- 真机验收（同日第二轮）：免责声明三页中文、"我同意。"按钮中文渲染且可点击（三层字体修复前为空盒+整页卡死）、主菜单侧栏 5 钮全中文、Load 页标题"加载"/侧栏/9 空槽全中文。已知边界：主菜单按钮是图片贴图（无文本可译）；`Page {}` 页码（带插值的引擎内置串）仍英文；RenPy 8 无真机样本。
- 合成探针 `--probe-renpy` 14/14 PASS（proto2 手工夹具 + 5 项 screen_* + scan_skips_tl_and_cache）。
- CLI 三命令：`--renpy-prepare <gameDir> <appId> <outputDir> <settings.json> <packsDir> <seconds> [--language=]`、`--renpy-install <gameDir> <preparedDir>`、`--renpy-uninstall <gameDir>`；另有只读 `--renpy-extract`。

## 待办（后续增强）

1. RenPy 8（py3 pickle 协议 4/5）真机样本验收（解析器已支持，无样本）。
2. 引擎内置串扩充（`Page {}` 等带插值串需拆固定部分单独映射）。
3. 长时间游玩稳定性（ poem 小游戏等动态文本路径）留用户手测。
4. RenPy 准备流程 GUI 化（当前与 UE 同口径：准备 CLI、安装 GUI）。
