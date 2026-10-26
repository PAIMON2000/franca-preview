# Franca 项目指引

开源的游戏翻译运行时（免费、非商业）；桌面原型为 .NET 9 / WPF，原生 Loader 尚属实验。

## 开始任务

- 先按任务在 `architecture.json` 的 `features` 中定位；只读命中项、入口和关联代码。
- 架构与已知耦合见 `docs/SYSTEM-MAP.md`；只有涉及边界或跨模块改动时才读。
- 当前工作状态以 `docs/DEVELOPMENT-PROGRESS.md` 为准；历史交接及排障按需查 `docs/HANDOFF.md`。
- 实验依据在 `lab/`，项目实际能力说明在 `docs/site/index.html`；不要每次修改都通读。
- 索引是导航，源码是实际行为依据；发现过期入口时随相关改动修正。

## 搜索与阅读

- 先检查工作区改动，保留用户已有修改。
- 顺序：索引入口文件 → 同功能相关文件 → 所属目录 → 必要的跨模块调用方。
- 仅在定位失败、索引缺失或需要核对影响范围时扩大搜索；不要反复遍历整个仓库。
- 先用 `rg --files` 找文件、`rg -l` 找命中文件，再按函数或行段读取；避免整文件倾倒。
- 常规源码搜索限于 `src/Franca` 或 `src/Loader`，排除 `bin/`、`obj/`、Loader 的 `build/`。
- `tools/` 是本机下载、备份和证据缓存；不常规搜索，不删除。`lab/`、`LICENSES/`、日志与翻译包仅在任务需要时读取。
- `UiStrings.cs` 是大型多语言资源表：先查 key，再读对应片段，不通读全部语言。
- 无新改动、失败或疑点时，不重复同一搜索和验证。

## 修改边界

- 优先沿现有服务边界做局部修改；不为普通功能任务顺便搬目录、换框架或扩大重构。
- 新业务流程应放入服务，页面保留输入、状态展示和服务调用；避免新增服务对 `MainWindow` 的依赖。
- 正式协议在 `TranslationProviders.*.cs`；`EndpointTestService` 只包装健康探测。探针入口与实现位于 `Diagnostics/`。
- `RelayServer`、`PackStore`、自动端点选择按实例隔离；中继使用 `/translate/<appid>` 归档，旧 `/translate` 不读写游戏包。
- 页面通过 `GameTranslationService` 启停/修复游戏；安装器接收显式中继地址，不自行启动运行时。
- 长文入口仍限 2500；`TranslationTextSegmenter` 每次端点请求限 200，`SegmentedTranslationService` 排队、缓存片段并在全部成功后重组。中继模式必须保留段落空白；不把半成品当完整译文。
- 修改上游改编代码时保留文件头、来源、版权与许可证，按需核对 `docs/THIRD-PARTY-NOTICES.md`。
- 游戏目录写入先确认目标并保留可恢复备份；不把构建或探针通过当作真实游戏翻译验收。

## 验证与交接

- 构建：`dotnet build src/Franca/Franca.csproj -c Release`。
- 程序：`src/Franca/bin/Release/net9.0-windows/Franca.exe`；按功能索引选相关探针，先读其副作用说明。
- `--probe-architecture` 验证选择取消、请求快照、每游戏缓存及业务流程；仅使用临时目录和本机模拟 HTTP，不调用真实端点或修改真实游戏。
- `--probe-blueprint` 对账模块依赖：扫描真实跨模块引用，未声明边 FAIL 并重新生成 `docs/BLUEPRINT.md`；改模块边界时先更新 `architecture.json` 的 paths/dependsOn/contract 再重跑。
- `--probe-segmentation` 验证分段边界、段落/格式保留、排队、取消与部分失败重试；同样只使用临时夹具和本机 HTTP。
- 文档与导航修改只检查 JSON、路径、链接和差异，不启动应用、联网探针或真实游戏。
- 测试用例以 `tests/test-plan.md` 为准；当前结果看 `tests/` 的日期记录。旧记录中的游戏是否干净需重新确认。
- 功能完成后更新进度及必要证据；入口、边界或验证方式变化时更新 `architecture.json`，边界变化同时更新系统地图。
- 不把同一段状态重复写入多份文档；`docs/HANDOFF.md` 保留换机、历史经验及详细排障。

## 对外表述

- 功能、兼容范围、性能和费用说明必须对应实际实现或注明日期/环境的证据；未完成验收不能写成已支持。
- 不以引擎识别、磁盘就绪或模拟探针通过代替真实游戏验证；不保证零崩溃、固定延迟、账号安全或服务商免费额度。
- 未接入的调研项目在文档和界面中标为未接入；社区、商店和工坊功能不得写成已确定交付。当前范围以开发进度和兼容矩阵为准。
