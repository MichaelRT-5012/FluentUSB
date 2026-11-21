using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using FluentUSB.Models;

namespace FluentUSB.Services;

/// <summary>
/// U 盘插拔监听器（Windows）。
/// 原理：在独立线程上创建一个隐藏的顶层窗口接收系统 WM_DEVICECHANGE 广播，
/// 比轮询 DriveInfo 或 WMI 事件更及时，插入瞬间即可收到通知。
/// （Avalonia 12 未开放窗口过程重写，故直接使用 Win32 消息窗口。）
/// 所有事件回调都投递到 UI 线程；耗时的盘符探测在后台线程完成，避免卡界面。
/// </summary>
public sealed class UsbDeviceMonitor : IDisposable
{
    private const uint WM_DEVICECHANGE = 0x0219;
    private const uint WM_CLOSE = 0x0010;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const uint DBT_DEVTYP_VOLUME = 0x0002;

    private const int ProbeAttempts = 50;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(100);

    private readonly HashSet<char> _activeDrives = [];                        // 当前在位的 USB 盘（仅 UI 线程访问）
    private readonly Dictionary<char, CancellationTokenSource> _pendingDrives = []; // 已到达、等待挂载完成（仅 UI 线程访问）
    private readonly ManualResetEventSlim _windowReady = new(false);

    private Thread? _messageThread;
    private WndProcDelegate? _wndProcDelegate; // 持有引用防止被 GC 回收
    private GCHandle _wndProcHandle;
    private IntPtr _hwnd;
    private bool _disposed;

    /// <summary>U 盘插入并挂载完成（UI 线程回调），参数为盘符信息。</summary>
    public event Action<UsbDriveInfo>? DriveArrived;

    /// <summary>U 盘被移除（UI 线程回调），参数为盘符，如 "D:"。</summary>
    public event Action<string>? DriveRemoved;

    public void Start()
    {
        if (_messageThread is not null)
            return;

        // 启动时已在位的盘只记录、不弹窗；后台探测避免启动卡界面
        _ = SeedExistingDrivesAsync();

        _messageThread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "FluentUSB UsbDeviceMonitor",
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var cts in _pendingDrives.Values)
            cts.Cancel();
        _pendingDrives.Clear();

        // 通知消息循环退出；窗口在所属线程内销毁
        if (_windowReady.Wait(TimeSpan.FromSeconds(1)) && _hwnd != IntPtr.Zero)
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        if (_messageThread is { } thread && !thread.Join(TimeSpan.FromSeconds(1)))
            Log.Warn("U 盘监听线程未在 1 秒内退出");

        if (_wndProcHandle.IsAllocated)
            _wndProcHandle.Free();
    }

    private async Task SeedExistingDrivesAsync()
    {
        var drives = await Task.Run(UsbDriveProbe.EnumerateReadyUsbDrives);
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var drive in drives)
                _activeDrives.Add(char.ToUpperInvariant(drive.DriveLetter[0]));
        });
    }

    /// <summary>独立线程的消息循环：只为隐藏窗口派发系统广播。</summary>
    private void MessageLoop()
    {
        try
        {
            _wndProcDelegate = WndProc;
            _wndProcHandle = GCHandle.Alloc(_wndProcDelegate);

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "FluentUSB_UsbMonitor_" + Guid.NewGuid().ToString("N"),
            };

            if (RegisterClassExW(ref wc) == 0)
            {
                Log.Error($"注册设备监听窗口类失败，Win32 错误码 {Marshal.GetLastWin32Error()}，U 盘监听不可用");
                return;
            }

            // 不带 WS_VISIBLE 的顶层窗口：不可见，但能收到 WM_DEVICECHANGE 广播
            _hwnd = CreateWindowExW(0, wc.lpszClassName, "", 0, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Error($"创建设备监听窗口失败，Win32 错误码 {Marshal.GetLastWin32Error()}，U 盘监听不可用");
                return;
            }

            _windowReady.Set();

            int result;
            while ((result = GetMessageW(out var msg, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            if (result < 0)
                Log.Warn("U 盘监听消息循环异常退出");

            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            // 后台线程异常不能让它直接结束进程
            Log.Error("U 盘监听线程异常退出", ex);
        }
        finally
        {
            // 让 Dispose 不必一直等到超时
            _windowReady.Set();
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_DEVICECHANGE when lParam != IntPtr.Zero:
                    // lParam 指向的内存仅在消息处理期间有效，须当场解析，
                    // 只把事件类型与盘符掩码投递到 UI 线程
                    var volume = Marshal.PtrToStructure<DevBroadcastVolume>(lParam);
                    if (volume.DeviceType == DBT_DEVTYP_VOLUME)
                    {
                        var deviceEvent = (int)wParam;
                        var unitMask = volume.UnitMask;
                        Dispatcher.UIThread.Post(() => OnVolumeEvent(deviceEvent, unitMask));
                    }
                    break;

                case WM_CLOSE:
                    PostQuitMessage(0);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("处理设备消息失败", ex);
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnVolumeEvent(int deviceEvent, uint unitMask)
    {
        foreach (var letter in LettersFromMask(unitMask))
        {
            switch (deviceEvent)
            {
                case DBT_DEVICEARRIVAL:
                    OnVolumeArrival(letter);
                    break;
                case DBT_DEVICEREMOVECOMPLETE:
                    OnVolumeRemoval(letter);
                    break;
            }
        }
    }

    private void OnVolumeArrival(char letter)
    {
        if (_activeDrives.Contains(letter) || _pendingDrives.ContainsKey(letter))
            return;

        var cts = new CancellationTokenSource();
        _pendingDrives[letter] = cts;
        _ = WaitForReadyAsync(letter, cts, cts.Token);
    }

    private void OnVolumeRemoval(char letter)
    {
        // 拔出即取消尚未完成的就绪等待，避免把已拔出的盘重新加入列表
        if (_pendingDrives.Remove(letter, out var cts))
            cts.Cancel();

        if (_activeDrives.Remove(letter))
            Log.Info($"U 盘已拔出 {letter}:");

        // 即使监听器没有记录该盘（例如启动枚举尚未完成），也通知移除，
        // 让主窗口按盘符清理，避免残留已经拔出的盘
        DriveRemoved?.Invoke(letter + ":");
    }

    /// <summary>
    /// 卷插入后需要一小段时间完成挂载，轮询直到就绪（最长 5 秒）再通知。
    /// 探测本身在后台线程执行，拔出会立即取消。
    /// </summary>
    private async Task WaitForReadyAsync(char letter, CancellationTokenSource cts, CancellationToken token)
    {
        try
        {
            for (var i = 0; i < ProbeAttempts; i++)
            {
                var result = await Task.Run(() => UsbDriveProbe.Probe(letter), token);
                token.ThrowIfCancellationRequested();

                switch (result.State)
                {
                    case UsbProbeState.NotUsb:
                        return;

                    case UsbProbeState.Ready when result.Drive is { } drive:
                        if (_activeDrives.Add(letter))
                        {
                            Log.Info($"U 盘已插入 {drive.DriveLetter} {drive.DisplayName}（{drive.SizeSummary}）");
                            DriveArrived?.Invoke(drive);
                        }
                        return;
                }

                await Task.Delay(ProbeInterval, token);
            }

            Log.Warn($"盘符 {letter}: 在 5 秒内未就绪，放弃通知");
        }
        catch (OperationCanceledException)
        {
            // 拔出或退出导致的取消属于正常路径
        }
        catch (Exception ex)
        {
            Log.Error($"等待盘符 {letter}: 就绪失败", ex);
        }
        finally
        {
            if (_pendingDrives.TryGetValue(letter, out var current) && current == cts)
                _pendingDrives.Remove(letter);
            cts.Dispose();
        }
    }

    /// <summary>将 unitmask 的每一位转换为盘符（位 0 = A:）。</summary>
    private static IEnumerable<char> LettersFromMask(uint unitMask)
    {
        for (var i = 0; i < 26; i++)
        {
            if ((unitMask & (1u << i)) != 0)
                yield return (char)('A' + i);
        }
    }

    #region Win32 P/Invoke

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    /// <summary>DEV_BROADCAST_VOLUME 结构（Windows 卷设备广播参数）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastVolume
    {
        public uint Size;
        public uint DeviceType;
        public uint Reserved;
        public uint UnitMask;
        public ushort Flags;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    #endregion
}
