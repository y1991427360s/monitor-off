using System;
using System.Runtime.InteropServices;

namespace MonitorOff;

internal static class Native
{
    // ---------- 基础 ----------
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cb; public RECT mon; public RECT work; public int flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dev;
    }

    public delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);

    // ---------- DisplayConfig ----------
    public const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
    public const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_APPLY = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public uint refreshNum, refreshDen; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO { public PATH_SOURCE source; public PATH_TARGET target; public uint flags; }

    // infoType: 1=源模式(含桌面坐标) 2=目标模式 3=桌面图像
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public uint srcWidth;
        [FieldOffset(20)] public uint srcHeight;
        [FieldOffset(24)] public uint srcPixelFormat;
        [FieldOffset(28)] public int srcX;
        [FieldOffset(32)] public int srcY;
    }
    public const uint MODE_IDX_INVALID = 0xFFFFFFFF;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    [DllImport("user32.dll")] public static extern int SetDisplayConfig(uint numPath, [In] PATH_INFO[] paths, uint numMode, [In] MODE_INFO[] modes, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header; public uint flags; public uint outputTechnology;
        public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName;
    }

    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths, ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME r);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME r);
    [DllImport("user32.dll")] public static extern int SetDisplayConfig(uint numPath, IntPtr paths, uint numMode, IntPtr modes, uint flags);

    // ---------- 旧版显示设置（断开/定位） ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    public const int DM_POSITION = 0x20, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;
    public const int CDS_UPDATEREGISTRY = 0x1, CDS_NORESET = 0x10000000;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string? dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string? dev, IntPtr dm, IntPtr hwnd, int flags, IntPtr lp);

    // ---------- DDC/CI ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }
    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
    [DllImport("dxva2.dll")] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint cur, out uint max);
    [DllImport("dxva2.dll")] public static extern bool SetVCPFeature(IntPtr h, byte code, uint val);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] a);

    // ---------- 输入、热键、钩子 ----------
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void mouse_event(int f, int dx, int dy, int d, IntPtr e);

    public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
}
