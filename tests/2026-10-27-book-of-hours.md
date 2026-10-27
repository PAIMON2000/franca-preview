# 2026-10-27 Book of Hours 首次启动检查

用户授权选择未验 Unity 游戏并启动。目标为本机 Steam App 1028310，目录 `D:\steam\steamapps\common\Book of Hours`，游戏日志版本 2026.1.f.3，Unity 2022.3.62f3 / Mono。初始未见 BepInEx、winhttp、Doorstop、.franca；已备份原用户数据、PlayerPrefs、Steam 清单和原游戏文件 SHA256 清单，不能据此宣称 Steam 干净安装。

Franca 原窗口抓图/控件读取超时，改用本地证据目录中的辅助程序引用当前 Release 的正式 GameTranslationService / TranslationInstaller / RelayServer；独立 en → id 设置，GoogleTranslateV2 主用、BingTranslate 备用，无付费 Key，未改变日常 Franca 全局设置。辅助程序并非游戏库按钮 UI 验收。

正式事务安装 BepInEx 5.4.23.5、XUnity 5.6.2，配置健康。中继独立监听 `127.0.0.1:47821/translate/1028310`。事务 ID `537fceb870c2406f870ecb9736d65832`，原始文件哈希与备份见忽略目录 `tests/artifacts/2026-10-27-book-of-hours/`。

游戏原 config.ini 为 Culture=zh-hans，备份后改 en；窗口化 1600×1024（官方最低分辨率）、-noaudio、独立 Player 日志。Steam 初次停在 CheckShaderDepotManifest，随后直接启动一个实例；Steam 延迟完成后产生第二实例，已正常关闭直接启动的 PID 17784，仅保留 Steam 实例 PID 6132。此启动过程不计作稳定冷启动通过。

日志证实 BepInEx / XUnity 加载完成，抓取时中继 ServedCount=85、CacheHits=7、Failures=0、InFlight=1。游戏生成缓存包含 Settings→Pengaturan、Load→Muat、完整多行印尼语提示。TextMesh Pro SetCharArray_Hook3 有兼容警告，实际影响待验。

**实际显示已获用户确认。** 用户在本次启动后反馈“成功了”，据此记录首次游戏内翻译显示成功；用户未逐项确认具体页面、字体、漏译或全文覆盖。自动侧未取得实际游戏截图。 Windows Computer Use 对 Franca、Steam 和游戏抓图均超时；未盲点菜单、未主动加载或游玩存档。游戏日志自身在启动时读取 AUTOSAVE。原用户数据已备份，当前保持游戏和独立测试中继运行，等用户观察；运行中不还原用户数据或改配置。

后续：正常退出后验证重启缓存及停用恢复，并补具体页面、动态文本和排版范围。测试结束需恢复原 Culture 与用户数据（先保留测试后数据），并将游戏翻译配置恢复日常路由或停用；不得在测试中继关闭后把当前 id / 47821 配置当作日常完成态。未验长时间游玩、完整恢复或全游戏覆盖。
