# 2026-10-24 大模型主用高亮

## 问题与修复

DeepSeek 的选择会保存到 Settings.Endpoint，卡片也更新 IsPrimary，但主用按钮没有绑定选中状态；独立 DeepSeekPanel 的边框同样固定为中性色。因此不能用按钮没有亮起推断设置未生效。

端点页新增共享的主用按钮样式，沿用 AccentButtonStyle 的交互模板：未选择时中性，IsPrimary=true 时使用主题强调填充与对应文字色，切换主用后恢复中性。普通端点卡、已有/自定义模型卡及 DeepSeek 专用按钮共用此样式；DeepSeek 面板边框同步 CardBorderBrush。没有修改 API 协议或选择逻辑。

## 验证

- 新增真实 WPF 路由检查：点击 DeepSeek 主用、确认设置及卡片选中、六主题按钮与边框高亮、切换到必应后 DeepSeek 取消高亮且必应按钮亮起。
- 独立 Release 构建零警告零错误；导航 59/59、端点配置 24/24 通过，退出均为 0。截图 `tests/artifacts/2026-10-24-endpoint-primary/screens/deepseek-primary.png` 已查看，按钮及边框高亮可见。
- 探针用假 Key、临时设置和本机模拟 HTTP，没有读取用户真实密钥或调用付费服务。未运行 `--probe-deepseek`。
- 标准构建首次因现有应用锁住 exe 失败。用户明确同意重启后，正常关闭 Franca、完成标准 Release 构建（零警告零错误），再通过根目录快捷方式重开，主窗口 Responding=true。没有强杀进程、关闭其他应用或更改用户主用配置。

## 当前大模型请求实现（源码核对）

`TranslationProviders.Custom.cs` 向 `/chat/completions` 发两条消息：system 指定游戏翻译、源/目标语言、只输出译文并保留占位符/格式标签/标点风格；user 放原文。temperature=0.3、max_tokens=1024，DeepSeek 附 thinking.type=disabled。

模型正文是纯译文，不要求它生成 `{译文}` 或 JSON 对象，也没有 response_format。应用解析接口外层 JSON 的 `choices[0].message.content` 并 Trim；对 length/content_filter 拒绝半成品，但没有完整的占位符/解释文本强校验。游戏中继先查词典/缓存（取决于模式），需要在线时走主用/备用端点；长文切成至多 200 字符的请求片段，全部成功后重组。每次模型请求只有提示词和该段原文，没有加入游戏场景或前文对话历史。
