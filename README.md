<p align="center">
  <img src="src/OmniRef.App/Assets/AppIcon.svg" width="160" alt="OmniRef 图标">
</p>

<h1 align="center">OmniRef</h1>

<p align="center">
  面向 Windows 的轻量本地资料画布
</p>

OmniRef 把常看的图片、文件、文件夹、文本和网址放在同一个无限画布上，像 PureRef 一样随手展开，但不局限于参考图。

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-1674CE" alt="Windows 10 或 11">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10">
  <img src="https://img.shields.io/badge/UI-WPF-5C2D91" alt="WPF">
</p>

## 功能

- 无限画布：以光标为中心缩放、空格或中键平移、框选、多选、拖动和调整尺寸。
- 内容卡片：图片、普通文件、文件夹、纯文本、HTTP/HTTPS 网址和分组框。
- 整理工具：对齐、等距分布、层级调整、分组框、标签和全文搜索。
- 本地优先：不联网抓取网址、不含遥测，工作区是可移动的单个 `.omniref` SQLite 文件。
- 引用或内嵌：文件默认保留本地引用，也可转为内嵌副本；删除卡片不会删除原文件。
- 日常驻留：多个工作区标签页、500ms 防抖自动保存、会话恢复、托盘、置顶和 `Ctrl+Alt+Space` 全局热键。
- 低占用预览：可见区域空间索引、按显示尺寸解码图片、独立 STA Shell 缩略图线程、96MB 内存预览缓存。
- 系统/浅色/深色主题，简体中文/英文自动切换。

## 快速开始

开发环境需要 Windows 10/11 x64 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。仓库通过 `global.json` 固定 SDK 版本。

```powershell
dotnet restore OmniRef.slnx
dotnet build OmniRef.slnx
dotnet test tests/OmniRef.Tests/OmniRef.Tests.csproj
dotnet run --project src/OmniRef.App/OmniRef.App.csproj
```

生成无需预装 .NET 的便携包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

输出位于：

- `artifacts/OmniRef-win-x64/`
- `artifacts/OmniRef-win-x64.zip`

ZIP 为不裁剪、非单文件的 `win-x64` 自包含发布，解压后直接运行 `OmniRef.exe`。发布目录同时包含示例工作区和使用文档。

## 项目结构

```text
src/
  OmniRef.Core/                    领域模型、坐标、搜索、空间索引、撤销历史
  OmniRef.Infrastructure.Windows/ SQLite、Windows Shell、热键、设置、单实例
  OmniRef.App/                     WPF、MVVM、虚拟化画布、主题和本地化
tests/
  OmniRef.Tests/                   核心与 SQLite 集成测试
docs/
  SHORTCUTS.md                     操作和快捷键
  DATA_STORAGE.md                  工作区格式、引用与本地数据目录
scripts/
  publish.ps1                      自包含发布和 ZIP 打包
```

运行时依赖仅使用 `CommunityToolkit.Mvvm`、`Microsoft.Data.Sqlite` 和 SQLite 原生包；没有 WebView、EF Core、通用 Host 或大型主题框架。

## 使用提示

- 第一次启动会创建一个欢迎工作区。未命名工作区保存在恢复区，使用“另存为”即可转成普通 `.omniref` 文件。
- 点击窗口关闭按钮默认隐藏到托盘；使用窗口右上角“退出”或托盘菜单“退出”才会终止进程。
- 内嵌普通文件以只读临时副本打开。如需编辑，请先导出。
- 路径失效的引用会显示醒目标记，可在属性侧栏重新定位。

完整操作见 [快捷键说明](docs/SHORTCUTS.md)，数据和隐私说明见 [数据存储说明](docs/DATA_STORAGE.md)。
