# FluentUSB

一款常驻桌面的 U 盘辅助工具（Avalonia MVVM，.NET 10）。

## 功能规划

- 识别 U 盘插入，自动扫描并弹出窗口，按最新日期列出若干文件
- 老师按需勾选，一键复制到预设的本地位置并打开
- 后续扩展：桌面文件自动化整理、分类归纳到指定目录

## 技术栈

- [Avalonia UI](https://avaloniaui.net/) 12 + Fluent 主题
- MVVM：CommunityToolkit.Mvvm
- .NET 10

## 目录结构

```
FluentUSB/
├── Models/          # 数据模型
├── ViewModels/      # 视图模型
├── Views/           # 视图（AXAML）
├── Assets/          # 图标等资源
├── Program.cs       # 入口
└── App.axaml(.cs)   # 应用级配置
```

## 构建与运行

```powershell
dotnet restore
dotnet build
dotnet run --project FluentUSB.csproj
```
