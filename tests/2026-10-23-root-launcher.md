# 根目录启动入口与整理 · 2026-10-23

## 变更

- 本机根目录生成「Franca.lnk」，使用现有 `franca-taskbar.ico` 品牌图标，目标是标准 Release `Franca.exe`，工作目录设为程序目录；运行数据继续使用既有程序目录。
- 生成脚本在 `src/Launcher/Setup-Shortcut.ps1`。快捷方式已改名为 `Franca.lnk` 并纳入 Git；其中含本机绝对路径，换机或移动仓库后，构建并按 README 重新生成。
- 两个说明网页移到 `docs/site/`；CHANGELOG 和 THIRD-PARTY-NOTICES 移到 `docs/`。README、AGENTS、架构索引、文档导航、交接表和网页相对链接同步更新。独立部署网页时需更新发布入口并保留文档相对目录。
- 程序许可证窗口的文件名和布局保持原约定；csproj 从新的源码位置复制声明，输出仍叫 `THIRD-PARTY-NOTICES.md`。
- 源码、测试、实验、上游许可证和本机备份目录保留原位；未删除 `tools/` 内容。

## 验证

- 标准 Release 构建被运行中的 Franca（PID 51984）占用 exe 阻止复制，未结束现有进程。
- 独立输出构建：`dotnet build src/Franca/Franca.csproj -c Release -o <repo>/src/Franca/bin/RootLauncherCheck`，通过，0 警告、0 错误。
- 使用 `powershell -NoProfile -ExecutionPolicy Bypass -File src/Launcher/Setup-Shortcut.ps1` 成功生成快捷方式；该选项仅用于此进程，不修改系统执行策略。读回目标、工作目录、图标路径，目标与图标均存在。未通过快捷方式另开应用。
- 独立构建的第三方声明与 `docs/THIRD-PARTY-NOTICES.md` SHA256 一致。
- 检查 JSON、新功能索引路径、调整文件的本地 HTML/Markdown 链接和差异；不运行应用探针或真实游戏。
