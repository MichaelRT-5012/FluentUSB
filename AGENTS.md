# FluentUSB 项目交接说明

> 更新日期：2026-11-21　维护者交接文档，面向下一位接手的人 / Agent。当前提交以 `git log -1` 为准。

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
  InstanceBridge.cs        命名管道：第二个实例通知已有实例
  Log.cs                   文件日志
ViewModels/
  MainWindowViewModel.cs   已连接 U 盘列表、空状态、状态文案
  SettingsViewModel.cs     外观/弹窗设置、背景展开状态、重置
  UsbDriveViewModel.cs     单个 U 盘的展示数据
Views/
  MainWindow.axaml         主窗口（列表 / 空状态 / 设置入口）
  SettingsWindow.axaml     设置窗口（导航 + 外观页 + 关于页）
  UsbToastWindow.axaml     插入提示弹窗（卡片式 / 整窗式）
  InstanceConflictWindow.axaml  重复启动提示窗（重启/显示主窗口/打开设置/取消）
Assets/Fonts/              HarmonyOS Sans SC + LICENSE（许可证要求随应用分发）
```

### 关键机制

- **单实例**：`Program.cs` 用命名互斥；第二个实例通过命名管道（`InstanceBridge`）通知已有实例，由它弹出「已在运行」窗口（重新启动 / 显示主窗口 / 打开设置 / 取消），随后自身退出，任何情况下都只有一个实例。
- **重启**：新进程带 `--relaunch <pid>`，先等旧进程退出再接管互斥体，避免出现短暂双开。
- **窗口生命周期**：`ShutdownMode = OnExplicitShutdown`；关闭主窗口/设置窗只是隐藏或销毁，只有托盘「退出」才结束进程。
- **启动参数**：`--show` 打开主窗口，`--settings` 打开设置窗口，`--relaunch <pid>` 内部用于重启（排障/自动化用）。
- **U 盘识别**：除 `DriveType.Removable` 外，还通过存储总线类型识别 USB 固态/硬盘盒（历史坑：只看 Removable 会漏设备）。
- **插拔监听**：独立 STA 线程 + 隐藏顶层窗口接收 `WM_DEVICECHANGE`；插入后在后台轮询等待挂载完成，再回到 UI 线程发事件。
- **背景材质**：`BackdropService` 启动时探测系统能力（Solid / Transparent / Blur / Acrylic10 / Acrylic11 / Mica / Mica Alt），不支持的选项在设置页自动禁用并显示原因；所有窗口通过 `App.AppearanceWindows()` 统一刷新。
- **弹窗**：`UsbToastWindow` 支持「卡片式」与「整窗式」两种绘制方式；整窗式窗口 344×92、卡片式 360×108（可见卡片同为 344×92）；多个弹窗用 `SetSlot` 堆叠，悬停暂停倒计时。
- **设置页**：「背景效果」默认收起，点击标题行展开；行内说明保持一句话；关于页应用卡片连点 5 次显示构建时间。

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

- **全局**（`App.axaml`）：字体/字号层级、DodgerBlue、Button/ComboBox/RadioButton 的圆角与最小高度、卡片与分组样式。
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
- Fluent 的 `RadioButton.Padding` **不会移动单选圆点**，只会移动内容（模板中圆点固定占 20px 列）→ 选项行用 `Margin=16` + `Padding=20,10`，让圆点落在图标列、文字与标题列对齐。
- 扩展客户区（`ExtendClientAreaToDecorationsHint`）在 Avalonia 12 下会导致原生标题文字与自绘文字重叠、**窗口无法拖动**（`ExtendClientAreaChromeHints` 已被移除）→ 已回退为原生标题栏，应用标识下沉到侧栏。
- `ClassIsland-master/`、`ClassIsland-2.1.0.1/` 放在仓库内会被 SDK 当源码编译（曾报 2434 个错误）→ `FluentUSB.csproj` 中排除，并加入 `.gitignore`。

### 3.4 2026-11-21 后续调整

- **文字精简**：删除主窗口空状态副文案与底部提示、设置页系统能力说明条、关于页应用描述与「应用信息」分组标题；设置行说明改为短句；删除侧栏「设置」副标题。
- **背景效果默认收起**：标题行改为整行按钮（带旋转箭头），由 `IsBackdropExpanded` 控制展开；「弹窗背景」仍随整窗式显示。
- **关于页**：应用卡片改为可点击按钮卡片（悬停/按下反馈），连点 5 次显示构建时间；设置文件/日志目录各加「打开」按钮。
- **切页动画**：修复切换分类先闪现一帧的问题——新页同步复位子项、旧页 150ms 淡出；`IntroAnimation` 复位前先摘除过渡，避免从终态“倒放”。
- **背景选项对齐**：圆点移到图标列、文字与标题列对齐（16px 圆点列 + 20px 内边距），选中/悬停改为整行高亮。
- **整窗式弹窗**：窗口缩到 344×92，与卡片式可见尺寸一致；切换绘制方式时重新贴边。
- **重复启动**：命名管道通知已有实例，弹出四选项提示窗；「重新启动」用 `--relaunch <pid>` 等待旧进程退出。

---

## 4. 已知问题与开发陷阱

1. **管理员权限会锁 `bin` 目录**：调试一律用普通权限启动，否则构建无法覆盖产物。
2. **动画必须静默降级**：所有 `Transitions` / 控件查找都包 `try/catch`；未注册动画器的属性直接补间会导致 UI 线程崩溃。
3. **入场动画必须先复位再挂过渡**：`IntroAnimation` 复位子项前会把 `Transitions` 置空；切页动画必须同步复位，用 `Dispatcher.Post` 晚一帧会先闪现完整内容。
4. **新增窗口要接入外观广播**：在 `App.AppearanceWindows()` 里返回（`InstanceConflictWindow` 已接入），否则主题/背景切换不会作用到它。
5. **设置页导航指示条**依赖 `NavLayer` 与按钮的实际位置（`TranslatePoint`），改布局后要重新验证对齐。
6. **字体体积**：两个字重约 15.7 MB；如需发布瘦身可做子集化，但注意保留中文常用字。
7. **参考源码**只用于查阅，已排除编译和版本管理，不要提交进仓库。

---

## 5. 接下来建议做什么

### P0（优先）

1. **主窗口继续向 ClassIsland 靠拢**：状态胶囊、容量进度条（需在 `UsbDriveInfo` 暴露已用/总容量并在 VM 增加属性）、列表卡片化/分组。
2. **Windows 10 实机验证**：Mica/亚克力 11 应自动禁用并有说明；核对 1024×768、125%/150% 缩放下不裁切（`FitToScreen`）；确认整窗式弹窗在 Win10 无系统圆角时的观感。
3. **插入弹窗外观同步**：目前仍是旧版卡片样式（只调整过尺寸），建议按设置卡片语言重做。
4. **回归验证切页动画**：快速来回切换「外观/关于」时，确认不再闪现、旧页不会被延迟任务误隐藏、指示条无残留。

### P1

5. **字体子集化**，减小安装包。
6. **设置项扩展**：开机自启、弹窗时长范围、托盘图标行为。
7. **新控件无障碍**：背景效果标题行、关于页应用卡片、目录「打开」按钮的键盘操作与 `AutomationProperties`。

### P2

8. 多屏/高 DPI 下弹窗位置复验；本地化。

---

## 6. 开发与验证

```powershell
# 构建（当前基线：0 警告 0 错误）
dotnet build "d:\FluentUSB\FluentUSB.csproj" -c Debug

# 运行
& "d:\FluentUSB\bin\Debug\net10.0\FluentUSB.exe" --settings
& "d:\FluentUSB\bin\Debug\net10.0\FluentUSB.exe" --show

# 重复启动测试：已有实例会弹出「已在运行」窗口（命名管道通知）
& "d:\FluentUSB\bin\Debug\net10.0\FluentUSB.exe"

# 结束测试实例（避免锁定 bin / 单实例互斥）
Stop-Process -Name FluentUSB -Force
```

- 日志：`%AppData%\FluentUSB\logs\fluentusb-yyyyMMdd.log`
- 设置：`%AppData%\FluentUSB\settings.json`
- 真实设备验证：插入/拔出 U 盘观察弹窗、主窗口列表、托盘菜单。
