# Codex 免费 Token 一键部署

这是一个面向普通用户的 Codex Desktop 一键安装包，已经打包好 Windows 和 macOS 版本。

提供了最新的 DeepSeek V4 免费模型，日常任务轻松解决。

## 下载地址

请到最新版 Release 下载：

[打开下载页面](https://github.com/ss22219/CodexDesktopInstaller/releases/latest)

- Windows x64：`CodexInstaller-windows-x64.zip`
- macOS Apple Silicon：`CodexInstaller-mac-arm64.zip`
- macOS Intel：`CodexInstaller-mac-x64.zip`

不确定自己的 Mac 是哪种芯片时，一般 2020 年之后的 M 系列 Mac 选 Apple Silicon，老款 Intel Mac 选 Intel。

## 使用方法

### Windows

下载并解压 `CodexInstaller-windows-x64.zip`，然后运行 `Codex 安装.exe`。

### macOS

下载并解压对应的 macOS 安装包，然后打开 `安装 Codex.command`。

如果 macOS 提示无法打开，右键点击 `安装 Codex.command`，选择“打开”。

## 说明

这个仓库主要用于分发一键安装包。大型安装文件不会直接放在源码里，而是放在 GitHub Release 下载页。

安装包内已包含基础配置、Skills 和插件缓存，适合快速部署和测试 Codex 桌面版。
