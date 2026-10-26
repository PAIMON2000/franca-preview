# FrancaLoader（实验性）

Windows x64 的 WinHTTP 代理与 Unity IL2CPP / CoreCLR 引导实验。自研 Loader 不会被桌面安装器自动部署，也不替代默认 Doorstop。2026-10-23 至 24 已在一台机器的 BALL x PIT / Unity 6000.0.62f1 上独立验证真实启动、印尼语菜单、新文本翻译、配置停用/恢复和缓存复用，见 [真机记录](../../tests/2026-10-23-live-games.md)。其他版本/系统与启动时序覆盖仍待验收；托管入口测试成功本身不等于游戏兼容。

## 可重复构建

需要 Windows x64、PowerShell 7、Visual Studio 2022 的 C++ x64 工具及 Windows SDK。`vswhere` 自动定位工具链，脚本不安装或下载工具。

```powershell
pwsh -File src/Loader/build.ps1
pwsh -File src/Loader/test.ps1
```

构建只写 `src/Loader/build/`。脚本从当前系统的 **System32/winhttp.dll** 生成命名/序号导出表、C 名称数组和 x64 MASM 跳板；转发保留参数寄存器、XMM 参数及栈参数，并生成 FRAME / .pdata / .xdata 展开信息；原生宿主检查展开表存在。系统 DLL 运行时按系统目录绝对路径加载，避免代理再次加载自身。目标仅为 Windows x64，不支持 x86、Mono 游戏或跨 Windows 版本的未验收分发。

输出 `winhttp.dll`、`exports.def/c/asm` 与 `build-info.json`（版本、工具链、SDK、系统 DLL 哈希、导出数、产物哈希）。产物均被 Git 忽略。`/Brepro` 固定链接时间元数据；在相同源码、工具链、SDK 和系统 DLL 输入下，两处不同输出目录的 DLL SHA-256 已一致。不同 Windows 版本的导出集可能不同，不承诺跨输入相同产物。

本机输入：MSVC 工具集 14.44.35207 / SDK 10.0.22621.0 / 70 个系统转发导出。链接器会对两个 COM 导出的原系统序号发出 LNK4222 建议；为保留系统导出 ABI，未删除序号，警告有意保留。不能把日志里的警告说成零警告构建。

## 配置与行为

读取进程 EXE 所在目录的 doorstop_config.ini。沿用 General / Il2Cpp 字段，同时要求明确选择实验引导：

```ini
[General]
enabled=true
target_assembly=BepInEx\core\BepInEx.Unity.IL2CPP.dll
ignore_disable_switch=false
[Il2Cpp]
coreclr_path=dotnet\coreclr.dll
corlib_dir=dotnet
[Franca]
experimental_bootstrap=true
```

- `enabled=false`、未选择实验引导、已有 DOORSTOP_DISABLE / DOORSTOP_INITIALIZED 且未忽略时，只转发 WinHTTP，不运行托管入口。
- 路径使用有界 Unicode Win32 API；进入 CoreCLR 时转 UTF-8。日志在 `BepInEx/franca-loader.log`，注明版本、阶段、HRESULT / Win32 错误。
- DllMain 不执行 LoadLibrary、日志 I/O、CoreCLR 初始化或等待；固定模块生命周期后启动工作线程。代理按进程生命周期固定，不支持运行中卸载或替换；DLL 内创建线程仍是需要真机验证的启动边界。
- 状态 1=托管入口返回，2=按配置跳过，10–23=具体诊断失败或等待超时。`FrancaLoaderWait(timeoutMs)` 仅供独立探针等待，不得在 DllMain 内调用。
- 本实现仍从工作线程引导；已有上述单机/单版本成功启动证据，但尚未覆盖足够的冷启动、时序变化和异常场景。因此不作为正式游戏适配器发布。

## 测试与副作用

`test.ps1` 编译原生宿主、模拟 CoreCLR 与小型 net9.0 托管程序集，在唯一临时目录内运行，结束清理前检查绝对目标。只创建本机 WinHTTP 会话/连接/请求句柄来验证 5/7 参数转发，**不发送 HTTP 请求**，不启动游戏、不写游戏目录。

覆盖配置缺失/禁用/未选实验、继承环境、目标/目录/运行时缺失、导出缺失、初始化失败、委托失败、入口异常、成功路径、中文路径，以及真正的 .NET 9 CoreCLR 调用 Doorstop.Entrypoint.Start 并写入标记。证据在 `tests/artifacts/loader/`，结果摘要见 [Loader 验证记录](../../tests/2026-10-23-loader.md)。

## 下一验收门槛

本机首次真实验收已按备份上游代理/INI、显式实验开关、窗口化启动、日志与真实画面对照、配置停用/恢复、移出本地文本缓存后重启复用的步骤完成；结束后恢复上游代理和配置并校验哈希。覆盖仅限记录中的 BALL x PIT / Windows 环境。后续仍需扩大冷启动、不同 Windows/Unity 版本及异常引导覆盖，明确每个阶段的超时和失败恢复结果。保持实验状态，构建、临时夹具或一次成功不能替代这些门槛。

## 来源与许可证

`loader.c` 的引导序列基于 [UnityDoorstop bootstrap.c](https://github.com/NeighTools/UnityDoorstop/blob/master/src/bootstrap.c) 的 IL2CPP 引导路径；按 LGPL-2.1 保留该文件及其修改的来源和源码，许可全文见 [UnityDoorstop.txt](../../LICENSES/UnityDoorstop.txt)。构建脚本、探针和新生成的转发桩是 Franca 新增代码。顶层 MIT 不覆盖上游改编代码的独立授权。再分发时须保留确切分发版本的源码及上游要求。
