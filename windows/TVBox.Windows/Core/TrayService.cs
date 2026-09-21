using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TVBoxForWindows.Core;

/// <summary>Native notification-area integration without pulling WinForms into the WinUI XAML build.</summary>
public sealed class TrayService : IDisposable
{
    const uint WM_APP = 0x8000;
    const uint WM_CLOSE = 0x0010;
    const uint WM_COMMAND = 0x0111;
    const uint WM_LBUTTONDBLCLK = 0x0203;
    const uint WM_RBUTTONUP = 0x0205;
    const uint NIF_MESSAGE = 0x00000001;
    const uint NIF_ICON = 0x00000002;
    const uint NIF_TIP = 0x00000004;
    const uint NIM_ADD = 0x00000000;
    const uint NIM_MODIFY = 0x00000001;
    const uint NIM_DELETE = 0x00000002;
    const uint IMAGE_ICON = 1;
    const uint LR_LOADFROMFILE = 0x00000010;
    const uint LR_DEFAULTSIZE = 0x00000040;
    const uint TPM_RETURNCMD = 0x00000100;
    const uint TPM_NONOTIFY = 0x00000080;
    const uint MF_STRING = 0x00000000;
    const uint MF_SEPARATOR = 0x00000800;
    const int ShowCommand = 1;
    const int ExitCommand = 2;

    readonly TVBoxForWindows.MainWindow _window;
    readonly ManualResetEventSlim _ready = new();
    readonly WndProc _wndProc;
    Thread _thread;
    IntPtr _windowHandle;
    IntPtr _iconHandle;
    string _className;
    bool _available;
    bool _iconVisible;
    bool _disposed;

    public event EventHandler ExitRequested;

    public TrayService(TVBoxForWindows.MainWindow window)
    {
        _window = window;
        _wndProc = WindowProc;
    }

    public bool Initialize()
    {
        if (_disposed || _available) return _available;
        try
        {
            var iconPath = Path.Combine(AppPaths.IconDir, "icon.ico");
            if (!File.Exists(iconPath)) return false;
            _iconHandle = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (_iconHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _className = $"TVBoxTray_{Environment.ProcessId}_{Guid.NewGuid():N}";
            _thread = new Thread(TrayThread) { IsBackground = true, Name = "TVBox tray" };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(2)) || !_available)
            {
                Dispose();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.E("Tray", ex.Message);
            Dispose();
            return false;
        }
    }

    public bool HideWindow()
    {
        if (_disposed || !_available) return false;
        try
        {
            if (!_iconVisible) _iconVisible = UpdateIcon(NIM_ADD);
            else UpdateIcon(NIM_MODIFY);
            _window.AppWindow.Hide();
            return true;
        }
        catch (Exception ex)
        {
            Logger.E("Tray", ex.Message);
            return false;
        }
    }

    void ShowWindow()
    {
        if (_disposed || !_available) return;
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _window.AppWindow.Show();
                _window.Activate();
            }
            catch (Exception ex) { Logger.E("Tray", ex.Message); }
        });
    }

    void TrayThread()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var windowClass = new WNDCLASS { lpfnWndProc = _wndProc, hInstance = instance, lpszClassName = _className };
            if (RegisterClass(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _windowHandle = CreateWindowEx(0, _className, "TVBox", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_windowHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _available = UpdateIcon(NIM_ADD);
            _iconVisible = _available;
            _ready.Set();
            if (!_available) return;
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            Logger.E("Tray", ex.Message);
            _available = false;
            _ready.Set();
        }
        finally
        {
            if (_windowHandle != IntPtr.Zero) DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
            if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
            if (!string.IsNullOrEmpty(_className)) UnregisterClass(_className, GetModuleHandle(null));
        }
    }

    bool UpdateIcon(uint operation)
    {
        if (_windowHandle == IntPtr.Zero || _iconHandle == IntPtr.Zero) return false;
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP + 1,
            hIcon = _iconHandle,
            szTip = "TVBox for Windows"
        };
        return Shell_NotifyIcon(operation, ref data);
    }

    IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_APP + 1)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage == WM_LBUTTONDBLCLK) ShowWindow();
            else if (mouseMessage == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (message == WM_COMMAND)
        {
            switch (unchecked((int)(wParam.ToInt64() & 0xffff)))
            {
                case ShowCommand: ShowWindow(); break;
                case ExitCommand: ExitRequested?.Invoke(this, EventArgs.Empty); break;
            }
            return IntPtr.Zero;
        }
        if (message == WM_CLOSE)
        {
            if (_iconVisible)
            {
                UpdateIcon(NIM_DELETE);
                _iconVisible = false;
            }
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        AppendMenu(menu, MF_STRING, (nuint)ShowCommand, "显示 TVBox");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, (nuint)ExitCommand, "退出软件");
        GetCursorPos(out var point);
        SetForegroundWindow(_windowHandle);
        var command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY, point.X, point.Y, 0, _windowHandle, IntPtr.Zero);
        if (command == (uint)ShowCommand) ShowWindow();
        else if (command == (uint)ExitCommand) ExitRequested?.Invoke(this, EventArgs.Empty);
        DestroyMenu(menu);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_windowHandle != IntPtr.Zero) PostMessage(_windowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1000);
        }
        catch (Exception ex) { Logger.E("Tray", ex.Message); }
        finally { _ready.Dispose(); }
    }

    delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClass([In] ref WNDCLASS windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint loadFlags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool DestroyIcon(IntPtr icon);
}
