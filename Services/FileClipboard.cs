using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentUSB.Services;

/// <summary>
/// 与 Windows 剪贴板互通的文件剪贴板（CF_HDROP）。
/// 写入后系统资源管理器可直接粘贴；也会读取资源管理器复制/剪切进来的文件。
/// 所有调用都应发生在 UI（STA）线程。
/// </summary>
internal static class FileClipboard
{
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DropFiles
    {
        public uint pFiles;
        public int ptX;
        public int ptY;
        public int fNC;
        public int fWide;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>把文件路径写入系统剪贴板；cut 为 true 时标记为剪切（移动）。</summary>
    public static bool TrySetFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0)
            return false;

        try
        {
            if (!OpenClipboard(IntPtr.Zero))
                return false;

            try
            {
                if (!EmptyClipboard())
                    return false;

                if (!SetFileDrop(paths))
                    return false;

                SetDropEffect(cut ? DROPEFFECT_MOVE : DROPEFFECT_COPY);
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取系统剪贴板中的文件路径；cut 表示来源标记为剪切。</summary>
    public static List<string> GetFiles(out bool cut)
    {
        cut = false;
        var paths = new List<string>();

        try
        {
            if (!OpenClipboard(IntPtr.Zero))
                return paths;

            try
            {
                var handle = GetClipboardData(CF_HDROP);
                if (handle == IntPtr.Zero)
                    return paths;

                var count = DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFileW(handle, i, null, 0);
                    if (length == 0)
                        continue;

                    var buffer = new StringBuilder((int)length + 1);
                    if (DragQueryFileW(handle, i, buffer, (uint)buffer.Capacity) > 0)
                        paths.Add(buffer.ToString());
                }

                cut = ReadDropEffect();
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            // 剪贴板被其他程序占用时静默忽略
        }

        return paths;
    }

    /// <summary>清空系统剪贴板（剪切粘贴成功后调用，避免重复移动）。</summary>
    public static void Clear()
    {
        try
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                EmptyClipboard();
                CloseClipboard();
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static bool SetFileDrop(IReadOnlyList<string> paths)
    {
        var list = string.Join('\0', paths) + "\0\0";
        var bytes = Encoding.Unicode.GetBytes(list);
        var headerSize = Marshal.SizeOf<DropFiles>();
        var totalSize = headerSize + bytes.Length;

        var handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)(uint)totalSize);
        if (handle == IntPtr.Zero)
            return false;

        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        var header = new DropFiles { pFiles = (uint)headerSize, fWide = 1 };
        Marshal.StructureToPtr(header, target, false);
        Marshal.Copy(bytes, 0, target + headerSize, bytes.Length);
        GlobalUnlock(handle);

        if (SetClipboardData(CF_HDROP, handle) == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        // 所有权已交给剪贴板，不能释放
        return true;
    }

    private static void SetDropEffect(uint effect)
    {
        var format = RegisterClipboardFormatW("Preferred DropEffect");
        if (format == 0)
            return;

        var handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)sizeof(int));
        if (handle == IntPtr.Zero)
            return;

        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            return;
        }

        Marshal.WriteInt32(target, (int)effect);
        GlobalUnlock(handle);

        if (SetClipboardData(format, handle) == IntPtr.Zero)
            GlobalFree(handle);
    }

    private static bool ReadDropEffect()
    {
        var format = RegisterClipboardFormatW("Preferred DropEffect");
        if (format == 0)
            return false;

        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
            return false;

        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
            return false;

        var effect = (uint)Marshal.ReadInt32(target);
        GlobalUnlock(handle);

        return (effect & DROPEFFECT_MOVE) != 0;
    }
}
