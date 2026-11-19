# Apple Music 桌面歌词

Windows 上的 Apple Music 桌面歌词工具。播放歌曲时从 [LRCLIB](https://lrclib.net/) 匹配同步歌词，在桌面显示当前句和下一句；也可导入本地歌词。

## 快速开始

1. 确保已安装 Windows 版 Apple Music 和 .NET Framework 4.8。
2. 获取本仓库的源码，在项目目录双击 `Start.cmd`。首次运行时会在 `outputs/` 下构建。
3. 播放歌曲后，用 `Ctrl+Alt+L` 显示歌词。将鼠标移入歌词浮层可打开设置。

## 功能

- 两行透明置顶歌词，支持拖动、贴顶和调整宽度。
- 可分别选择字体色和描边色；记住字体、字号、颜色和逐曲校时。
- 支持本地 LRC/TXT 备用；无时间戳的歌词仅预览前两行，不假装同步。

## 隐私与限制

歌词匹配会将歌名、歌手、专辑和时长发送到 LRCLIB，不发送音频、账号或令牌。本工具独立于 Apple Music 运行，不修改客户端配置，也没有客户端内置开关。

## 文档

- [完整使用与构建说明](docs/README.md)
- [检测记录与已知限制](docs/VALIDATION.md)

[自动歌词实测记录](docs/AUTOMATIC-LYRICS.md) · [延迟优化与校时说明](docs/TIMING.md)
