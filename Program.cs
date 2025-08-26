// TopLock — lock any window "Always on Top" on Windows
// -----------------------------------------------------
// Features:
// • Right-click a window’s title bar to get a tiny menu: Lock layer / Unlock layer
// • Global hotkey: Ctrl + Alt + T toggles Always-on-Top for the active window
// • Tray app with options: Toggle current window, Unlock all, Exit
// • Custom tray icon (uses embedded .ico or falls back to app EXE icon)
// • No admin required for normal apps; if your target window is elevated, run TopLock as admin.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplication());
    }
}

internal class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly TopmostManager _topmost = new();
    private readonly HotkeyWindow _hotkeys;
    private readonly MouseHook _mouseHook;

    // used by the title-bar popup menu
    private readonly ContextMenuStrip _titleBarMenu = new();
    private IntPtr _contextHwnd = IntPtr.Zero;

    public TrayApplication()
    {
        // Load custom icon from .ico file in same folder OR fallback to embedded EXE icon
        Icon trayIcon;
        try
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string iconPath = Path.Combine(exeDir, "toplock.ico");
            if (File.Exists(iconPath))
            {
                trayIcon = new Icon(iconPath);
            }
            else
            {
                trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _tray = new NotifyIcon
        {
            Icon = trayIcon,
            Visible = true,
            Text = "TopLock — Always on Top"
        };

        BuildTrayMenu();
        _tray.ContextMenuStrip = _trayMenu;

        // Build the tiny title-bar context menu once
        var toggleItem = new ToolStripMenuItem("Lock layer");
        toggleItem.Click += (_, __) => ToggleTarget(_contextHwnd);
        _titleBarMenu.Items.Add(toggleItem);
        _titleBarMenu.Items.Add(new ToolStripSeparator());
        var unlockAllItem = new ToolStripMenuItem("Unlock all");
        unlockAllItem.Click += (_, __) => _topmost.UnlockAll();
        _titleBarMenu.Items.Add(unlockAllItem);

        // Hotkeys
        _hotkeys = new HotkeyWindow();
        _hotkeys.RegisterToggleHotkey(() =>
        {
            var hwnd = Native.GetForegroundWindow();
            ToggleTarget(hwnd);
        });

        // Mouse hook to detect right-click on title bars
        _mouseHook = new MouseHook(OnRButtonUp);
    }

    private void BuildTrayMenu()
    {
        _trayMenu.Items.Clear();

        var toggleActive = new ToolStripMenuItem("Toggle current window (Ctrl+Alt+T)");
        toggleActive.Click += (_, __) =>
        {
            var hwnd = Native.GetForegroundWindow();
            ToggleTarget(hwnd);
        };

        var unlockAll = new ToolStripMenuItem("Unlock all");
        unlockAll.Click += (_, __) => _topmost.UnlockAll();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, __) =>
        {
            _mouseHook.Dispose();
            _hotkeys.Dispose();
            _tray.Visible = false;
            Application.ExitThread();
        };

        _trayMenu.Items.Add(toggleActive);
        _trayMenu.Items.Add(unlockAll);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exit);
    }

    private void ToggleTarget(IntPtr hwnd)
    {
        hwnd = _topmost.GetRoot(hwnd);
        if (hwnd == IntPtr.Zero) return;

        bool isTop = _topmost.IsTopMost(hwnd);
        if (isTop) _topmost.UnsetTopMost(hwnd);
        else _topmost.SetTopMost(hwnd);
    }

    // Called by mouse hook on RBUTTONUP anywhere
    private void OnRButtonUp(Point screenPt)
    {
        var hWndAtPoint = Native.WindowFromPoint(screenPt);
        var root = _topmost.GetRoot(hWndAtPoint);
        if (root == IntPtr.Zero) return;

        int hit = _topmost.HitTest(root, screenPt);
        if (hit == Native.HTCAPTION || hit == Native.HTSYSMENU)
        {
            _contextHwnd = root;

            // Swap menu label based on current state
            ((ToolStripMenuItem)_titleBarMenu.Items[0]).Text =
                _topmost.IsTopMost(root) ? "Unlock layer" : "Lock layer";

            _titleBarMenu.Show(screenPt);
        }
    }
}

internal sealed class TopmostManager
{
    private readonly HashSet<IntPtr> _locked = new();

    public IntPtr GetRoot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (!Native.IsWindow(root)) return IntPtr.Zero;
        return root;
    }

    public bool IsTopMost(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return false;
        IntPtr stylesPtr = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        long styles = stylesPtr.ToInt64();
        return (styles & Native.WS_EX_TOPMOST) != 0;
    }

    public void SetTopMost(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
            0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _locked.Add(hwnd);
    }

    public void UnsetTopMost(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return;
        Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST,
            0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _locked.Remove(hwnd);
    }

    public int UnlockAll()
    {
        int count = 0;
        foreach (var h in new List<IntPtr>(_locked))
        {
            if (Native.IsWindow(h))
            {
                UnsetTopMost(h);
                count++;
            }
        }
        _locked.Clear();
        return count;
    }

    public int HitTest(IntPtr hwnd, Point screenPt)
    {
        int x = screenPt.X;
        int y = screenPt.Y;
        IntPtr lParam = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        IntPtr res = Native.SendMessage(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lParam);
        return res.ToInt32();
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HOTKEY_ID = 1;
    private Action? _toggle;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    public void RegisterToggleHotkey(Action onToggle)
    {
        _toggle = onToggle;
        if (!Native.RegisterHotKey(this.Handle, HOTKEY_ID, Native.MOD_CONTROL | Native.MOD_ALT, (uint)Keys.T))
        {
            Debug.WriteLine("Failed to register hotkey. It may already be in use.");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            _toggle?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        try { Native.UnregisterHotKey(this.Handle, HOTKEY_ID); } catch { }
        DestroyHandle();
    }
}

internal sealed class MouseHook : IDisposable
{
    private readonly Native.LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private readonly Action<Point> _onRButtonUp;

    public MouseHook(Action<Point> onRButtonUp)
    {
        _onRButtonUp = onRButtonUp;
        _proc = HookCallback;
        _hookId = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(IntPtr.Zero), 0);
        if (_hookId == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to set mouse hook");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == Native.WM_RBUTTONUP)
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                _onRButtonUp?.Invoke(new Point(data.pt.x, data.pt.y));
            }
        }
        return Native.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}

internal static class Native
{
    public const int GA_ROOT = 2;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public const int GWL_EXSTYLE = -20;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_RBUTTONUP = 0x0205;

    public const int HTCAPTION = 2;
    public const int HTSYSMENU = 3;

    public const int WH_MOUSE_LL = 14;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const long WS_EX_TOPMOST = 0x00000008;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, int gaFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point p);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8) return GetWindowLongPtr64(hWnd, nIndex);
        return new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(IntPtr lpModuleName);
}

/* ---------------------------------------------------------
TopLock.csproj — replace your generated one with this:
---------------------------------------------------------
<Project Sdk="Microsoft.NET.Sdk.WindowsDesktop">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>toplock.ico</ApplicationIcon>
  </PropertyGroup>
</Project>
*/
