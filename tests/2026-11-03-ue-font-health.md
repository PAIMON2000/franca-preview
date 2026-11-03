# UE 字体健康检查组件落地（2026-11-03）

UE 路线图阶段三的"静态检查译文所需字符、实际字体资源缺字集合"在应用内落地：新增 `UeFontHealthService`（只读）+ 最小 TTF 解析器 `TtfFont` + 合成夹具探针 `--probe-ue-fonts` + 真实检查命令 `--ue-font-check`。

## 组件

- `src/Franca/Services/UePak/TtfFont.cs`：最小 TrueType 读取器（sfnt 大端）。校验 1.0/`true` 魔数与表边界；读 head.unitsPerEm、maxp.numGlyphs、OS/2.usWeightClass；解析 cmap（优先 format 12，否则 format 4，platform 0/3），输出码点集合。
- `src/Franca/Services/UeFontHealthService.cs`：`Parse` 得字形数/码点数/upem/字重/假名数/汉字数；`Analyze(faces, texts)` 对**文本可用**字体（含假名或 CJK）报告缺失码点集合与样例，符号字体（无假名且无 CJK，如图标/数字字体）只分类不告警；另报告"所有文本字体都缺"的未覆盖样例。
- `--probe-ue-fonts`：内存合成最小 TTF（format 4/12、非法夹具）15 项断言，不触真实字体/游戏/网络。
- `--ue-font-check <fontsDir> <textFile>...`：只读检查命令，JSON 输出，有缺字时退出码 2。

## 真实数据交叉验证（本机证据，tools/ 不入库）

- 原始 25 款游戏字体 + 全部译文（2,209 去重字符）：10 款文本可用、15 款符号字体（KS_Meldir/Skech 8 款 + MJicon/pc/ps4/ps5/switch——**无假名无 CJK，与 Python 侧结论一致：它们只承接拉丁/数字/符号，官方任何语言都不会把日文文本路由到它们，嫁接需求闭环**）；简中三款缺 685–687 字符（含 〜ー 及日文汉字），与 Python cmap 矩阵吻合。
- 8 款嫁接后字体复检：Issues=0、未覆盖为空、退出码 0，与 Python 嫁接校验一致。
- Noto Sans JP 可变字体权威源哈希补齐：GitHub google/fonts raw（经本机代理）与 jsDelivr 镜像 SHA-256 一致（`C2F3B4D4…50FC68F`），镜像分发"哈希为准"规则达成。

## 构建与探针

Release 构建 0 错误；`--probe-ue-fonts` 15 项、`--probe-ue-static`（含 datatable）44 项、`--probe-blueprint` 14 项、`--probe-architecture` 51 项全过。

## Font 资产引用链解析（同日追加）

- 新增 `UeCookedPackageRefs`： cooked .uasset 导入表读取器（布局经本作验证：nameCount/nameOffset@41/45、importCount/importOffset@65/69、name 表 = FString+4 字节、FObjectImport 28 字节）。Font 复合资产通过 FontFace 导入项引用子字体，**导入顺序即回退链顺序**（默认字体在前）。
- 新命令 `--ue-font-refs <pak>`：列出 pak 内全部 Font 复合资产及有序 FontFace 引用 + 包路径配对（JSON，只读）。
- 真实数据（本作原 pak）：112 个 Font 复合资产，**80 个引用 `MJ_KR_YoonGothicPro770`**（共享回退实证），`KS_NewCinema_Std_D` 64 次、`KS_Meldir_Default` 49 次；31 个引擎内置字体（RobotoRegular 等）的 `Default__FontFace` 无包路径配对，属正常。
- 探针新增 4 项合成 cooked 包断言（`--probe-ue-fonts` 现 19 项）：有序性、路径配对、非法魔数/表界拒绝。

## 复合字体链级健康报告（同日追加，阶段三静态检查闭环）

- 回退链并集语义：UE 复合字体是一条 fallback 链（`--ue-font-refs` 的导入顺序），**链上任一字体覆盖即算覆盖**。新增 `UeFontHealthService.AnalyzeChain` + `NeededCodepoints`。
- 新命令 `--ue-font-report <pak> <textFile>... [--facesDir <dir>]`：pak 内解析每个 Font 资产的链 → FontFace 名 → 同 pak `.ufont`（`--facesDir` 的嫁接字体优先，支持验证修复后状态）→ 链级缺字 JSON 报告；有缺字退出码 2；链上字体在 pak 和目录都找不到的（引擎内置 Roboto 等）记 Note 跳过不计失败。
- **本作实证（原 pak + 全部译文 2,209 字符）**：81 条链、31 条引擎链跳过、**仅 3 条不健康**——
  - `FONT_KS_Skech`：缺 303 日文汉字。链 = 6 款 Skech 符号字体 + YoonGothicPro770，Yoon 的韩系 4,620 汉字盖不住日文用字。**官方日文能正常显示 ⇒ 证明 CJK 文本从不路由到此链**（装饰性拉丁字体），与"符号字体不告警"结论互证。
  - `FONT_MJ_TW_YuanMeldir_Switch`：缺 178（Yoon + 台系字体的日文汉字缺口）。
  - `Roboto`：缺 4（㌀㌁㌂㌄）——译文中仅 2 条英文源即含此串（`Gate of ㌀㌁㌂㌃㌄`，开发测试行），非玩家内容。
- **嫁接修复链级验证**：`--facesDir` 指向 8 款重嫁接字体后复检，81 条链仅 Roboto 测试串仍缺（3→1），部署修复在链级层面闭环。
- 局限：链覆盖 ≠ 文本路由。哪些字符串实际走哪个字体由 `TalkFontTable_*.uasset`（Font/Database 下按语言的 DataTable）决定，其 schema 非 Id/Text 对，现有 DataTable 解析器不适用——路由表解析是下一步。

## TalkFontTable 路由级闭环（同日追加）

- `--ue-font-routes <pak>`：对 pak 内每个 `*FontTable*` 资产扫描 cooked name 表中的 `/Game/.../FONT_*` 引用，按语言给出对话/气泡字体的路由（行级 .uexp 属性解码的低成本近似；name 表收录了行数据引用的全部字体路径）。
- **本作五语言路由实证**：EN→`FONT_KS_Skech_PC`、JA→`FONT_KS_NewCinema_PC`、KR→`FONT_KR_YDHopeL_PC`、**ZH_CN→`FONT_MJ_CN_WeiBei_PC`**（本部署活动槽）、ZH_TW→`FONT_MJ_TW_FangSong_PC`。
- **关键结论**：ZH_CN 对话链 = NewCinema（默认，完整日文字体，0 缺字）→ 魏碑 → Yoon → 图标字体。**即使不嫁接，对话气泡也零缺字**——默认字体全覆盖；嫁接后链上魏碑/Yoon 也达到 0 缺字（与原字体观感一致）。此前标题菜单缺字是 UI 部件字体（不在 TalkFontTable 管辖），两处证据合拢。
- 路由级风险评估闭环：需要承接日文的链（ZH_CN 对话、各 UI 复合字体）在嫁接后全部 0 缺字；不承担日文的链（Skech 等）官方本来就不路由 CJK。
- `UeCookedPackageRefs` 重构出公开 `ReadNames`（name 表读取复用）。

## 边界与下一步

- 嫁接（glyf 手术 + 字重匹配）仍由本机 Python/fontTools 工具链执行，尚未移植进应用；产品化时决策"应用内嵌字体处理"还是"准备期工具产出字体、应用只做检查与注入"。
- `--ue-font-check` 目前按字体文件粒度报告，尚不解析 .uasset Font 资产的引用链（哪个界面用哪个复合字体）；解析 Font 资产回退链属阶段三后续。
- UE 安装/停用/恢复事务尚未接入字体产物（阶段四）。
