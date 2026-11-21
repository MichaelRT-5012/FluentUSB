# FluentUSB 项目交接说明

> 更新日期：2026-11-21　当前提交：`16dabd1`（master）　维护者交接文档，面向下一位接手的人 / Agent。

---

## 1. 项目目标

FluentUSB 是一个**常驻托盘的 U 盘助手**，只做 Windows 桌面端，核心链路很短：

1. 后台监听 U 盘插拔（不轮询、不卡界面）；
2. 插入后自动弹出提示卡片，点击可在资源管理器打开；
3. 主窗口展示当前已连接的 U 盘；
4. 托盘菜单 + 主窗口齿轮都能进入设置；
5. 所有外观设置（背景材质、不透明度、主题、弹窗样式）即时生效并持久化。

**视觉目标**：WinUI 3 / Windows 11 的观感，外观语言以 ClassIsland 2.1.0.1 为参照（连体设置卡片、NavigationView 侧栏、DodgerBlue 强调色、错峰入场动画）。

**明确的非目标（目前没做）**：文件浏览 / 安全扫描 / 安全弹出设备 / 自动备份 / 跨平台。

---

## 2. 技术栈与整体结构

| 项 | 值 |
|---|---|
| 运行时 | .NET 10（`net10.0`），仅 Windows（`SupportedOSPlatform("windows")`） |
| UI | Avalonia 12.0.3 + 内置 FluentTheme（**未使用 FluentAvalonia**） |
| MVVM | CommunityToolkit.Mvvm 8.4.1 |
| 字体 | 内置 HarmonyOS Sans SC（Regular / Medium，约 15.7 MB） |
| 设置文件 | `%AppData%\FluentUSB\settings.json` |
| 日志 | `%AppData%\FluentUSB\logs\fluentusb-yyyyMMdd.log` |

```
Program.cs                 单实例互斥，创建 Avalonia App
App.axaml(.cs)             应用资源/全局样式；托盘、窗口生命周期、U 盘事件分发
Controls/
  IntroAnimation.cs        设置页/主窗口的错峰上滑入场动画（附加属性）
  ComboPopupMotion.cs      ComboBox 下拉浮层动画（逐帧透明度+缩放）
  ValueSlider.axaml       带数值显示的滑块
Models/
  AppSettings.cs           可持久化设置（背景、主题、弹窗）
  UsbDriveInfo.cs          U 盘信息记录
Services/
  UsbDeviceMonitor.cs      隐藏窗口 + WM_DEVICECHANGE 监听插拔
  UsbDriveProbe.cs         盘符探测、USB 总线识别、容量信息
  BackdropService.cs       材质能力检测 + 窗口背景应用
  BackdropNative.cs        DWM/Mica/Acrylic 的 P/Invoke
  SettingsStore.cs         设置加载/保存/变更事件
  Log.cs                   文件日志
ViewModels/
  MainWindowViewModel.cs   已连接 U 盘列表、空状态、状态文案
  SettingsViewModel.cs     外观/弹窗设置、背景能力说明、重置
  UsbDriveViewModel.cs     单个 U 盘的展示数据
Views/
  MainWindow.axaml         主窗口（列表 / 空状态 / 设置入口）
  SettingsWindow.axaml     设置窗口（导航 + 外观页 + 关于页）
  UsbToastWindow.axaml     插入提示弹窗（卡片式 / 整窗式）
Assets/Fonts/              HarmonyOS Sans SC + LICENSE（许可证要求随应用分发）
```

### 关键机制

- **单实例**：`Program.cs` 用命名互斥；第二个实例直接退出，**不会**唤起已有窗口（见第 4 节遗留问题）。
- **窗口生命周期**：`ShutdownMode = OnExplicitShutdown`；关闭主窗口/设置窗只是隐藏或销毁，只有托盘「退出」才结束进程。
- **启动参数**：`--show` 打开主窗口，`--settings` 打开设置窗口（排障/自动化用）。
- **U 盘识别**：除 `DriveType.Removable` 外，还通过存储总线类型识别 USB 固态/硬盘盒（历史坑：只看 Removable 会漏设备）。
- **插拔监听**：独立 STA 线程 + 隐藏顶层窗口接收 `WM_DEVICECHANGE`；插入后在后台轮询等待挂载完成，再回到 UI 线程发事件。
- **背景材质**：`BackdropService` 启动时探测系统能力（Solid / Transparent / Blur / Acrylic10 / Acrylic11 / Mica / Mica Alt），不支持的选项在设置页自动禁用并显示原因；所有窗口通过 `App.AppearanceWindows()` 统一刷新。
- **弹窗**：`UsbToastWindow` 支持「卡片式」与「整窗式」两种绘制方式；多个弹窗用 `SetSlot` 堆叠，悬停暂停倒计时。

---

## 3. 本次外观整改（已完成）

参照本地 `ClassIsland-2.1.0.1` 源码逐项对齐，不是凭感觉调色：

### 3.1 设计令牌

| 令牌 | 值 |
|---|---|
| 字体 | HarmonyOS Sans SC（`avares://FluentUSB/Assets/Fonts/#HarmonyOS Sans SC`） |
| 字号 | 页面标题 28 Normal / 副标题 20 SemiBold / 正文 14 / 说明 12 |
| 强调色 | DodgerBlue `#1E90FF`（覆盖 `SystemAccentColor` 及 6 个明暗变体） |
| 卡片 | 4px 圆角、1px `CardStrokeColorDefault` 描边、16 内边距、64 最小高度、20px 图标列 |
| 导航 | 36px 项高、20px 图标列 + 12 间距、4px 圆角、选中 3×16 强调色指示条 |
| 设置容器 | 左右 12/18 内边距、卡片间距 4px（对应 `settings-container`） |
| 入场动画 | 25ms 错峰、0.75s、`SplineEasing(0,1,0,1)`、从 Y=50 上滑淡入 |

> 注意：早期用户曾选择「强调色跟随系统」，后来为了贴近 ClassIsland 改成了固定 DodgerBlue。若要恢复跟随系统，删除 `App.axaml` 两个 ThemeDictionaries 中的 `SystemAccentColor*` 与 `SystemControlBackgroundAccentBrush` 覆盖即可（FluentTheme 会自动取系统强调色）。

### 3.2 具体改动

- **全局**（`App.axaml`）：字体/字号层级、DodgerBlue、Button/ComboBox/TextBox/ToggleSwitch（开/关文案）/RadioButton 圆角与最小高度、卡片与分组样式。
- **设置窗口**（`Views/SettingsWindow.axaml` + `.cs`）：
  - 1040×660，240px 侧栏（分组标题「个性化 / 应用」），版本号放侧栏底部；
  - 页面标题 28px，内容边距 12/18；
  - 设置行按 SettingsExpander 规格；「背景效果」「弹窗背景」改成**一张卡片内含标题行 + 分隔线 + 多条整行可点选项**的展开式结构；
  - 入场动画 + 分类切换重新播放；导航指示条弹簧滑行。
- **主窗口**（`Views/MainWindow.axaml`）：强调色应用图标、标题/副标题/状态、卡片化 U 盘列表、圆角空状态。
- **关于页**：应用信息、文件位置、开源与致谢（含 HarmonyOS Sans 版权与许可声明）。
- **动画**：`Controls/IntroAnimation.cs`（入场）、`ComboPopupMotion`（下拉）、切页淡出。

### 3.3 期间修掉的坑

- Avalonia 12 的 `Button` **默认内容左对齐** → 已在全局样式显式居中（表现为「关闭」按钮文字贴左）。
- Fluent 的 `RadioButton.Padding` **不会移动单选圆点**，只会移动内容 → 选项行改用整行 `Margin` 缩进 56px 对齐标题列。
- 扩展客户区（`ExtendClientAreaToDecorationsHint`）在 Avalonia 12 下会导致原生标题文字与自绘文字重叠、**窗口无法拖动**（`ExtendClientAreaChromeHints` 已被移除）→ 已回退为原生标题栏，应用标识下沉到侧栏。
- `ClassIsland-master/`、`ClassIsland-2.1.0.1/` 放在仓库内会被 SDK 当源码编译（曾报 2434 个错误）→ `FluentUSB.csproj` 中排除，并加入 `.gitignore`。

---

## 4. 已知问题与开发陷阱

1. **单实例不传递参数**：已有实例在跑时，`--settings` 只会让新进程退出，不会唤起设置窗。排障需先 `Stop-Process -Name FluentUSB`。
2. **管理员权限会锁 `bin` 目录**：调试一律用普通权限启动，否则构建无法覆盖产物。
3. **动画必须静默降级**：所有 `Transitions` / 控件查找都包 `try/catch`；未注册动画器的属性直接补间会导致 UI 线程崩溃。
4. **Transitions 首帧可能被跳过**：先设置初始状态，`Dispatcher.UIThread.Post` 到下一帧再设终态（`IntroAnimation` 已按此实现）。
5. **新增窗口要接入外观广播**：在 `App.AppearanceWindows()` 里返回，否则主题/背景切换不会作用到它。
6. **设置页导航指示条**依赖 `NavLayer` 与按钮的实际位置（`TranslatePoint`），改布局后要重新验证对齐。
7. **字体体积**：两个字重约 15.7 MB；如需发布瘦身可做子集化，但注意保留中文常用字。
8. **参考源码**只用于查阅，已排除编译和版本管理，不要提交进仓库。

---

## 5. 接下来建议做什么

### P0（优先）

1. **主窗口继续向 ClassIsland 靠拢**：状态胶囊、容量进度条（需在 `UsbDriveInfo` 暴露已用/总容量并在 VM 增加属性）、列表卡片化/分组。
2. **Windows 10 实机验证**：Mica/亚克力 11 应自动禁用并有说明；核对 1024×768、125%/150% 缩放下不裁切（`FitToScreen`）。
3. **插入弹窗外观同步**：上一轮按用户要求未纳入整改，目前沿用旧版卡片样式；建议按设置卡片语言重做。
4. **回归验证切页动画**：快速来回切换「外观/关于」时，确认旧页不会被延迟任务误隐藏、指示条无残留。

### P1

5. **单实例参数转发**：用命名管道/窗口消息把 `--settings` 转发给已有实例，避免"启动没反应"。
6. **字体子集化**，减小安装包。
7. **设置项扩展**：开机自启、弹窗时长范围、托盘图标行为。

### P2

8. 多屏/高 DPI 下弹窗位置复验；无障碍（`AutomationProperties`）；本地化。

---

## 6. 开发与验证

```powershell
# 构建（当前基线：0 警告 0 错误）
dotnet build "d:\FluentUSB\FluentUSB.csproj" -c Debug

# 运行
& "d:\FluentUSB\bin\Debug\net10.0\FluentUSB.exe" --settings
& "d:\FluentUSB\bin\Debug\net10.0\FluentUSB.exe" --show

# 结束测试实例（避免锁定 bin / 单实例互斥）
Stop-Process -Name FluentUSB -Force
```

- 日志：`%AppData%\FluentUSB\logs\fluentusb-yyyyMMdd.log`
- 设置：`%AppData%\FluentUSB\settings.json`
- 真实设备验证：插入/拔出 U 盘观察弹窗、主窗口列表、托盘菜单。
