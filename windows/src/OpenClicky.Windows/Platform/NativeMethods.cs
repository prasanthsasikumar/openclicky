using System.Runtime.InteropServices;
using System.Text;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// Every Win32 P/Invoke the platform layer uses, in one place. Structs are laid out for both x64
/// and ARM64 (and x86): <see cref="INPUT"/> is 40 bytes on 64-bit, its union starting at offset 8.
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- constants

    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_QUIT = 0x0012;
    public const int WM_CLOSE = 0x0010;
    public const int WM_DESTROY = 0x0002;
    public const int WM_USER = 0x0400;
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_ALTDOWN = 0x20;
    public const uint LLKHF_UP = 0x80;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint MAPVK_VK_TO_VSC = 0;

    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_METAFILEPICT = 3;
    public const uint CF_PALETTE = 9;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_ENHMETAFILE = 14;
    public const uint CF_HDROP = 15;
    public const uint CF_OWNERDISPLAY = 0x0080;
    public const uint CF_DSPTEXT = 0x0081;
    public const uint CF_DSPBITMAP = 0x0082;
    public const uint CF_DSPMETAFILEPICT = 0x0083;
    public const uint CF_DSPENHMETAFILE = 0x008E;
    public const uint CF_GDIOBJFIRST = 0x0300;
    public const uint CF_GDIOBJLAST = 0x03FF;
    public const uint GMEM_MOVEABLE = 0x0002;

    public static readonly nint HWND_MESSAGE = -3;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_OWNER = 4;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const int SHIL_LARGE = 0;
    public const int SHIL_SMALL = 1;
    public const int SHIL_EXTRALARGE = 2;
    public const int SHIL_JUMBO = 4;
    public const int ILD_TRANSPARENT = 0x00000001;

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // ---------------------------------------------------------------- structs

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    /// <summary>type (4 bytes) + padding to pointer alignment + the union (the MOUSEINPUT is the largest member).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;

        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    // ---------------------------------------------------------------- delegates

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);
    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);
    public delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    // ---------------------------------------------------------------- user32: hooks, messages, input

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // ---------------------------------------------------------------- user32: clipboard

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(nint hwnd);

    // ---------------------------------------------------------------- user32: windows

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(nint hWnd, out uint pdwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    // ---------------------------------------------------------------- user32: monitors, cursor, DCs

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    // ---------------------------------------------------------------- gdi32

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(nint hdc);

    // ---------------------------------------------------------------- kernel32

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nuint GlobalSize(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalFree(nint hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    // ---------------------------------------------------------------- advapi32

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    // ---------------------------------------------------------------- dwmapi, shcore

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---------------------------------------------------------------- shell32, comctl32

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, out nint ppv);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern nint ImageList_GetIcon(nint himl, int i, int flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    public static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    public static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid FOLDERID_Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    public static readonly Guid FOLDERID_Profile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");

    // ---------------------------------------------------------------- helpers

    /// <summary>The pid that owns a window (0 when it cannot be told).</summary>
    public static int ProcessIdOf(nint windowHandle)
    {
        GetWindowThreadProcessId(windowHandle, out var processId);
        return (int)processId;
    }

    public static string WindowTitle(nint windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        GetWindowText(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    public static string WindowClassName(nint windowHandle)
    {
        var builder = new StringBuilder(256);
        GetClassName(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    /// <summary>The full image path of a process; works for elevated processes too (PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    public static string? ProcessImagePath(int processId)
    {
        var processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (processHandle == 0) return null;
        try
        {
            var capacity = 1024u;
            var builder = new StringBuilder((int)capacity);
            return QueryFullProcessImageName(processHandle, 0, builder, ref capacity) ? builder.ToString() : null;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    /// <summary>Whether a process runs elevated (UIPI then drops input this unelevated process sends it). Null when it cannot be told.</summary>
    public static bool? IsProcessElevated(int processId)
    {
        var processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (processHandle == 0) return null;
        try
        {
            if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var tokenHandle)) return null;
            try
            {
                return GetTokenInformation(tokenHandle, TokenElevation, out var elevation, sizeof(uint), out _) ? elevation != 0 : null;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS (the visible frame), falling back to GetWindowRect.</summary>
    public static RECT VisibleFrame(nint windowHandle)
    {
        if (DwmGetWindowAttribute(windowHandle, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0) return frame;
        GetWindowRect(windowHandle, out frame);
        return frame;
    }

    public static bool IsCloaked(nint windowHandle) =>
        DwmGetWindowAttribute(windowHandle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static string? KnownFolder(Guid folderId)
    {
        if (SHGetKnownFolderPath(ref folderId, 0, 0, out var pathPointer) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(pathPointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    public static INPUT KeyboardInput(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = scanCode,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = (nint)Core.Platform.LiveTypingMarker.Value,
            },
        },
    };

    /// <summary>A marked key-down or key-up for a virtual key, with its scan code and the extended flag where the key needs it.</summary>
    public static INPUT VirtualKeyInput(int virtualKey, bool keyUp)
    {
        var flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;
        var scanCode = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
        return KeyboardInput((ushort)virtualKey, scanCode, flags);
    }

    public static bool IsExtendedKey(int virtualKey) => virtualKey is
        0xA3 /* RCONTROL */ or 0xA5 /* RMENU */ or 0x5B or 0x5C /* WIN */ or 0x2D or 0x2E /* INSERT DELETE */
        or 0x21 or 0x22 or 0x23 or 0x24 /* PGUP PGDN END HOME */ or 0x25 or 0x26 or 0x27 or 0x28 /* arrows */
        or 0x90 /* NUMLOCK */ or 0x2C /* SNAPSHOT */ or 0x6F /* DIVIDE */
        or 0xAD or 0xAE or 0xAF or 0xB0 or 0xB1 or 0xB2 or 0xB3 /* volume + media */;

    public static uint Send(params INPUT[] inputs) =>
        inputs.Length == 0 ? 0 : SendInput((uint)inputs.Length, inputs, INPUT.Size);
}
