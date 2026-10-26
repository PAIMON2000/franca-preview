# 2026-10-24 东方 thcrap 运行时路线真机验收（th11）

## 结论

地灵殿（th11，Steam v1.00a）经 thcrap 路线启用后，**局内对话简体中文渲染正常，用户实际游玩确认"可以用"**。后续扩展已补主菜单与下级界面精灵图、结局、音乐室评论和启动器对话框的中文画面验收；Steam 版符卡名仍自动跳过。th10 已迁移到 thcrap 路线，对话与界面由用户确认，具体范围见下方边界及兼容矩阵。

## 链路

Franca 启用（thcrap 路线）→ 工作树 `<程序目录>/thcrap/`（裁剪离线运行时；补丁数据首次启用时按清单下载 + franca/zh_hans 补丁）→ 存根 `th11.exe`（真身 `th11.exe.frorig`）+ `th11.thcrapcmd`（loader / config / games.js 游戏 ID）→ Steam 或直接启动均经 thcrap_loader 注入 → jdiff 译文 + Noto Sans SC 运行时渲染。

## 真机过程中发现并修复的问题

1. **裁剪误删 `bin/update.json`**：loader 的 `update_finalize` 找不到该文件即返回失败（启动即退出码 1）。已补回（历史迁移清单，新布局不命中任何条件）。
2. **loader 参数扩展名限制**：真实 exe 备份名 `th11.exe.frorig` 不以 `.exe` 结尾，命令行直传会被当作游戏 ID 查 games.js。改为 games.js 映射（`franca_th11` → 真实路径），存根透传。
3. **存根 FileExists 校验误伤游戏 ID 形式**：第三行为裸 ID 时不再要求文件存在（无路径分隔符即视为 ID）。
4. **HasArtifacts 误匹配**：`*.exe.frorig` 通配把 custom.exe 汉化备份（已改名 `.zhbak`）当 thcrap 产物，导致停用分流到空操作、日志谎报"已停用"。已收窄为 `thNN.exe` 前缀。
5. **游戏库 RefreshRow 收起详情**：RemoveAt/Insert 重建行丢选中，开关操作后展开详情消失。已改为保持选中。
6. 探针夹具改为自动识别真实目录的启用态（原位存根则取 `.frorig` 做哈希/复制）。

## 证据

- thcrap 日志：`th11/st01_00a.msg.jdiff` 命中 franca/zh_hans 补丁加载（`logs/thcrap_log.txt`）。
- 截图：`tests/artifacts/th11-*.png`（标题、难度选择、一面 Kisume 战等；对话中文由用户游玩确认，我侧按键注入未采到对话帧——SendInput 对该游戏不可靠，最终由用户手动操作）。
- 探针：`--probe-touhou-thcrap` failures=0（含 games.js 断言）、`--probe-touhou-static` failures=0、`--probe-architecture` failures=0；Release 构建 0 警告。

## 已知边界

- 结局：经辅助补丁加载对应 jdiff 实测通过，结局中文渲染用户确认（测试后已撤除该补丁）。
- 启动器窗口模式对话框：stringdefs.js 中文，真机确认。
- 音乐室评论：已接入翻译管线（musiccmt.txt 在 dat 内，17/18 首逐首翻译为 musiccmt.js），探针覆盖结构断言，已真机画面确认。
- 符卡名：Steam 版 exe 关键数据段为加密/非明文，静态提取不可行，管线自动跳过；仅对无 DRM 的 exe 生效。能否支持取决于后续是否有合规的数据来源。
- 主菜单与下级界面文字为烘焙图片（取证：title01.png 等精灵图含全部菜单项，exe/dat 无对应文本）：已管线化生成中文精灵图（th11 23 张 / th10 16 张，含主菜单、难度、画面标题、结算、支援详情、人物卡、暂停菜单、加载、关卡标题卡；词条走端点翻译缓存、zh 人工校译覆盖、本地成像零 token），用户全页面真机验收通过。
- 每次启动出现"窗口模式选择"对话框是 Steam 版自带行为（custom.exe「每次启动时询问」），现已中文；可用 custom.exe 关掉。
- 注入方式可能被杀软启发式误报；th10/th11 为纯单机无反作弊。
- th10 已从旧静态补丁迁移到 thcrap 路线；不能将旧静态补丁的 cp932 乱码描述为当前状态。
