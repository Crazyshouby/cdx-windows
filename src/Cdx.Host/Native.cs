using System.Runtime.InteropServices;
using System.Text;

namespace Cdx;

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
struct APPBARDATA
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential)]
struct MEMORYSTATUSEX
{
    public uint dwLength, dwMemoryLoad;
    public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }

static class N
{
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

    public delegate bool EnumProc(IntPtr h, IntPtr l);

    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    public const uint ABE_BOTTOM = 3;
    public const int ABS_AUTOHIDE = 1;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref RECT r, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }

    public static string ExePathOf(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        IntPtr p = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (p == IntPtr.Zero) return null;
        try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(p, 0, sb, ref n) ? sb.ToString() : null; }
        finally { CloseHandle(p); }
    }
}
