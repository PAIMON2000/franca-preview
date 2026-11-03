# 歧路旅人 2 字体嫁接方案与覆盖包构建（2026-11-02）

UE 路线图阶段三字体专项第一轮：破解字体资产结构、实现"原字体优先、OFL 字体补缺"的嫁接方案并构建覆盖包。**真机显示验收被当日环境不稳定性阻塞**（基线同样无法启动，见下），字体与包体均通过离线验证。后续：真机验收于 2026-11-03 完成，发现并修复字重不匹配问题，详见文末"真机验收与字重匹配"。

## 字体资产结构（本作实证）

- 本作 `.ufont` 为**裸 TTF**（偏移 0 处 `00010000` 魔数）；`.uasset/.uexp` 为对应 FontFace/Font 资产。
- UI 字体为复合 Font 资产：`FONT_KS_*`/`FONT_MJ_*_PC` 按语言组合（简中=魏碑/雅黑/圆体 + `KS_NewCinema_Std_D`，日/韩/繁各有组合），**所有 PC 复合字体共享回退字体 `MJ_KR_YoonGothicPro770`**。
- cmap 覆盖矩阵（关键字符 ー/続/設/〜）：简中三款字体缺 ー設続〜；共享回退 Yoon 缺 続〜；繁体缺 ー続〜；`KS_Meldir/Skech` 系列为 250–318 字形的符号子集（无 CJK）；`KS_NewCinema_Std_D`（6MB）与 `NewRodinProN-B` 为完整日文字体。真机缺字（ー/続/設）与矩阵吻合。

## 嫁接方案（原字体观感保留 + 缺字补齐）

- 供体：Noto Sans JP（OFL，Google Fonts 官方源；GitHub raw 超时，经 jsDelivr 镜像取得，SHA-256 `C2F3B4D4…FC68F`，OFL 文本存档于证据目录）。
- 按"全部译文实际用字 + 假名/全角/标点安全块"（2,545 字符）子集化 Noto（2,525 字形，约 1MB）。
- 游戏字体 upem=1024、Noto upem=1000：fontTools Merger 因 VarStore 不支持合并且 upem 不同直接失败；改用**自研嫁接**（graft-fonts.py）：逐码点把 Noto 字形坐标与步进宽度按 1.024 缩放后追加进游戏字体（原字形与度量零改动），同步 cmap/glyphOrder/hmtx/maxp。
- 8 款地区字体全部嫁接成功（各补 364–874 字形），FreeType（与 UE 同引擎）逐款验证： face 加载、嫁接字形渲染、`ー続設〜新ゲム` 全覆盖。

## 产物（本机，未部署）

- `pakchunk99-fontfix-1030.pak`（40.9MB，SHA-256 `94275317…90D3B`）：`???`→`？？？` 修复版译文表 + 8 款嫁接字体，10 项，pak 逐文件往返校验通过。证据副本存 `tests/artifacts/ue-octopath2/`。
- `pakchunk99-fontsonly-1102.pak`（37.5MB，SHA-256 `ACC16019…6C2D`）：仅 8 款字体（对照实验用）。
- 嫁接工具链与字体源存 `tools/ue-prepared-octopath2/fonts-src/`（本机不入库）。

## 真机验证受阻（环境不稳定性，非字体结论）

- 对照实验：无覆盖包基线下，直连启动 4/4 **退出码 1 + 崩溃报告**（与 BALL x PIT 未解的退出码 1 同特征）；Steam 启动间歇成功（2 次达标题），强制结束后出现静默不启动。带字体包经 Steam 启动 2 次均崩溃的表现**与基线直连失败模式不可区分**。
- 结论边界：不能据此认定字体包导致崩溃——基线同样无法稳定启动；字体本身已过 FreeType 验证。需在安静环境（关闭覆盖层/模拟器类应用或重启后）重试对照：基线 → fonts-only → fontfix。

## 分发设计实战验证

- "GitHub 权威源 + jsDelivr 镜像回退"首次实战：raw.githubusercontent 300 秒超时失败，jsDelivr 21 秒完成（9.6MB）。
- 镜像文件哈希已记录，正式实现时按"哈希为准"规则对权威源校验。

## 真机验收与字重匹配（2026-11-03）

- 真机启动成功（Steam 启动，窗口化），`fontfix-1030` 包验收**不通过**：缺字已补齐，但补字与原字形颜色深浅不一、小字看不清。
- 诊断：对嫁接字体测 28px 渲染墨量覆盖率，原字形 vs 补字——共享回退 `YoonGothicPro770` 差 +0.153（最严重，正是用户观感主源）、`YDHopeL` +0.063，其余 ≤0.015。根因：统一用 Noto Regular(400) 嫁接，但原字体明显更粗。
- **字重匹配**：从 Noto Sans JP 可变字体按字重实例化候选档（250/300/400/500/700/900），逐字体选墨量最接近档重新子集化并嫁接：

  | 字体 | 原墨量 | 选用字重 | 字体 | 原墨量 | 选用字重 |
  |---|---|---|---|---|---|
  | MJ_CN_ARUDJingXiHeiXGBB | 0.602 | 500 | MJ_KR_YDHopeL | 0.361 | 250 |
  | MJ_KR_YoonGothicPro770 | 0.592 | 500 | MJ_TW_ARFangSongB5B | 0.432 | 250 |
  | MJ_TW_ARUDJingXiHeiE1HKB | 0.608 | 500 | MJ_CN_ARYuanGBL | 0.431 | 300 |
  | MJ_TW_ARYuanB5M | 0.510 | 400 | MJ_CN_ARWeiBeiGBM | 0.399 | 250 |

- 工具链陷阱：`fontTools.varLib.instancer.instantiateVariableFont` 必须 `inplace=False` 使用返回值——`inplace=True` 对本字体静默失败（gvar/fvar 原样保留，各档渲染结果完全相同），首轮字重测量因此全灭。
- 8 款字体按上表字重重嫁接（缺字数 364–874，嫁接后 cmap 校验无缺失），重建 `pakchunk99-fontfix-1102.pak`（40,891,135 字节，SHA-256 `4841B371…7399B8`，沿用旧译文条目仅替换字体，回环校验通过）。
- **真机验收通过**：主菜单补字处笔画粗细与原字形一致，深浅不一问题解决。

## 待办

- 剧情/战斗界面是否还有 KS_Meldir/Skech 子集字体渲染的译文（未验，可能另有缺字）；
- 字体嫁接沉淀为准备管线能力（输入：游戏字体清单+译文用字；输出：嫁接字体+覆盖包），并对 KS 子集字体（Meldir/Skech）评估是否需要同法处理；
- 直连启动退出码 1 根因（本作与 BALL x PIT 共性）。
