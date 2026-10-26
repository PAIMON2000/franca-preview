# 非游戏软件过滤验证 · 2026-10-23

设置页「隐藏非游戏软件」默认开启（包含缺少字段的旧配置）；用户点击后保存，游戏库即时更新。当前以本地 Steam 类型为主，已知软件名称及辅助程序特征回退；过滤只影响展示、数量及最近启动入口，扫描条目保留，取消勾选可恢复。初版仅匹配软件名称，后续修正见下文。

## 验证结果

- 常规 `dotnet build src/Franca/Franca.csproj -c Release` 被正在运行的 Franca（PID 33552）占用 exe 阻止复制；未关闭现有进程。
- `dotnet build src/Franca/Franca.csproj -c Release -o <repo>/src/Franca/bin/SoftwareFilterCheck`：通过，0 警告、0 错误。
- `dotnet src/Franca/bin/SoftwareFilterCheck/Franca.dll --probe-library`：退出码 0，全部 RESULT PASS。新增八项检查覆盖默认勾选/旧配置、软件原名及目录回退、未知和相似游戏名称保留、计数、搜索、取消/重新勾选即时更新、false 序列化保留、原始扫描条目不丢失。
- 原有搜索/排序、元数据及 15 种语言完整性检查通过；玩家设置文件保持不变。只读扫描本机 Steam 78 项；元数据请求使用模拟 HttpClient，不调用真实翻译端点、不修改游戏目录。

## 后续 debug 与正式输出更新

用户反馈过滤未生效且设置没有 UI。核对发现，根目录 `Franca.lnk` 指向标准 Release 输出，但此前只有 `SoftwareFilterCheck` 临时目录构建成功；标准 DLL 时间为 16:59:20，临时验证 DLL 为 17:20:13。旧程序缺少整个功能，导致过滤和 UI 同时缺失。

- 确认无 Franca 进程占用后，标准 `dotnet build src/Franca/Franca.csproj -c Release` 成功，0 警告、0 错误；常用快捷方式指向的程序已更新。
- 加强 `LibraryProbe`：在不激活的屏幕外窗口加载真实设置页，关闭后台服务，验证控件可见且有正确文案；通过勾选框的 Click 路由事件验证即时恢复/隐藏，替代原先直接调用宿主方法。
- 标准输出运行 `dotnet src/Franca/bin/Release/net9.0-windows/Franca.dll --probe-library <repo>/src/Franca/bin/Release/net9.0-windows/software-filter-evidence`，退出码 0，全部 RESULT PASS（包含 15 语言、78 项本机 Steam 只读扫描及用户设置不变）。
- 人工检查生成的 `settings-software-filter.png`，确认「隐藏非游戏软件」位于主题选择器下方且默认勾选；截图位于上述忽略的构建目录中。

本次未启动普通应用后台服务、真实翻译端点或游戏，未修改游戏目录。后续从根目录快捷方式打开即可加载更新后的正式输出。

## Steam 类型与 Glacier Events 漏项修正

- 根因：初版 `GameLibraryOrder.SoftwareNames` 是名称名单，未接入 Steam 类型；扫描器将无清单但含 exe 的目录作为回退条目，因此漏掉 Glacier Events。
- 新增 `SteamAppInfoReader`，只读本地 `appcache/appinfo.vdf`（v39/v40/v41），读取 `common/type`、`common/name`、`config/installdir`。格式核对参考 [SteamAppInfo 的格式说明与解析器](https://github.com/SteamDatabase/SteamAppInfo)；本轮独立实现所需字段读取，不引入上游源码或依赖。损坏记录隔离，未知格式或缺失缓存回退，不联网分类、不写 Steam 文件。
- 有清单按 appId 关联类型；无清单仅按无歧义的完整安装目录名关联类型，不据缓存补造安装 appId。Game/Demo/Mod 保留；Application/Tool 等明确软件类型隐藏，未知类型无其他明确软件特征时保留；原名称名单用于回退。
- 本机 Glacier Events 无安装清单，缓存类型为 `Beta`；目录只有 EA 反作弊启动器及 DLL，按「所有 exe 均为 EAAntiCheat.GameServiceLauncher.exe」识别辅助目录。目录含真实游戏 exe 时不会命中，Game/Demo/Mod 证据也优先保留。没有按 Glacier 名称拉黑，也没有把 Beta 一律隐藏。
- 标准 Release 构建通过，0 警告、0 错误；`--probe-library <截图目录>` 退出码 0，全部 PASS。新增覆盖三种二进制版本、坏记录隔离、截断/未知版本/缺失缓存、类型优先级、无名单软件、目录匹配冲突、辅助程序/真实游戏共存、点击恢复及最近启动入口。
- 本机只读扫描 78 项，78 项具有类型，隐藏 5 项（分别为 3 个 Application、1 个 Tool、1 个 Beta + 辅助目录特征）。截图显示过滤后 73 项；用户设置文件保持不变，缺失过滤字段按默认 true 生效。
- `--probe-blueprint` 全部 PASS，已更新架构索引、系统地图与生成蓝图。最初对账将通用嵌套名 `Entry` 与 DictionaryService 的同名类型混淆；改为专用名 `SteamAppMetadata` 后消除误报，没有增加模块依赖。
- 常用快捷方式指向的标准 Release 已更新；更新前正常关闭旧 Franca，未强制结束进程，未启动或修改真实游戏。本轮探针的元数据 HTTP 请求仍为本机夹具，应用正常启动的后台行为与探针隔离。

本轮截图保存在忽略的 `src/Franca/bin/Release/net9.0-windows/steam-type-evidence/`，不是游戏翻译验收证据。
