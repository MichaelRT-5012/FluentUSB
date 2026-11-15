using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentUSB.Models;

namespace FluentUSB.Services;

/// <summary>单个盘符的探测结果。</summary>
internal enum UsbProbeState
{
    /// <summary>盘符还没出现（刚插入时系统尚未完成挂载），可以稍后重试。</summary>
    NotPresent,

    /// <summary>盘符存在，但不是 USB 存储设备，直接忽略。</summary>
    NotUsb,

    /// <summary>是 USB 存储设备，但尚未就绪（如空读卡器），可以稍后重试。</summary>
    NotReady,

    /// <summary>已就绪，可读取容量与文件系统。</summary>
    Ready,
}

internal readonly record struct UsbProbeResult(UsbProbeState State, UsbDriveInfo? Drive)
{
    public static UsbProbeResult NotPresent => new(UsbProbeState.NotPresent, null);

    public static UsbProbeResult NotUsb => new(UsbProbeState.NotUsb, null);

    public static UsbProbeResult NotReady => new(UsbProbeState.NotReady, null);

    public static UsbProbeResult Ready(UsbDriveInfo drive) => new(UsbProbeState.Ready, drive);
}

/// <summary>
/// U 盘识别与信息快照。
/// 关键点：只靠 <see cref="DriveType.Removable"/> 会漏掉被系统识别为固定盘的 USB 固态盘/硬盘盒，
/// 因此对固定盘再向存储驱动查询底层总线类型，确认是 USB 才当作 U 盘处理，
/// 避免把机器内置硬盘误判为 U 盘。
/// </summary>
internal static class UsbDriveProbe
{
    /// <summary>STORAGE_BUS_TYPE 中的 BusTypeUsb。</summary>
    private const int BusTypeUsb = 0x07;

    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint StorageDeviceProperty = 0;
    private const uint PropertyStandardQuery = 0;
    private const uint OpenExisting = 3;
    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>探测指定盘符；会阻塞读取驱动器属性，调用方应放在后台线程。</summary>
    public static UsbProbeResult Probe(char letter)
    {
        try
        {
            var upper = char.ToUpperInvariant(letter);
            var drive = DriveInfo.GetDrives()
                .FirstOrDefault(d => char.ToUpperInvariant(d.Name[0]) == upper);

            if (drive is null)
                return UsbProbeResult.NotPresent;

            if (!IsUsbStorage(drive))
                return UsbProbeResult.NotUsb;

            // TryCreate 已处理 IsReady/TotalSize 检查与读取异常
            var snapshot = TryCreate(drive);
            return snapshot is null
                ? UsbProbeResult.NotReady
                : UsbProbeResult.Ready(snapshot);
        }
        catch (Exception)
        {
            // 枚举驱动器或判断总线类型期间被拔出会抛异常，按"未就绪"重试即可
            return UsbProbeResult.NotReady;
        }
    }

    /// <summary>枚举当前已在位的 USB 存储盘（仅返回已就绪的）。会阻塞，调用方应放在后台线程。</summary>
    public static UsbDriveInfo[] EnumerateReadyUsbDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(IsUsbStorage)
                .Select(TryCreate)
                .OfType<UsbDriveInfo>()
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举驱动器失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>判断一个驱动器是否属于 USB 存储设备。</summary>
    public static bool IsUsbStorage(DriveInfo drive)
    {
        if (drive.DriveType == DriveType.Removable)
            return true;

        // 固定盘里既有内置硬盘也有 USB 固态盘/硬盘盒，必须查底层总线类型
        if (drive.DriveType != DriveType.Fixed)
            return false;

        return GetBusType(drive.Name[0]) == BusTypeUsb;
    }

    /// <summary>读取单个驱动器信息；未就绪（如空读卡器）或读取失败时返回 null。</summary>
    public static UsbDriveInfo? TryCreate(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.TotalSize <= 0)
                return null;

            return CreateSnapshot(drive);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static UsbDriveInfo CreateSnapshot(DriveInfo drive) => new(
        DriveLetter: char.ToUpperInvariant(drive.Name[0]) + ":",
        RootPath: drive.RootDirectory.FullName,
        VolumeLabel: drive.VolumeLabel,
        DriveFormat: drive.DriveFormat,
        TotalSize: drive.TotalSize,
        FreeSpace: drive.AvailableFreeSpace);

    /// <summary>
    /// 通过 IOCTL_STORAGE_QUERY_PROPERTY 读取存储设备描述符中的 BusType。
    /// 以 0 访问权限打开卷句柄即可，无需管理员权限。
    /// </summary>
    private static int? GetBusType(char letter)
    {
        var path = $@"\\.\{char.ToUpperInvariant(letter)}:";
        var handle = CreateFileW(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == InvalidHandle)
            return null;

        try
        {
            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceProperty,
                QueryType = PropertyStandardQuery,
            };

            var buffer = new byte[1024];
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty,
                    ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                    buffer, buffer.Length, out var returned, IntPtr.Zero))
            {
                return null;
            }

            // STORAGE_DEVICE_DESCRIPTOR 中 BusType 位于偏移 28，且描述符长度至少 32 字节
            if (returned < 32)
                return null;

            return BitConverter.ToInt32(buffer, 28);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion
}
