using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// </summary>
public sealed class UsbDeviceMonitor : IDisposable
{
    private const uint WM_DEVICECHANGE = 0x0219;
    private const uint WM_CLOSE = 0x0010;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const uint DBT_DEVTYP_VOLUME = 0x0002;

    private readonly HashSet<char> _activeDrives = [];   // 当前在位的可移动盘（仅 UI 线程访问）
    private readonly HashSet<char> _pendingDrives = [];  // 已到达、等待挂载完成（仅 UI 线程访问）
    private readonly ManualResetEventSlim _windowReady = new(false);

    private Thread? _messageThread;
    private WndProcDelegate? _wndProcDelegate; // 持有引用防止被 GC 回收
    private GCHandle _wndProcHandle;
    private IntPtr _hwnd;

    /// <summary>U 盘插入并挂载完成（UI 线程回调），参数为盘符信息。</summary>
    public event Action<UsbDriveInfo>? DriveArrived;

    /// <summary>U 盘被移除（UI 线程回调），参数为盘符，如 "D:"。</summary>
    public event Action<string>? DriveRemoved;

    public void Start()
    {
        // 记录启动时已在位的可移动盘：只在"插入"时通知，启动即存在的盘不弹窗
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable))
            _activeDrives.Add(char.ToUpperInvariant(drive.Name[0]));

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
        // 通知消息循环退出；窗口在所属线程内销毁
        if (_windowReady.Wait(TimeSpan.FromSeconds(1)) && _hwnd != IntPtr.Zero)
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _messageThread?.Join(TimeSpan.FromSeconds(1));
        if (_wndProcHandle.IsAllocated)
            _wndProcHandle.Free();
    }

    /// <summary>独立线程的消息循环：只为隐藏窗口派发系统广播。</summary>
    private void MessageLoop()
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
            throw new InvalidOperationException("注册设备监听窗口类失败");

        // 不带 WS_VISIBLE 的顶层窗口：不可见，但能收到 WM_DEVICECHANGE 广播
        _hwnd = CreateWindowExW(0, wc.lpszClassName, "", 0, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建设备监听窗口失败");

        _windowReady.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
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
        if (_activeDrives.Contains(letter) || _pendingDrives.Contains(letter)) return;
        _pendingDrives.Add(letter);
        _ = WaitForReadyAsync(letter);
    }

    private void OnVolumeRemoval(char letter)
    {
        _pendingDrives.Remove(letter);
        if (_activeDrives.Remove(letter))
            DriveRemoved?.Invoke(letter + ":");
    }

    /// <summary>
    /// 卷插入后需要一小段时间完成挂载，轮询直到可读（最长 5 秒）再通知。
    /// 注意：个别 U 盘（如部分固态 U 盘）会被系统识别为 DriveType.Fixed，
    /// M1 只处理可移动磁盘，此类盘不会触发通知。
    /// </summary>
    private async Task WaitForReadyAsync(char letter)
    {
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var drive = DriveInfo.GetDrives()
                    .FirstOrDefault(d => char.ToUpperInvariant(d.Name[0]) == letter);

                // 不是可移动盘（如虚拟磁盘挂载），放弃
                if (drive is not null && drive.DriveType != DriveType.Removable)
                    return;

                if (drive is { IsReady: true, TotalSize: > 0 })
                {
                    if (_activeDrives.Add(letter))
                    {
                        DriveArrived?.Invoke(new UsbDriveInfo(
                            DriveLetter: letter + ":",
                            RootPath: drive.RootDirectory.FullName,
                            VolumeLabel: drive.VolumeLabel,
                            DriveFormat: drive.DriveFormat,
                            TotalSize: drive.TotalSize,
                            FreeSpace: drive.AvailableFreeSpace));
                    }
                    return;
                }

                await Task.Delay(100);
            }
        }
        catch (Exception)
        {
            // 个别状态下枚举驱动器可能抛出 IO 异常，放弃本次通知即可
        }
        finally
        {
            _pendingDrives.Remove(letter);
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
    private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

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
