# 实验记录 001：Suzerain × XUnity.AutoTranslator

日期：2026-10-08 · 靶标：Suzerain 3.1.0（Unity 6000.3.9f1，IL2CPP）

## 部署

- BepInEx 6.0.0-be.788（bleeding-edge）+ XUnity.AutoTranslator 5.6.2（BepInEx-IL2CPP 版）
- 文件装入游戏目录，未修改任何游戏本体文件
- 配置：Language=zh-CN，FromLanguage=en

## 结果

- ✅ 全链路跑通：主菜单英→中（New Game→新游戏、CONTINUE→继续 等 40 条）
- ✅ Il2CppInterop 成功处理 Unity 6 元数据 v39
- ⚠️ 首次进入文本延迟 5~6 秒（空缓存实时翻译，网络逐条请求）
- ⚠️ 部分区域未翻译
- ⚠️ 典型机翻错误：Load→负载（应为"载入"），上下文缺失的实物标本

## 踩坑记录（agent 自动化的需求来源）

1. BepInEx 官方 v6.0.0-pre.2 的 Cpp2IL 只支持元数据 ≤29，Unity 6（v39）必须用 bleeding-edge 构建 → 适配数据库需按 Unity 版本选择 BepInEx 构建
2. XUnity.Common.dll 在 core/ 目录，替换 BepInEx 核心时容易丢 → 依赖清单要完整
3. TMP_Text_SetCharArray_Hook3 在 Unity 6/TMP 1.4 失效（主钩子正常兜住）→ hook 兼容性需 per-game 验证

## 三个体验差距 → 对应产品设计

| 差距 | 解法 | 归属 |
|---|---|---|
| 实时翻译延迟 | 预翻译包（本地哈希查找） | 图书馆模式 |
| 覆盖漏洞（hook 变体失效/贴图文字/框架默认关闭） | per-game 适配配置 + ResourceRedirector | 云端适配数据库 |
| 上下文质量（Load=负载） | 词库 + 上下文管线 | 包格式 v1 |

## 结论

阶段 0 核心验证过关：Unity 6 IL2CPP 游戏可零代码实现文本级翻译。
机制可行，体验差距明确且全部有对应设计，无新增未知风险。
