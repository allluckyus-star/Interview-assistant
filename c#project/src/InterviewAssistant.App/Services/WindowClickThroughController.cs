using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Click-through: mouse passes to windows behind this overlay. Uses <c>WS_EX_TRANSPARENT</c> on the
/// root HWND and all descendant HWNDs (WebView2 is nested). Top chrome stays clickable via a cursor
/// poll that temporarily clears transparency on the root. Window stays <c>Topmost</c> and visible;
/// only mouse input passes through the content area.
/// </summary>
public sealed class WindowClickThroughController : IDisposable
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;

    private readonly Window _window;
    private readonly Func<FrameworkElement?> _topChromeProvider;
    private readonly Action<bool>? _setWpfContentHitTestVisible;
    private readonly Dictionary<IntPtr, long> _savedExStyles = new();
    private readonly DispatcherTimer _cursorTimer;
    private HwndSource? _source;
    private bool _enabled;
    private bool _rootPassesMouse;
    private double _topChromeBottomScreenY;
    private Rect _topChromeScreenRect;

    public WindowClickThroughController(
        Window window,
        Func<FrameworkElement?> topChromeProvider,
        Action<bool>? setWpfContentHitTestVisible = null)
    {
        _window = window;
        _topChromeProvider = topChromeProvider;
        _setWpfContentHitTestVisible = setWpfContentHitTestVisible;
        _cursorTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _cursorTimer.Tick += (_, _) => SyncFromCursorPosition();
        _window.SourceInitialized += OnWindowSourceInitialized;
        _window.SizeChanged += OnWindowLayoutChanged;
        _window.LocationChanged += OnWindowLayoutChanged;
        if (_window.IsLoaded)
            AttachHook();
    }

    public bool IsEnabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        UpdateTopChromeBounds();

        if (_enabled)
        {
            _setWpfContentHitTestVisible?.Invoke(false);
            _cursorTimer.Start();
            SyncFromCursorPosition();
            ApplyClickThroughToDescendantHwnds();
        }
        else
        {
            _cursorTimer.Stop();
            SetRootPassesMouse(false);
            RestoreAllHwnds();
            _setWpfContentHitTestVisible?.Invoke(true);
        }
    }

    public void RefreshChildHwnds()
    {
        if (!_enabled)
            return;

        UpdateTopChromeBounds();
        ApplyClickThroughToDescendantHwnds();
        SyncFromCursorPosition();
    }

    public void UpdateTopChromeBounds()
    {
        if (!_window.IsLoaded)
            return;

        var chrome = _topChromeProvider();
        if (chrome is null || chrome.ActualHeight <= 0)
            return;

        try
        {
            var topLeft = chrome.PointToScreen(new Point(0, 0));
            _topChromeScreenRect = new Rect(
                topLeft.X,
                topLeft.Y,
                chrome.ActualWidth,
                chrome.ActualHeight);
            _topChromeBottomScreenY = topLeft.Y + chrome.ActualHeight + 2;
        }
        catch
        {
            // visual tree not ready
        }
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e) => AttachHook();

    private void OnWindowLayoutChanged(object? sender, EventArgs e)
    {
        UpdateTopChromeBounds();
        if (_enabled)
        {
            ApplyClickThroughToDescendantHwnds();
            SyncFromCursorPosition();
        }
    }

    private void AttachHook()
    {
        if (_source is not null)
            return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        _source = HwndSource.FromHwnd(hwnd);
        UpdateTopChromeBounds();
    }

    private void SyncFromCursorPosition()
    {
        if (!_enabled)
            return;

        UpdateTopChromeBounds();

        if (!GetCursorPos(out var pt))
            return;

        var inChrome = _topChromeScreenRect.Contains(new Point(pt.X, pt.Y))
            || pt.Y <= _topChromeBottomScreenY;

        SetRootPassesMouse(!inChrome);
    }

    private void SetRootPassesMouse(bool passThrough)
    {
        if (_rootPassesMouse == passThrough)
            return;

        _rootPassesMouse = passThrough;
        var root = new WindowInteropHelper(_window).Handle;
        if (root == IntPtr.Zero)
            return;

        SetHwndClickThrough(root, passThrough);
    }

    private void ApplyClickThroughToDescendantHwnds()
    {
        var root = new WindowInteropHelper(_window).Handle;
        if (root == IntPtr.Zero)
            return;

        UpdateTopChromeBounds();
        var chromeBottom = _topChromeBottomScreenY;
        if (chromeBottom <= 0)
            chromeBottom = 72;

        EnumDescendantHwnds(root, hwnd =>
        {
            if (hwnd == root)
                return;

            if (!GetWindowRect(hwnd, out var rect))
                return;

            var centerY = (rect.Top + rect.Bottom) / 2.0;
            if (centerY <= chromeBottom)
                return;

            SetHwndClickThrough(hwnd, true);
        });
    }

    private static void EnumDescendantHwnds(IntPtr parent, Action<IntPtr> visit)
    {
        EnumChildWindows(
            parent,
            (hwnd, _) =>
            {
                if (hwnd == IntPtr.Zero)
                    return true;

                visit(hwnd);
                EnumDescendantHwnds(hwnd, visit);
                return true;
            },
            IntPtr.Zero);
    }

    private void SetHwndClickThrough(IntPtr hwnd, bool clickThrough)
    {
        var current = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        if (clickThrough)
        {
            if (!_savedExStyles.ContainsKey(hwnd))
                _savedExStyles[hwnd] = current;

            if ((current & WsExTransparent) == 0)
                SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(current | WsExTransparent));
        }
        else
        {
            if (_savedExStyles.TryGetValue(hwnd, out var saved))
            {
                SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(saved));
                _savedExStyles.Remove(hwnd);
            }
            else if ((current & WsExTransparent) != 0)
            {
                SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(current & ~WsExTransparent));
            }
        }
    }

    private void RestoreAllHwnds()
    {
        foreach (var hwnd in _savedExStyles.Keys.ToArray())
            SetHwndClickThrough(hwnd, false);

        _savedExStyles.Clear();
        _rootPassesMouse = false;
    }

    public void Dispose()
    {
        _enabled = false;
        _cursorTimer.Stop();
        RestoreAllHwnds();
        _setWpfContentHitTestVisible?.Invoke(true);

        _window.SourceInitialized -= OnWindowSourceInitialized;
        _window.SizeChanged -= OnWindowLayoutChanged;
        _window.LocationChanged -= OnWindowLayoutChanged;

        if (_source is not null)
        {
            _source = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrNative(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrNative(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtrNative(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtrNative(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
}
