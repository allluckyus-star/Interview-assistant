using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Stealth = exclude-from-capture on the main window plus real popup HWNDs (menus/tooltips).
/// WPF tooltips are disabled while stealthed (ToolTipOpening is direct-routed, so a window handler misses children).
/// </summary>
public sealed class CaptureStealthMonitor : IDisposable
{
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint GwOwner = 4;
    private const uint EventObjectShow = 0x8002;
    private const int SwHide = 0;

    private static bool _classHandlerRegistered;
    private static CaptureStealthMonitor? _activeMonitor;

    private readonly Window _window;
    private readonly Func<bool> _isStealthEnabled;
    private readonly HashSet<IntPtr> _auxiliaryHwnds = new();
    private readonly WinEventDelegate _winEventProc;
    private IntPtr _winEventHook;

    public CaptureStealthMonitor(Window window, Func<bool> isStealthEnabled)
    {
        _window = window;
        _isStealthEnabled = isStealthEnabled;
        _winEventProc = OnWinEvent;
    }

    public void Start()
    {
        if (!WindowCaptureStealth.IsSupported)
            return;

        _activeMonitor = this;
        RegisterToolTipClassHandler();
        InstallTooltipShowHook();
        SetToolTipsEnabled(!_isStealthEnabled());

        _window.AddHandler(
            ContextMenu.OpenedEvent,
            new RoutedEventHandler(OnContextMenuOpened),
            true);
    }

    public void SyncNow(bool excludeFromCapture)
    {
        if (!WindowCaptureStealth.IsSupported)
            return;

        SetToolTipsEnabled(!excludeFromCapture);

        var root = new WindowInteropHelper(_window).Handle;
        if (root == IntPtr.Zero)
            return;

        SetAffinity(root, excludeFromCapture);

        if (excludeFromCapture)
            ApplyAuxiliaryPopups(root, exclude: true);
        else
            ClearTrackedAuxiliary();
    }

    private void SetToolTipsEnabled(bool enabled)
    {
        ToolTipService.SetIsEnabled(_window, enabled);
        if (!enabled)
            CloseOpenToolTip();
    }

    private static void RegisterToolTipClassHandler()
    {
        if (_classHandlerRegistered)
            return;

        _classHandlerRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler(OnToolTipOpeningClass));
    }

    private static void OnToolTipOpeningClass(object sender, ToolTipEventArgs e)
    {
        if (_activeMonitor?._isStealthEnabled() == true)
            e.Handled = true;
    }

    private void InstallTooltipShowHook()
    {
        if (_winEventHook != IntPtr.Zero)
            return;

        _winEventHook = SetWinEventHook(
            EventObjectShow,
            EventObjectShow,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            0);
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (!_isStealthEnabled() || hwnd == IntPtr.Zero)
            return;

        var classBuffer = new StringBuilder(64);
        if (GetClassName(hwnd, classBuffer, classBuffer.Capacity) <= 0)
            return;

        if (!classBuffer.ToString().Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase))
            return;

        SetAffinity(hwnd, exclude: true);
        _auxiliaryHwnds.Add(hwnd);
        ShowWindow(hwnd, SwHide);
    }

    private void CloseOpenToolTip()
    {
        foreach (var tip in FindOpenToolTips(_window))
            tip.IsOpen = false;
    }

    private static IEnumerable<ToolTip> FindOpenToolTips(DependencyObject root)
    {
        if (root is ToolTip { IsOpen: true } openTip)
            yield return openTip;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var tip in FindOpenToolTips(VisualTreeHelper.GetChild(root, i)))
                yield return tip;
        }
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        // Opened bubbles: handler on Window yields sender=Window; the menu is e.Source.
        if (!_isStealthEnabled() || e.Source is not ContextMenu menu)
            return;

        _window.Dispatcher.BeginInvoke(
            () => ApplyAffinityToVisual(menu),
            DispatcherPriority.Loaded);
    }

    private void ApplyAffinityToVisual(DependencyObject? obj)
    {
        if (!_isStealthEnabled() || obj is not Visual visual)
            return;

        if (PresentationSource.FromVisual(visual) is not HwndSource { Handle: { } hwnd } || hwnd == IntPtr.Zero)
            return;

        var root = new WindowInteropHelper(_window).Handle;
        if (hwnd == root)
            return;

        SetAffinity(hwnd, true);
        _auxiliaryHwnds.Add(hwnd);
    }

    private void ApplyAuxiliaryPopups(IntPtr rootHwnd, bool exclude)
    {
        var classBuffer = new StringBuilder(128);
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == rootHwnd || !IsAuxiliaryPopupHwnd(hwnd, rootHwnd, classBuffer))
                return true;

            SetAffinity(hwnd, exclude);
            if (exclude)
                _auxiliaryHwnds.Add(hwnd);
            return true;
        }, IntPtr.Zero);
    }

    private void ClearTrackedAuxiliary()
    {
        foreach (var hwnd in _auxiliaryHwnds.ToArray())
            SetAffinity(hwnd, exclude: false);
        _auxiliaryHwnds.Clear();
    }

    private static bool IsAuxiliaryPopupHwnd(IntPtr hwnd, IntPtr rootHwnd, StringBuilder classBuffer)
    {
        if (!IsWindowVisible(hwnd))
            return false;

        classBuffer.Clear();
        _ = GetClassName(hwnd, classBuffer, classBuffer.Capacity);
        var className = classBuffer.ToString();

        if (IsWebViewOrChromeClass(className))
            return false;

        if (className.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase))
            return true;

        if (className.Equals("#32768", StringComparison.OrdinalIgnoreCase))
            return true;

        return GetWindow(hwnd, GwOwner) == rootHwnd;
    }

    private static bool IsWebViewOrChromeClass(string className)
    {
        if (string.IsNullOrEmpty(className))
            return false;
        return className.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
            || className.Contains("WebView", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("Cef", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetAffinity(IntPtr hwnd, bool exclude)
    {
        if (hwnd == IntPtr.Zero)
            return;
        var affinity = exclude ? WdaExcludeFromCapture : WdaNone;
        SetWindowDisplayAffinity(hwnd, affinity);
    }

    public void Dispose()
    {
        if (_activeMonitor == this)
        {
            _activeMonitor = null;
            SetToolTipsEnabled(enabled: true);
        }

        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        ClearTrackedAuxiliary();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
