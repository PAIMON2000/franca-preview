# 游戏行不支持提示 · 2026-10-23

- 原因：操作按钮绑定 `IsSupported`，不支持时整个单元格为空；状态列又可能优先显示官方语言信息，因此玩家无法从该行判断为何没有按钮。
- 修改：操作列增加与按钮互斥的文字提示，复用 `ui.unsupported`，简体中文为「暂未支持」、繁体为「暫未支援」，其他语言使用既有本地文案；不使用「即将支持」作为能力承诺。支持的游戏仍显示原按钮，临时处理中的禁用状态不归为不支持。
- `dotnet build src/Franca/Franca.csproj -c Release`：通过，0 警告、0 错误。
- 标准输出 `--probe-library <repo>/src/Franca/bin/Release/net9.0-windows/unsupported-row-evidence`：退出码 0，全部 PASS，用户设置保持不变。
- 人工核对 `library-actions-zh-CN.png`：合成 Unity 行显示「启用翻译」；RenPy、未知引擎行在操作列显示「暂未支持」；Unreal 行保留状态「存在官方简体中文」，操作列仍显示「暂未支持」。截图在上述忽略的构建目录，非真实游戏翻译证据。
- 本轮只核对列表展示，未启用或修改真实游戏。标准 Release 程序已更新。
