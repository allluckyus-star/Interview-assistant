using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.Ui;
using InterviewAssistant.Bridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;

namespace InterviewAssistant.App;

/// <summary>Result payload from <c>__iaWpfSendOnly</c> delivered via <c>chrome.webview.postMessage</c>.</summary>
internal sealed class GptSendResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public partial class MainWindow : Window
{
    private enum WizardStep
    {
        Step1ResumeJd = 1,
        Step2Login = 2,
        Step3ResumeSummary = 3,
        Step4JdSummary = 4,
        Step5Interview = 5,
        Main = 6,
    }

    private enum PrepSendKind
    {
        Resume,
        Jd,
        InitialInterview,
    }

    private WizardStep _step = WizardStep.Step1ResumeJd;
    private WebView2? GptWebView;
    private CoreWebView2? GptCore => GptWebView?.CoreWebView2;
    private bool _webViewInitialized;
    private bool _pipelineInjected;
    private bool _loginPageReady;
    private Storyboard? _wizardTitleStoryboard;
    private const string WizardStep1Title = "Resume & Job Description";
    private static readonly TimeSpan WizardTitleAnimDuration = TimeSpan.FromMilliseconds(300);
    private bool _sendInProgress;
    private readonly string _webViewUserDataDir;
    private readonly DispatcherTimer _loginPollTimer;
    private const string LoginChatReadyScript =
        """
        (() => {
          try {
            if (document.readyState !== 'complete') return false;
            if (typeof __iaFindComposer !== 'function') return false;
            var c = __iaFindComposer();
            if (!c) return false;
            var surface = document.querySelector('[data-composer-surface="true"]');
            if (surface) {
              var st = window.getComputedStyle(surface);
              if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) return false;
            }
            return true;
          } catch (_e) { return false; }
        })()
        """;

    private const double MinSliderOpacityPercent = 4;
    private const double MinSliderOpacityFraction = MinSliderOpacityPercent / 100.0;
    private const double DefaultOpacityFraction = 0.8;
    private double _recentOpacity = DefaultOpacityFraction;
    private double _lastChatPageOpacity01 = DefaultOpacityFraction;
    private readonly SemaphoreSlim _webViewScriptGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GptSendResult>> _prepSendCompletes = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GptSendResult>> _attachCompletes = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GptSendResult>> _pasteCompletes = new();
    private static readonly JsonSerializerOptions sGptSendJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly InterviewHotkeyService _hotkeys = new();
    private readonly OpacityWindowHotkeys _opacityHotkeys;
    private readonly ClickThroughHotkeys _clickThroughHotkeys;
    private readonly CaptureStealthHotkeys _stealthHotkeys;
    private readonly SnipComposerHotkeys _snipHotkeys;
    private readonly InterviewSessionCoordinator _interview;
    private bool _interviewSessionActive;
    private bool _settingsViewActive;
    private bool _chunkSendInProgress;
    private bool _snipInProgress;
    private CancellationTokenSource? _snipCaptureCts;
    private bool _folderCombineBusy;
    private bool _captureStealthEnabled;
    private bool _clickThroughEnabled;
    private CaptureStealthMonitor? _captureStealthMonitor;
    private WindowClickThroughController? _clickThroughController;
    private readonly string? _startupToastMessage;
    private readonly ToastLevel _startupToastLevel;
    private string _latestGptAnswer = "";

    /// <summary>Maps to <see cref="SnapshotExportDirectory"/> — registered in WebView2 before navigating to ChatGPT.</summary>
    private const string SnapshotExportVirtualHost = "ia-export-host";

    private static string SnapshotExportDirectory =>
        Path.Combine(Path.GetTempPath(), "InterviewAssistant", "exports");
    private static readonly Color SessionModeDeepColor =
        (Color)ColorConverter.ConvertFromString("#4dd0e1")!;
    /// <summary>Same as the session mode outer bar fill so idle segments match the track.</summary>
    private static readonly Color SessionModeLightColor =
        (Color)ColorConverter.ConvertFromString("#b2ebf2")!;
    private const double SessionModeMinWidthInactive = 28;
    private string? _sessionModeUiShown;
    private static int _gptCopyInFlight;

    public MainWindow(PromptStore promptStore, string bridgeHost, int bridgePort, string? startupToastMessage = null, ToastLevel startupToastLevel = ToastLevel.Warning)
    {
        StartupDiagnostics.Log("MainWindow: before InitializeComponent");
        ApplyOpaqueWindowFallbackIfRequested();
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"MainWindow: InitializeComponent failed: {ex}");
            throw;
        }

        StartupDiagnostics.Log("MainWindow: after InitializeComponent");
        TryCreateGptWebViewControl();
        StartupDiagnostics.Log("MainWindow: after WebView setup");
        ToastService.Register(AppToastHost);
        _startupToastMessage = startupToastMessage;
        _startupToastLevel = startupToastLevel;
        _opacityHotkeys = new OpacityWindowHotkeys(this);
        _opacityHotkeys.OpacityTogglePressed += () =>
            Dispatcher.BeginInvoke(ToggleOpacityWithHotkey);
        _opacityHotkeys.Attach();
        _clickThroughHotkeys = new ClickThroughHotkeys(this);
        _clickThroughHotkeys.ClickThroughTogglePressed += () =>
            Dispatcher.BeginInvoke(ToggleClickThroughWithHotkey);
        _clickThroughHotkeys.Attach();
        _stealthHotkeys = new CaptureStealthHotkeys(this);
        _stealthHotkeys.StealthTogglePressed += () =>
            Dispatcher.BeginInvoke(ToggleCaptureStealthWithHotkey);
        _stealthHotkeys.Attach();
        _snipHotkeys = new SnipComposerHotkeys(this);
        _snipHotkeys.ImageSnipPressed += () =>
            Dispatcher.BeginInvoke(() => _ = RunSnipThenAsync(attachImage: true));
        _snipHotkeys.TextSnipPressed += () =>
            Dispatcher.BeginInvoke(() => _ = RunSnipThenAsync(attachImage: false));
        _snipHotkeys.Attach();
        _interview = new InterviewSessionCoordinator(promptStore, bridgeHost, bridgePort, _hotkeys);
        WireInterviewSession();
        InitInterviewTopBarIcons();
        InterviewSettingsPanel.Bind(_interview.ModePrompts);
        InterviewSettingsPanel.BackRequested += (_, _) => ShowInterviewSettings(false);
        CaptionFeed.CopyPromptBuilder = text =>
        {
            var intent = _interview.ResolveInterviewerIntentForPrompt(text);
            var (_, finalPrompt) = ChunkPromptBuilder.Build(intent, _interview.ModePrompts.GetActiveTemplate());
            return string.IsNullOrWhiteSpace(finalPrompt) ? null : finalPrompt;
        };
        CaptionFeed.GetEndpointWordChoices = count =>
            _interview.GetEndpointWordChoices(count);
        CaptionFeed.SetEndpointAtIndex = idx =>
            _interview.SetDraftEndpointAt(idx);
        CaptionFeed.CaptureDraftForEdit = label =>
            _interview.CaptureDraftForInlineEdit(label);

        var (resume, jd) = ResumeJdStore.Load();
        ResumeTextBox.Text = resume;
        JdTextBox.Text = jd;
        ApplyContentOpacity(OpacitySlider.Value / 100.0);
        _webViewUserDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".interview_assistant",
            "webview2_gpt_profile");
        Directory.CreateDirectory(_webViewUserDataDir);
        _loginPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _loginPollTimer.Tick += LoginPollTimer_OnTick;
        _captureStealthMonitor = new CaptureStealthMonitor(this, () => _captureStealthEnabled);
        _captureStealthMonitor.Start();
        SizeChanged += (_, _) => ScheduleCaptureStealthSync();
        LocationChanged += (_, _) => ScheduleCaptureStealthSync();
        ApplyShellPrepChrome();
        GptCopyResultIconHost.Child = TopBarIcons.CreateCopyGlyphIcon(14, "#111111");
        GptImageIconHost.Child = TopBarIcons.CreateImageIcon(14, "#111111");
        GptTextIconHost.Child = TopBarIcons.CreateTextGlyphIcon(14, "#111111");
        GptFolderIconHost.Child = TopBarIcons.CreateFolderIcon(14, "#111111");
        WindowTaskbarHiding.Apply(this);
        ApplyWizardUi();
        StartupDiagnostics.Log("MainWindow: constructor done");
    }

    /// <summary>
    /// Default UI uses a transparent window so opacity shows the desktop behind.
    /// Set env <c>IA_OPAQUE_WINDOW=1</c> only if transparency crashes on a specific PC.
    /// </summary>
    private void ApplyOpaqueWindowFallbackIfRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("IA_OPAQUE_WINDOW"),
                "1",
                StringComparison.OrdinalIgnoreCase))
            return;

        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5));
        StartupDiagnostics.Log("MainWindow: IA_OPAQUE_WINDOW=1 (dimmed opacity, not see-through)");
    }

    private void TryCreateGptWebViewControl()
    {
        if (GptWebView is not null)
            return;

        if (!WebView2RuntimeCheck.TryGetInstalledVersion(out var version, out var error))
        {
            StartupDiagnostics.Log(
                $"WebView2 runtime not available: {error ?? "not installed"}");
            GptWebViewHost.Children.Add(new TextBlock
            {
                Text = "WebView2 Runtime is required for ChatGPT.\n\n"
                    + "Install: https://go.microsoft.com/fwlink/p/?LinkId=2124703\n"
                    + "Then restart this app.",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(16),
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            });
            return;
        }

        try
        {
            StartupDiagnostics.Log($"WebView2 runtime: {version}");
            GptWebView = new WebView2 { Visibility = Visibility.Visible };
            GptWebViewHost.Children.Add(GptWebView);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"WebView2 control create failed: {ex}");
            StartupDiagnostics.ShowFatalDialog("WebView2 could not load.", ex);
        }
    }

    private void WireInterviewSession()
    {
        _interview.DraftCaptionChanged += draft =>
            Dispatcher.BeginInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(draft))
                    CaptionFeed.ClearDraft();
                else
                    CaptionFeed.UpdateDraft(draft);
            });
        _interview.StatusMessage += msg =>
            Dispatcher.BeginInvoke(() => ShowInterviewStatus(msg));
        _interview.GptAnswerReceived += answer =>
            Dispatcher.BeginInvoke(() =>
            {
                _latestGptAnswer = (answer ?? "").Trim();
                CaptionFeed.AddGptBubble(answer ?? "");
            });
        _interview.InterviewerChunkCaptured += (text, source) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (string.Equals(source, "sent_gpt", StringComparison.OrdinalIgnoreCase))
                {
                    var intent = _interview.ResolveInterviewerIntentForPrompt(text);
                    var (_, clip) = ChunkPromptBuilder.Build(intent, _interview.ModePrompts.GetActiveTemplate());
                    CaptionFeed.FinalizeDraft(text, clip);
                }
                else
                    CaptionFeed.AddInterviewerBubble(text, null);
            });
        _interview.SendChunkToGptRequested += prompt =>
            Dispatcher.BeginInvoke(() => _ = RunInterviewChunkSendAsync(prompt));
        CaptionFeed.BubbleEditFinished += (_, e) =>
            Dispatcher.BeginInvoke(() => _interview.FinishBubbleEdit(e.Reject, e.Text, e.CopyPrompt));
    }

    private void InitInterviewTopBarIcons()
    {
        SessionModeReadIconHost.Child = TopBarIcons.CreateReadModeIcon(14);
        SessionModeTypeIconHost.Child = TopBarIcons.CreateTypeModeIcon(14);
        SessionModeErrorIconHost.Child = TopBarIcons.CreateErrorModeIcon(14);
        SessionModeBehavioralIconHost.Child = TopBarIcons.CreateBehavioralModeIcon(14);
        SessionModeClosingIconHost.Child = TopBarIcons.CreateClosingModeIcon(14);
        SaveTranscriptIconHost.Child = TopBarIcons.CreateSaveIcon(16);
        SettingsNavIconHost.Child = TopBarIcons.CreateKebabIcon(16);
        ClickThroughIconHost.Child = TopBarIcons.CreateClickThroughIcon(16, "#111111", false);
        ClickThroughToggleButton.IsChecked = false;
        UpdateClickThroughToggleUi();
        StealthToggleButton.IsEnabled = WindowCaptureStealth.IsSupported;
        StealthToggleButton.IsChecked = _captureStealthEnabled;
        UpdateStealthToggleUi();
        UpdateSessionModeButtonUi("read", animate: false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _clickThroughController = new WindowClickThroughController(
            this,
            () => TopBarChromeGrid,
            SetWpfContentReceivesMouseHits);
        _clickThroughController.SetEnabled(_clickThroughEnabled);
        ApplyCaptureStealth();
    }

    private void ScheduleCaptureStealthSync()
    {
        if (!WindowCaptureStealth.IsSupported || !_captureStealthEnabled)
            return;
        _captureStealthMonitor?.SyncNow(excludeFromCapture: true);
    }

    private void ApplyCaptureStealth()
    {
        if (!WindowCaptureStealth.IsSupported)
            return;
        _captureStealthMonitor?.SyncNow(_captureStealthEnabled);
    }

    private void UpdateStealthToggleUi()
    {
        var stealthed = _captureStealthEnabled && WindowCaptureStealth.IsSupported;
        StealthIconHost.Child = TopBarIcons.CreateHumanStealthIcon(16, "#111111", stealthed);
        StealthToggleButton.IsChecked = stealthed;
        StealthToggleButton.ToolTip = stealthed
            ? "Stealth on (Alt+Shift+3) — hidden from screen capture / share"
            : WindowCaptureStealth.IsSupported
                ? "Stealth off (Alt+Shift+3) — visible in screen capture / share"
                : "Stealth requires Windows";
    }

    private void StealthToggleButton_OnClick(object sender, RoutedEventArgs e) =>
        SetCaptureStealth(StealthToggleButton.IsChecked == true, fromHotkey: false);

    private void ToggleCaptureStealthWithHotkey()
    {
        if (!IsLoaded || !WindowCaptureStealth.IsSupported)
            return;
        SetCaptureStealth(!_captureStealthEnabled, fromHotkey: true);
    }

    private void SetCaptureStealth(bool enabled, bool fromHotkey)
    {
        if (!WindowCaptureStealth.IsSupported)
            return;

        _captureStealthEnabled = enabled;
        StealthToggleButton.IsChecked = enabled;
        ApplyCaptureStealth();
        UpdateStealthToggleUi();
        var msg = enabled
            ? "Stealth on — window hidden from capture."
            : "Stealth off — window visible in capture.";
        if (fromHotkey)
            msg = $"Alt+Shift+3 → {msg}";
        ToastService.Show(msg, ToastLevel.Info);
    }

    private void UpdateClickThroughToggleUi()
    {
        ClickThroughIconHost.Child = TopBarIcons.CreateClickThroughIcon(16, "#111111", _clickThroughEnabled);
        ClickThroughToggleButton.IsChecked = _clickThroughEnabled;
        ClickThroughToggleButton.ToolTip = _clickThroughEnabled
            ? "Click-through on (Alt+Shift+2) — mouse goes to apps behind; top bar still works"
            : "Click-through off — mouse controls this window";
    }

    private void ClickThroughToggleButton_OnClick(object sender, RoutedEventArgs e) =>
        ApplyClickThrough(ClickThroughToggleButton.IsChecked == true, fromHotkey: false);

    private void ToggleClickThroughWithHotkey()
    {
        if (!IsLoaded)
            return;
        ApplyClickThrough(!_clickThroughEnabled, fromHotkey: true);
    }

    private void ScheduleClickThroughHwndRefresh()
    {
        _clickThroughController?.RefreshChildHwnds();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(350).ConfigureAwait(true);
            _clickThroughController?.RefreshChildHwnds();
            await Task.Delay(700).ConfigureAwait(true);
            _clickThroughController?.RefreshChildHwnds();
        });
    }

    private void SetWpfContentReceivesMouseHits(bool receivesHits)
    {
        ContentHost.IsHitTestVisible = receivesHits;
    }

    private void ApplyClickThrough(bool enabled, bool fromHotkey)
    {
        _clickThroughEnabled = enabled;
        ClickThroughToggleButton.IsChecked = enabled;
        _clickThroughController?.UpdateTopChromeBounds();
        _clickThroughController?.SetEnabled(_clickThroughEnabled);
        if (_clickThroughEnabled)
        {
            Topmost = true;
            ScheduleClickThroughHwndRefresh();
        }
        UpdateClickThroughToggleUi();
        if (fromHotkey)
        {
            var pct = _clickThroughEnabled ? "on" : "off";
            ToastService.Show(
                _clickThroughEnabled
                    ? "Alt+Shift+2 → click-through on. Window stays visible on top; hover top bar for buttons."
                    : "Alt+Shift+2 → click-through off.",
                ToastLevel.Info);
        }
        else
        {
            ToastService.Show(
                _clickThroughEnabled
                    ? "Click-through on — window stays on top; mouse controls apps behind. Use top bar or Alt+Shift+2 to turn off."
                    : "Click-through off — mouse controls this app again.",
                ToastLevel.Info);
        }
    }

    private void UpdateSessionModeButtonUi(string mode, bool animate = false)
    {
        _interview.ModePrompts.SessionMode = mode;
        var m = _interview.ModePrompts.SessionMode;
        var fromUi = _sessionModeUiShown;

        if (!animate)
        {
            StopSessionModeSegmentAnimations();
            ApplySessionModeSegmentsImmediate(m);
            _sessionModeUiShown = m;
            return;
        }

        if (fromUi == m)
            return;

        StopSessionModeSegmentAnimations();

        if (fromUi is null)
        {
            ApplySessionModeSegmentsImmediate(m);
            _sessionModeUiShown = m;
            return;
        }

        ApplySessionModeSegmentsImmediate(fromUi);
        _sessionModeUiShown = m;
        AnimateSessionModeSwitch(fromUi, m);
    }

    private static double SessionModePreferredAnimWidth(string mode) =>
        mode switch
        {
            "behavioral" => 120,
            "closing" => 72,
            "type" => 66,
            "error" => 58,
            _ => 64,
        };

    private static double SessionModeClipWidthExpanded(string mode) =>
        mode switch
        {
            "type" => 40,
            "error" => 38,
            "behavioral" => 132,
            "closing" => 52,
            _ => 36,
        };

    private static double SessionModeMinWidthActive(string mode) =>
        mode switch
        {
            "type" => 60,
            "error" => 54,
            "behavioral" => 116,
            "closing" => 68,
            _ => 58,
        };

    private (Button Button, TextBlock Label, Viewbox Icon, FrameworkElement TextClip) SessionModeSegmentParts(
        string mode) =>
        mode switch
        {
            "type" => (SessionModeTypeButton, SessionModeTypeLabel, SessionModeTypeIconHost, SessionModeTypeTextClip),
            "error" => (SessionModeErrorButton, SessionModeErrorLabel, SessionModeErrorIconHost, SessionModeErrorTextClip),
            "behavioral" => (
                SessionModeBehavioralButton,
                SessionModeBehavioralLabel,
                SessionModeBehavioralIconHost,
                SessionModeBehavioralTextClip),
            "closing" => (
                SessionModeClosingButton,
                SessionModeClosingLabel,
                SessionModeClosingIconHost,
                SessionModeClosingTextClip),
            _ => (SessionModeReadButton, SessionModeReadLabel, SessionModeReadIconHost, SessionModeReadTextClip),
        };

    private void StopSessionModeSegmentAnimations()
    {
        foreach (var mode in new[] { "read", "type", "error", "behavioral", "closing" })
        {
            var (btn, label, icon, clip) = SessionModeSegmentParts(mode);
            label.BeginAnimation(UIElement.OpacityProperty, null);
            btn.BeginAnimation(FrameworkElement.MinWidthProperty, null);
            btn.BeginAnimation(FrameworkElement.WidthProperty, null);
            clip.BeginAnimation(FrameworkElement.MaxWidthProperty, null);
            if (btn.Background is SolidColorBrush { IsFrozen: false } b)
                b.BeginAnimation(SolidColorBrush.ColorProperty, null);
            if (icon.RenderTransform is ScaleTransform st)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleX = 1;
                st.ScaleY = 1;
            }
        }
    }

    private void ApplySessionModeSegmentsImmediate(string m)
    {
        ApplySessionModeSegmentStateImmediate(SessionModeReadButton, SessionModeReadLabel, SessionModeReadTextClip, "read", m == "read");
        ApplySessionModeSegmentStateImmediate(SessionModeTypeButton, SessionModeTypeLabel, SessionModeTypeTextClip, "type", m == "type");
        ApplySessionModeSegmentStateImmediate(SessionModeErrorButton, SessionModeErrorLabel, SessionModeErrorTextClip, "error", m == "error");
        ApplySessionModeSegmentStateImmediate(
            SessionModeBehavioralButton,
            SessionModeBehavioralLabel,
            SessionModeBehavioralTextClip,
            "behavioral",
            m == "behavioral");
        ApplySessionModeSegmentStateImmediate(
            SessionModeClosingButton,
            SessionModeClosingLabel,
            SessionModeClosingTextClip,
            "closing",
            m == "closing");
    }

    private static void ApplySessionModeSegmentStateImmediate(
        Button segment,
        TextBlock label,
        FrameworkElement textClip,
        string modeKey,
        bool active)
    {
        label.BeginAnimation(UIElement.OpacityProperty, null);
        label.Opacity = 1;
        label.Visibility = Visibility.Visible;
        textClip.BeginAnimation(FrameworkElement.MaxWidthProperty, null);
        segment.BeginAnimation(FrameworkElement.MinWidthProperty, null);
        segment.BeginAnimation(FrameworkElement.WidthProperty, null);

        textClip.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        if (segment.Background is SolidColorBrush { IsFrozen: false } oldB)
            oldB.BeginAnimation(SolidColorBrush.ColorProperty, null);

        if (active)
        {
            segment.ClearValue(FrameworkElement.WidthProperty);
            segment.MinWidth = SessionModeMinWidthActive(modeKey);
        }
        else
        {
            segment.Width = SessionModeMinWidthInactive;
            segment.MinWidth = SessionModeMinWidthInactive;
        }

        textClip.MaxWidth = active ? SessionModeClipWidthExpanded(modeKey) : 0;
        segment.Background = new SolidColorBrush(active ? SessionModeDeepColor : SessionModeLightColor);
    }

    private void AnimateSessionModeSwitch(string from, string to)
    {
        var (fromBtn, _, _, fromClip) = SessionModeSegmentParts(from);
        var (toBtn, _, toIcon, toClip) = SessionModeSegmentParts(to);

        var dur = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var stagger = TimeSpan.FromMilliseconds(35);

        fromBtn.UpdateLayout();
        var fromW = Math.Max(SessionModeMinWidthInactive, fromBtn.ActualWidth);
        if (fromW < SessionModePreferredAnimWidth(from))
            fromW = SessionModePreferredAnimWidth(from);

        fromClip.Visibility = Visibility.Visible;

        fromBtn.MinWidth = SessionModeMinWidthInactive;
        fromBtn.BeginAnimation(FrameworkElement.WidthProperty, null);
        fromBtn.Width = fromW;

        AnimateDouble(
            fromClip,
            FrameworkElement.MaxWidthProperty,
            SessionModeClipWidthExpanded(from),
            0,
            dur,
            ease,
            onCompleted: () =>
            {
                fromClip.BeginAnimation(FrameworkElement.MaxWidthProperty, null);
                fromClip.Visibility = Visibility.Collapsed;
            });
        AnimateSegmentBackground(fromBtn, SessionModeDeepColor, SessionModeLightColor, 260);
        AnimateDouble(
            fromBtn,
            FrameworkElement.WidthProperty,
            fromW,
            SessionModeMinWidthInactive,
            dur,
            ease,
            onCompleted: () =>
            {
                fromBtn.BeginAnimation(FrameworkElement.WidthProperty, null);
                fromBtn.Width = SessionModeMinWidthInactive;
                fromBtn.MinWidth = SessionModeMinWidthInactive;
            });

        toBtn.MinWidth = SessionModeMinWidthInactive;
        toBtn.BeginAnimation(FrameworkElement.WidthProperty, null);
        toBtn.Width = SessionModeMinWidthInactive;
        toClip.Visibility = Visibility.Visible;
        toClip.MaxWidth = 0;
        var toTarget = SessionModePreferredAnimWidth(to);

        AnimateDouble(
            toClip,
            FrameworkElement.MaxWidthProperty,
            0,
            SessionModeClipWidthExpanded(to),
            dur,
            ease,
            stagger);
        AnimateSegmentBackground(toBtn, SessionModeLightColor, SessionModeDeepColor, 270, stagger);
        AnimateDouble(
            toBtn,
            FrameworkElement.WidthProperty,
            SessionModeMinWidthInactive,
            toTarget,
            dur,
            ease,
            stagger,
            onCompleted: () =>
            {
                toBtn.BeginAnimation(FrameworkElement.WidthProperty, null);
                toBtn.ClearValue(FrameworkElement.WidthProperty);
                toBtn.MinWidth = SessionModeMinWidthActive(to);
            });

        PulseSessionModeIcon(toIcon);
    }

    private static void AnimateDouble(
        FrameworkElement el,
        DependencyProperty prop,
        double from,
        double to,
        TimeSpan dur,
        IEasingFunction ease,
        TimeSpan? beginTime = null,
        Action? onCompleted = null)
    {
        el.BeginAnimation(prop, null);
        var a = new DoubleAnimation(from, to, dur) { EasingFunction = ease };
        if (beginTime.HasValue)
            a.BeginTime = beginTime;
        if (onCompleted is not null)
            a.Completed += (_, _) => onCompleted();
        el.BeginAnimation(prop, a);
    }

    private static void AnimateDouble(
        FrameworkElement el,
        DependencyProperty prop,
        double from,
        double to,
        TimeSpan dur,
        IEasingFunction ease,
        TimeSpan? beginTime)
    {
        AnimateDouble(el, prop, from, to, dur, ease, beginTime, null);
    }

    private static void AnimateSegmentBackground(Button btn, Color from, Color to, int ms, TimeSpan? beginTime = null)
    {
        if (btn.Background is SolidColorBrush { IsFrozen: false } existing)
            existing.BeginAnimation(SolidColorBrush.ColorProperty, null);
        var brush = new SolidColorBrush(from);
        btn.Background = brush;
        var anim = new ColorAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        if (beginTime.HasValue)
            anim.BeginTime = beginTime;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private static void PulseSessionModeIcon(Viewbox icon)
    {
        if (icon.RenderTransform is not ScaleTransform st)
            return;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        st.ScaleX = 1;
        st.ScaleY = 1;

        const double keyMid = 0.42;
        var dur = TimeSpan.FromMilliseconds(300);
        var ax = new DoubleAnimationUsingKeyFrames { Duration = dur };
        ax.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        ax.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.12,
            KeyTime.FromPercent(keyMid),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        ax.KeyFrames.Add(new EasingDoubleKeyFrame(
            1,
            KeyTime.FromPercent(1),
            new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

        var ay = new DoubleAnimationUsingKeyFrames { Duration = dur };
        ay.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        ay.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.12,
            KeyTime.FromPercent(keyMid),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        ay.KeyFrames.Add(new EasingDoubleKeyFrame(
            1,
            KeyTime.FromPercent(1),
            new QuadraticEase { EasingMode = EasingMode.EaseInOut }));

        var sb = new Storyboard();
        Storyboard.SetTarget(ax, st);
        Storyboard.SetTargetProperty(ax, new PropertyPath("ScaleX"));
        Storyboard.SetTarget(ay, st);
        Storyboard.SetTargetProperty(ay, new PropertyPath("ScaleY"));
        sb.Children.Add(ax);
        sb.Children.Add(ay);
        sb.Begin();
    }

    private void EnterMainInterviewMode()
    {
        if (_interviewSessionActive)
            return;
        CaptionFeed.Clear();
        if (_settingsViewActive)
            ShowInterviewSettings(false);
        var (resume, jd) = ResumeJdStore.Load();
        try
        {
            _interview.Start(resume, jd);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InterviewAssistant] EnterMainInterviewMode failed: {ex}");
            ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
            return;
        }

        _interviewSessionActive = true;
        InterviewTopBarPanel.Visibility = Visibility.Visible;
        UpdateSessionModeButtonUi(_interview.ModePrompts.SessionMode, animate: false);
        _ = EnsurePipelineInjectedAsync();
    }

    private void LeaveMainInterviewMode()
    {
        if (!_interviewSessionActive)
            return;
        _interview.ResetSessionArtifacts();
        _interviewSessionActive = false;
        CaptionFeed.Clear();
        InterviewTopBarPanel.Visibility = Visibility.Collapsed;
    }

    private void RestartWizardButton_OnClick(object sender, RoutedEventArgs e) =>
        RestartWizardToStep1();

    private void RestartWizardToStep1()
    {
        if (_step == WizardStep.Step1ResumeJd)
            return;

        CancelPendingWizardOperations();
        _settingsViewActive = false;
        InterviewSettingsPanel.Visibility = Visibility.Collapsed;
        LeaveMainInterviewMode();
        _interview.ResetSessionArtifacts();
        CaptionFeed.Clear();
        _latestGptAnswer = "";
        _interview.ModePrompts.SessionMode = "read";
        _step = WizardStep.Step1ResumeJd;
        ApplyWizardUi();
        ToastService.Show(
            "Back to Resume & Job Description. Your text is unchanged; ChatGPT stays open.",
            ToastLevel.Info);
    }

    private void CancelPendingWizardOperations()
    {
        foreach (var kv in _prepSendCompletes)
            kv.Value.TrySetCanceled();
        _prepSendCompletes.Clear();
        foreach (var kv in _attachCompletes)
            kv.Value.TrySetCanceled();
        _attachCompletes.Clear();
        foreach (var kv in _pasteCompletes)
            kv.Value.TrySetCanceled();
        _pasteCompletes.Clear();
        _sendInProgress = false;
        _chunkSendInProgress = false;
    }

    private void UpdateRestartWizardButtonVisibility()
    {
        RestartWizardButton.Visibility = _step == WizardStep.Step1ResumeJd
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShowInterviewSettings(bool show)
    {
        if (_step != WizardStep.Main)
            return;

        _settingsViewActive = show;
        InterviewSettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            InterviewSettingsPanel.RefreshCurrentMode();
            CaptionPanel.Visibility = Visibility.Collapsed;
            GptWebViewOpacityHost.Visibility = Visibility.Collapsed;
            UpdateGptSideToolsVisibility();
            SettingsNavButton.Background = new SolidColorBrush(Color.FromRgb(128, 222, 234));
        }
        else
        {
            CaptionPanel.Visibility = Visibility.Visible;
            GptWebViewOpacityHost.Visibility = Visibility.Visible;
            UpdateGptSideToolsVisibility();
            SettingsNavButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b2ebf2")!);
        }
    }

    private void SessionModeSegment_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode })
            return;
        if (_interview.ModePrompts.SessionMode == mode)
            return;
        UpdateSessionModeButtonUi(mode, animate: true);
        var label = char.ToUpper(mode[0]) + mode[1..] + " mode.";
        ToastService.Show(label, ToastLevel.Success);
    }

    private void SaveTranscriptButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_interview.History.HasInterviewerLines())
        {
            ToastService.Show("Nothing to save.", ToastLevel.Warning);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = Path.GetFileName(InterviewHistory.DefaultSavePath()),
            InitialDirectory = Path.GetDirectoryName(InterviewHistory.DefaultSavePath()) ?? "",
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dlg.FileName, _interview.History.BuildTranscriptText(), Encoding.UTF8);
            ToastService.Show($"Saved {Path.GetFileName(dlg.FileName)}.", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            ToastService.Show(ToastMessages.ForFileSaveException(ex), ToastLevel.Error);
        }
    }

    private void SettingsNavButton_OnClick(object sender, RoutedEventArgs e) =>
        ShowInterviewSettings(!_settingsViewActive);

    private void ShowInterviewStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        if (message.Contains("End = send", StringComparison.OrdinalIgnoreCase))
        {
            CaptionStatusText.Text = message;
            return;
        }

        var mapped = ToastMessages.ForInterviewStatus(message);
        if (mapped is { } toast)
            ToastService.Show(toast.Text, toast.Level);
    }

    private async Task<GptSendResult?> AttachImageToComposerAsync(string imagePngBase64)
    {
        if (GptCore is null)
            return new GptSendResult { Ok = false, Error = "webview_not_ready" };

        var bridgeReady = await ExecuteWebViewScriptAsync(
                "(() => !!(window.__iaWpfMessageBridgeInstalled && typeof window.__iaWpfAttachImageToComposer === 'function'))()")
            .ConfigureAwait(true);
        if (bridgeReady is not "true")
        {
            await EnsurePipelineInjectedAsync().ConfigureAwait(true);
        }

        if (!_pipelineInjected)
            return new GptSendResult { Ok = false, Error = "attach_missing" };

        GptWebView.Focus();
        await Task.Delay(40).ConfigureAwait(true);

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<GptSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _attachCompletes[requestId] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "ia_attach_image",
                requestId,
                imagePngBase64,
            });
            GptCore.PostWebMessageAsJson(payload);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(25))).ConfigureAwait(true);
            if (completed != tcs.Task)
                return new GptSendResult { Ok = false, Error = "attach_timeout" };

            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            _attachCompletes.TryRemove(requestId, out _);
        }
    }

    private async Task<GptSendResult?> AttachBinaryFileToComposerAsync(string fileBase64, string fileName, string mimeType)
    {
        if (GptCore is null)
            return new GptSendResult { Ok = false, Error = "webview_not_ready" };

        var bridgeReady = await ExecuteWebViewScriptAsync(
                "(() => !!(window.__iaWpfMessageBridgeInstalled && typeof window.__iaWpfAttachBinaryFileToComposer === 'function'))()")
            .ConfigureAwait(true);
        if (bridgeReady is not "true")
            await EnsurePipelineInjectedAsync().ConfigureAwait(true);

        if (!_pipelineInjected)
            return new GptSendResult { Ok = false, Error = "attach_missing" };

        GptWebView.Focus();
        await Task.Delay(120).ConfigureAwait(true);

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<GptSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _attachCompletes[requestId] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "ia_attach_file",
                requestId,
                fileBase64,
                fileName,
                mimeType,
            });
            GptCore.PostWebMessageAsJson(payload);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(true);
            if (completed != tcs.Task)
                return new GptSendResult { Ok = false, Error = "attach_timeout" };

            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            _attachCompletes.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Loads a snapshot .txt from the mapped export folder via HTTPS (no megabyte JSON), builds a <c>File</c>, attaches like a PNG.
    /// Requires <see cref="SnapshotExportVirtualHost"/> mapping before ChatGPT navigation.
    /// </summary>
    private async Task<GptSendResult?> AttachSnapshotFromExportViaVirtualHostAsync(string exportFileName, string mimeType)
    {
        if (GptCore is null)
            return new GptSendResult { Ok = false, Error = "webview_not_ready" };

        EnsureSnapshotExportHostMapped();

        var safeName = Path.GetFileName(exportFileName);
        if (string.IsNullOrEmpty(safeName)
            || !string.Equals(safeName, exportFileName, StringComparison.Ordinal)
            || safeName.Contains("..", StringComparison.Ordinal))
            return new GptSendResult { Ok = false, Error = "invalid_name" };

        var fullPath = Path.Combine(SnapshotExportDirectory, safeName);
        if (!File.Exists(fullPath))
            return new GptSendResult { Ok = false, Error = "file_not_found" };

        var bridgeReady = await ExecuteWebViewScriptAsync(
                "(() => typeof window.__iaWpfAttachFileFromVirtualUrl === 'function')()")
            .ConfigureAwait(true);
        if (bridgeReady is not "true")
            await EnsurePipelineInjectedAsync().ConfigureAwait(true);

        if (!_pipelineInjected)
            return new GptSendResult { Ok = false, Error = "attach_missing" };

        GptWebView.Focus();
        await Task.Delay(120).ConfigureAwait(true);

        var enc = Uri.EscapeDataString(safeName);
        var virtualUrl = $"https://{SnapshotExportVirtualHost}/{enc}";
        var urlLit = JsonSerializer.Serialize(virtualUrl);
        var nameLit = JsonSerializer.Serialize(safeName);
        var mimeLit = JsonSerializer.Serialize(mimeType);

        var tail =
            "(async function(){ " +
            "if (typeof window.__iaWpfAttachFileFromVirtualUrl !== 'function') return { ok: false, error: 'attach_url_helper_missing' }; " +
            "return await window.__iaWpfAttachFileFromVirtualUrl(" + urlLit + "," + nameLit + "," + mimeLit + "); " +
            "})()";
        var script = await ComposePipelineScriptAsync(tail).ConfigureAwait(false);
        var raw = await ExecuteWebViewScriptAsync(script).ConfigureAwait(true);
        LogWebViewScriptResult(raw);
        return TryParseGptSendResult(raw);
    }

    /// <summary>
    /// Maps <see cref="SnapshotExportVirtualHost"/> to the export folder with CORS allowed so chatgpt.com can
    /// <c>fetch()</c> snapshot bytes. Idempotent; call before first navigation and again before upload if needed.
    /// </summary>
    private void EnsureSnapshotExportHostMapped()
    {
        if (GptCore is null)
            return;
        Directory.CreateDirectory(SnapshotExportDirectory);
        GptCore.SetVirtualHostNameToFolderMapping(
            SnapshotExportVirtualHost,
            SnapshotExportDirectory,
            CoreWebView2HostResourceAccessKind.Allow);
    }

    private static string ToastMessageForImageAttach(GptSendResult? result)
    {
        var err = (result?.Error ?? "").Trim();
        return err switch
        {
            "composer_not_found" => "ChatGPT composer not found.",
            "attachment_not_visible" => "Could not attach image. Try again.",
            "image_missing" => "No image to attach.",
            "attach_missing" => "Attach helper not loaded. Restart the app.",
            "attach_timeout" => "Attach timed out. Click the ChatGPT box and try again.",
            "could_not_focus_composer" => "Could not focus ChatGPT input.",
            _ when err.Length > 0 => ToastMessages.Trim($"Could not attach image. {err}"),
            _ => "Could not attach image.",
        };
    }

    private static string ToastMessageForFileAttach(GptSendResult? result)
    {
        var err = (result?.Error ?? "").Trim();
        return err switch
        {
            "composer_not_found" => "ChatGPT composer not found.",
            "attachment_not_visible" => "Could not attach file. Drag the snapshot .txt into ChatGPT if needed.",
            "file_missing" => "No file to attach.",
            "attach_missing" => "Attach helper not loaded. Restart the app.",
            "attach_timeout" => "Attach timed out. Click the ChatGPT box and try again.",
            "could_not_focus_composer" => "Could not focus ChatGPT input.",
            "invalid_name" => "Invalid snapshot file name.",
            "file_not_found" => "Snapshot file missing — try the folder action again.",
            "attach_url_helper_missing" => "Attach helper out of date. Restart the app.",
            _ when err.StartsWith("fetch_failed_", StringComparison.Ordinal) =>
                ToastMessages.Trim($"Could not load snapshot into ChatGPT. ({err})"),
            _ when err.Length > 0 => ToastMessages.Trim($"Could not attach file. {err}"),
            _ => "Could not attach file.",
        };
    }

    private async Task<GptSendResult?> PasteTextToComposerAsync(string text, bool append = true)
    {
        if (GptCore is null)
            return new GptSendResult { Ok = false, Error = "webview_not_ready" };

        if (!await EnsurePipelineInjectedAsync().ConfigureAwait(true))
            return new GptSendResult { Ok = false, Error = "paste_missing" };

        GptWebView.Focus();
        await Task.Delay(40).ConfigureAwait(true);

        var scriptResult = await PasteTextViaExecuteScriptAsync(text, append).ConfigureAwait(true);
        Trace.WriteLine(
            $"[InterviewAssistant][paste] script ok={scriptResult?.Ok} phase={scriptResult?.Phase ?? "(null)"} error={scriptResult?.Error ?? "(none)"}");

        if (scriptResult?.Ok == true)
            return scriptResult;

        if (await VerifyComposerContainsTextAsync(text).ConfigureAwait(true))
            return new GptSendResult { Ok = true, Phase = "pasted" };

        var messageResult = await PasteTextViaPostMessageAsync(text, append).ConfigureAwait(true);
        Trace.WriteLine(
            $"[InterviewAssistant][paste] message ok={messageResult?.Ok} error={messageResult?.Error ?? "(none)"}");

        if (messageResult?.Ok == true)
            return messageResult;

        if (await VerifyComposerContainsTextAsync(text).ConfigureAwait(true))
            return new GptSendResult { Ok = true, Phase = "pasted" };

        return scriptResult ?? messageResult;
    }

    private async Task<GptSendResult?> PasteTextViaExecuteScriptAsync(string text, bool append)
    {
        var textArg = JsonSerializer.Serialize(text);
        var appendLit = append ? "true" : "false";
        var tail =
            "if (typeof window.__iaWpfPasteTextToComposer !== 'function') " +
            "return { ok: false, error: 'paste_missing' }; " +
            $"return await window.__iaWpfPasteTextToComposer({textArg}, {appendLit});";
        var script = await ComposePipelineScriptAsync(tail).ConfigureAwait(false);
        var raw = await ExecuteWebViewScriptAsync(script).ConfigureAwait(true);
        LogWebViewScriptResult(raw);
        return TryParseGptSendResult(raw);
    }

    private async Task<GptSendResult?> PasteTextViaPostMessageAsync(string text, bool append)
    {
        if (GptCore is null)
            return null;

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<GptSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pasteCompletes[requestId] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "ia_paste_text",
                requestId,
                text,
                append,
            });
            GptCore.PostWebMessageAsJson(payload);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(true);
            return completed != tcs.Task
                ? new GptSendResult { Ok = false, Error = "paste_timeout" }
                : await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            _pasteCompletes.TryRemove(requestId, out _);
        }
    }

    private async Task<bool> VerifyComposerContainsTextAsync(string text)
    {
        var needle = (text ?? "").Trim();
        if (needle.Length < 2)
            return false;

        if (needle.Length > 96)
            needle = needle[..96];

        var needleArg = JsonSerializer.Serialize(needle);
        var tail =
            "if (typeof window.__iaWpfComposerContainsNeedle !== 'function') return false; " +
            $"return !!window.__iaWpfComposerContainsNeedle({needleArg});";
        var script = await ComposePipelineScriptAsync(tail).ConfigureAwait(false);
        var raw = await ExecuteWebViewScriptAsync(script).ConfigureAwait(true);
        return string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToastMessageForTextPaste(GptSendResult? result)
    {
        var err = (result?.Error ?? "").Trim();
        return err switch
        {
            "composer_not_found" => "ChatGPT composer not found.",
            "text_missing" => "No text to paste.",
            "paste_missing" => "Paste helper not loaded. Restart the app.",
            "paste_timeout" => "Paste timed out. Click the ChatGPT box and try again.",
            "paste_parse_failed" => "Paste failed (page response). Click ChatGPT, then try again.",
            "could_not_insert" => "Could not paste into ChatGPT input.",
            _ when err.Length > 0 => ToastMessages.Trim($"Could not paste text. {err}"),
            _ => "Could not paste text.",
        };
    }

    private async Task<byte[]?> CaptureScreenSnipAsync(bool forTextOcr, CancellationToken cancellationToken)
    {
        Topmost = false;
        ToastService.Clear();
        var hint = forTextOcr
            ? "Snip on-screen text only (not this app or chat)."
            : "Snip: Win+Shift+S, then select an area.";
        ToastService.Show(hint, ToastLevel.Info);
        return await WindowsScreenSnipCapture.CaptureViaOsSnipAsync(Dispatcher, cancellationToken)
            .ConfigureAwait(true);
    }

    private static GptSendResult? TryParseGptSendResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var t = raw.Trim();
        if (string.Equals(t, "null", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Null || root.ValueKind == JsonValueKind.Undefined)
                return null;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                    return null;
                if (string.Equals(inner, "null", StringComparison.OrdinalIgnoreCase))
                    return null;
                return JsonSerializer.Deserialize<GptSendResult>(inner, sGptSendJson);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                var ok = false;
                if (root.TryGetProperty("ok", out var okEl))
                {
                    ok = okEl.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => okEl.TryGetInt32(out var n) && n != 0,
                        JsonValueKind.String => string.Equals(okEl.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                        _ => false,
                    };
                }

                root.TryGetProperty("phase", out var phaseEl);
                root.TryGetProperty("error", out var errEl);
                return new GptSendResult
                {
                    Ok = ok,
                    Phase = phaseEl.ValueKind == JsonValueKind.String ? phaseEl.GetString() : null,
                    Error = errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : null,
                };
            }

            return JsonSerializer.Deserialize<GptSendResult>(root.GetRawText(), sGptSendJson);
        }
        catch
        {
            return null;
        }
    }

    private async Task RunInterviewChunkSendAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            ToastService.Show("Send failed. Empty prompt.", ToastLevel.Warning);
            return;
        }

        if (!_webViewInitialized || GptCore is null)
        {
            ToastService.Show("Send failed. GPT not ready.", ToastLevel.Warning);
            return;
        }

        if (_sendInProgress || _chunkSendInProgress)
        {
            ToastService.Show("Send failed. Busy.", ToastLevel.Warning);
            return;
        }

        _chunkSendInProgress = true;
        ToastService.Show("Sending…", ToastLevel.Info);
        try
        {
            if (!await EnsurePipelineInjectedAsync().ConfigureAwait(true))
            {
                ToastService.Show("Send failed. Pipeline missing.", ToastLevel.Warning);
                return;
            }

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<GptSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _prepSendCompletes[requestId] = tcs;
            try
            {
                var script = await BuildStartSendOnlyScriptAsync(requestId, prompt, appendToComposer: true)
                    .ConfigureAwait(true);
                _ = ExecuteWebViewScriptAsync(script);

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(true);
                if (completed != tcs.Task && _prepSendCompletes.TryRemove(requestId, out var pending))
                    pending.TrySetResult(new GptSendResult { Ok = false, Error = "send_confirmation_timeout" });

                var parsed = await tcs.Task.ConfigureAwait(true);
                if (parsed.Ok && string.Equals(parsed.Phase, "sent", StringComparison.OrdinalIgnoreCase))
                    ToastService.Show("Sent (appended to prompt).", ToastLevel.Success);
                else
                    ToastService.Show(ToastMessages.ForSendFailure(parsed), ToastLevel.Warning);
            }
            finally
            {
                _prepSendCompletes.TryRemove(requestId, out _);
            }
        }
        catch (Exception ex)
        {
            ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
        }
        finally
        {
            _chunkSendInProgress = false;
        }
    }

    private void ApplyContentOpacity(double fraction)
    {
        if (fraction < 0)
            fraction = 0;
        if (fraction > 1)
            fraction = 1;
        ShellCard.Opacity = 1.0;
        GptWebViewOpacityHost.Opacity = 1.0;
        Opacity = fraction;
        _lastChatPageOpacity01 = fraction;
        _ = PushChatGptDomOpacityAsync();
    }

    private async Task PushChatGptDomOpacityAsync()
    {
        if (!_webViewInitialized || GptCore is null)
            return;
        var o = _lastChatPageOpacity01;
        if (o < 0)
            o = 0;
        if (o > 1)
            o = 1;
        string script;
        if (o >= 1.0 - 1e-6)
        {
            script =
                "(function(){try{var st=document.getElementById('__ia_wpf_trans');if(st)st.remove();" +
                "document.documentElement.style.removeProperty('opacity');document.body.style.removeProperty('opacity');" +
                "var nx=document.getElementById('__next');if(nx)nx.style.removeProperty('opacity');" +
                "return true;}catch(_e){return false;}})()";
        }
        else
        {
            var v = o.ToString("0.####", CultureInfo.InvariantCulture);
            script =
                "(function(){try{var v='" + v + "';" +
                "var st=document.getElementById('__ia_wpf_trans');" +
                "if(!st){st=document.createElement('style');st.id='__ia_wpf_trans';" +
                "st.textContent='html,body{background:transparent!important;}" +
                "#__next,body>div:first-of-type{background:transparent!important;}';" +
                "document.head.appendChild(st);}" +
                "document.documentElement.style.opacity='1';document.body.style.opacity='1';" +
                "var nx=document.getElementById('__next');" +
                "if(nx){nx.style.opacity=v;}else{document.documentElement.style.opacity=v;document.body.style.opacity=v;}" +
                "return true;}catch(_e){return false;}})()";
        }

        try
        {
            await ExecuteWebViewScriptAsync(script).ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
    }

    private async Task<string> ExecuteWebViewScriptAsync(string script)
    {
        if (GptCore is null)
            return "";
        await _webViewScriptGate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await GptWebView.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        finally
        {
            _webViewScriptGate.Release();
        }
    }

    private void OpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;
        var fraction = OpacitySlider.Value / 100.0;
        if (fraction >= MinSliderOpacityFraction)
            _recentOpacity = fraction;
        ApplyContentOpacity(fraction);
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_startupToastMessage))
            ToastService.Show(_startupToastMessage, _startupToastLevel);
        _hotkeys.Start();
        if (!_opacityHotkeys.IsRegistered || !_clickThroughHotkeys.IsRegistered || !_stealthHotkeys.IsRegistered
            || !_snipHotkeys.IsImageRegistered || !_snipHotkeys.IsTextRegistered)
        {
            var missing = string.Join(
                ", ",
                new[]
                    {
                        !_opacityHotkeys.IsRegistered ? "Alt+Shift+1 (opacity)" : null,
                        !_clickThroughHotkeys.IsRegistered ? "Alt+Shift+2 (click-through)" : null,
                        !_stealthHotkeys.IsRegistered ? "Alt+Shift+3 (stealth)" : null,
                        !_snipHotkeys.IsImageRegistered ? "Ctrl+Insert (image snip)" : null,
                        !_snipHotkeys.IsTextRegistered ? "Ctrl+Home (text snip)" : null,
                    }.Where(s => s is not null));
            ToastService.Show(
                ToastMessages.Trim($"Hotkey(s) not registered: {missing}. Another app may own the combo."),
                ToastLevel.Warning);
        }
        _ = EnsureWebViewAsync();
    }

    private void ToggleOpacityWithHotkey()
    {
        if (!IsLoaded)
            return;
        if (Opacity > 0.01)
        {
            _recentOpacity = Math.Max(MinSliderOpacityFraction, OpacitySlider.Value / 100.0);
            SetOpacityFromHotkey(0, "Alt+Shift+1");
        }
        else
            SetOpacityFromHotkey(_recentOpacity, "Alt+Shift+1");
    }

    private void SetOpacityFromHotkey(double fraction, string shortcutLabel)
    {
        if (!IsLoaded)
            return;

        if (fraction <= 0)
        {
            ApplyContentOpacity(0);
            ToastService.Show($"Shortcut caught: {shortcutLabel} → hidden.", ToastLevel.Success);
            return;
        }

        var restore = Math.Max(MinSliderOpacityFraction, fraction);
        OpacitySlider.Value = restore * 100.0;
        ApplyContentOpacity(restore);
        var pct = (int)Math.Round(restore * 100);
        ToastService.Show($"Shortcut caught: {shortcutLabel} → opacity {pct}%.", ToastLevel.Success);
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewInitialized)
            return;
        if (GptWebView is null)
        {
            TryCreateGptWebViewControl();
            if (GptWebView is null)
                return;
        }

        try
        {
            var env = await CoreWebView2Environment
                .CreateAsync(userDataFolder: _webViewUserDataDir)
                .ConfigureAwait(true);
            GptWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);
            await GptWebView.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            GptCore.Settings.IsStatusBarEnabled = false;
            EnsureSnapshotExportHostMapped();
            GptCore.WebMessageReceived += CoreWebView2_OnWebMessageReceived;
            GptWebView.NavigationCompleted += GptWebView_OnNavigationCompleted;
            GptWebView.Source = new Uri("https://chatgpt.com/");
            _webViewInitialized = true;
            await PushChatGptDomOpacityAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"WebView2 init failed: {ex}");
            ToastService.Show(ToastMessages.Trim($"WebView2 failed. {ex.Message}"), ToastLevel.Error);
        }
    }

    private void CoreWebView2_OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(json))
                return;
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return;
            var msgType = typeEl.GetString();
            if (string.Equals(msgType, "ia_send_debug", StringComparison.Ordinal))
            {
                LogSendDebugMessage(root);
                return;
            }

            if (string.Equals(msgType, "ia_attach_result", StringComparison.Ordinal)
                || string.Equals(msgType, "ia_paste_result", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("requestId", out var opRidEl) || opRidEl.ValueKind != JsonValueKind.String)
                    return;
                var opRequestId = opRidEl.GetString();
                if (string.IsNullOrEmpty(opRequestId))
                    return;
                if (!root.TryGetProperty("result", out var opResEl))
                    return;
                var opResult = JsonSerializer.Deserialize<GptSendResult>(opResEl.GetRawText(), sGptSendJson);
                if (opResult is null)
                    return;
                if (_attachCompletes.TryRemove(opRequestId, out var attachPending))
                    attachPending.TrySetResult(opResult);
                else if (_pasteCompletes.TryRemove(opRequestId, out var pastePending))
                    pastePending.TrySetResult(opResult);
                return;
            }

            if (!string.Equals(msgType, "ia_send_result", StringComparison.Ordinal))
                return;
            if (!root.TryGetProperty("requestId", out var ridEl) || ridEl.ValueKind != JsonValueKind.String)
                return;
            var requestId = ridEl.GetString();
            if (string.IsNullOrEmpty(requestId))
                return;
            if (!root.TryGetProperty("result", out var resEl))
                return;
            var result = JsonSerializer.Deserialize<GptSendResult>(resEl.GetRawText(), sGptSendJson);
            if (result is null)
                return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_prepSendCompletes.TryRemove(requestId, out var tcs))
                    tcs.TrySetResult(result);
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine("[InterviewAssistant] WebMessageReceived parse: " + ex.Message);
        }
    }

    private async void GptWebView_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || GptCore is null)
            return;
        var uri = GptWebView.Source?.ToString() ?? "";
        if (!uri.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase))
            return;
        _pipelineInjected = false;
        if (_step == WizardStep.Step2Login)
        {
            _loginPageReady = false;
            ApplyLoginGateUi();
        }

        try
        {
            await EnsurePipelineInjectedAsync().ConfigureAwait(true);
            await PushChatGptDomOpacityAsync().ConfigureAwait(true);
            ScheduleCaptureStealthSync();
            if (_clickThroughEnabled)
                ScheduleClickThroughHwndRefresh();
            if (_step == WizardStep.Step2Login)
                _ = RefreshLoginPageReadyAsync();
            }
            catch
            {
            // ignore
        }
    }

    private async Task<bool> EnsurePipelineInjectedAsync()
    {
        if (GptCore is null)
            return false;

        var ready = await ExecuteWebViewScriptAsync(
                "(() => !!(typeof window.__iaWpfPasteTextToComposer === 'function' && window.__iaWpfMessageBridgeInstalled))()")
            .ConfigureAwait(true);
        if (ready is "true")
        {
            _pipelineInjected = true;
            return true;
        }

        _pipelineInjected = false;
        var path = Path.Combine(AppContext.BaseDirectory, "GptSendPipeline.js");
        if (!File.Exists(path))
            return false;

        var js = await File.ReadAllTextAsync(path).ConfigureAwait(true);
        await ExecuteWebViewScriptAsync(js).ConfigureAwait(true);

        ready = await ExecuteWebViewScriptAsync(
                "(() => !!(typeof window.__iaWpfPasteTextToComposer === 'function' && window.__iaWpfMessageBridgeInstalled))()")
            .ConfigureAwait(true);
        _pipelineInjected = ready is "true";
        return _pipelineInjected;
    }

    private void ApplyShellPrepChrome()
    {
        ShellCard.Background = new SolidColorBrush(Color.FromRgb(228, 231, 238));
        ShellCard.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));
    }

    private void ApplyShellInterviewChrome()
    {
        ShellCard.Background = new SolidColorBrush(Color.FromRgb(252, 252, 255));
        ShellCard.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 17, 17, 17));
    }

    private void ApplyWizardUi()
    {
        _loginPollTimer.Stop();

        switch (_step)
        {
            case WizardStep.Step1ResumeJd:
                LeaveMainInterviewMode();
                Step1Panel.Visibility = Visibility.Visible;
                GptWebViewOpacityHost.Visibility = Visibility.Collapsed;
                WizardHeaderPanel.Visibility = Visibility.Visible;
                WizardFooterPanel.Visibility = Visibility.Collapsed;
                CaptionPanel.Visibility = Visibility.Collapsed;
                CaptionRowDefinition.Height = new GridLength(1, GridUnitType.Auto);
                ContentMiddleRowDefinition.Height = new GridLength(1, GridUnitType.Star);
                WizardFooterRowDefinition.Height = new GridLength(0);
                Grid.SetRow(GptWebViewOpacityHost, 1);
                Grid.SetRowSpan(GptWebViewOpacityHost, 1);
                ApplyShellPrepChrome();
                SetWizardTitleAnimated(WizardStep1Title);
                break;

            case WizardStep.Step2Login:
            case WizardStep.Step3ResumeSummary:
            case WizardStep.Step4JdSummary:
            case WizardStep.Step5Interview:
                LeaveMainInterviewMode();
                Step1Panel.Visibility = Visibility.Collapsed;
                GptWebViewOpacityHost.Visibility = Visibility.Visible;
                WizardHeaderPanel.Visibility = Visibility.Visible;
                WizardFooterPanel.Visibility = Visibility.Visible;
                CaptionPanel.Visibility = Visibility.Collapsed;
                CaptionRowDefinition.Height = new GridLength(1, GridUnitType.Auto);
                ContentMiddleRowDefinition.Height = new GridLength(1, GridUnitType.Star);
                WizardFooterRowDefinition.Height = new GridLength(1, GridUnitType.Auto);
                Grid.SetRow(GptWebViewOpacityHost, 1);
                Grid.SetRowSpan(GptWebViewOpacityHost, 1);
                ApplyShellPrepChrome();

                WizardSkipButton.Visibility = _step == WizardStep.Step2Login
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                switch (_step)
                {
                    case WizardStep.Step2Login:
                        _loginPageReady = false;
                        SetWizardTitleAnimated("Log In and Open Chat");
                        WizardPrimaryButton.Content = "Continue";
                        ApplyLoginGateUi();
                        _loginPollTimer.Start();
                        break;
                    case WizardStep.Step3ResumeSummary:
                        SetWizardTitleAnimated("Send Resume");
                        WizardPrimaryButton.Content = "Send";
                        ApplyWizardSendStepButtonsEnabled();
                        break;
                    case WizardStep.Step4JdSummary:
                        SetWizardTitleAnimated("Send Job Description");
                        WizardPrimaryButton.Content = "Send";
                        ApplyWizardSendStepButtonsEnabled();
                        break;
                    case WizardStep.Step5Interview:
                        SetWizardTitleAnimated("Prepare for Interview");
                        WizardPrimaryButton.Content = "Send";
                        ApplyWizardSendStepButtonsEnabled();
                        break;
                }
                break;

            case WizardStep.Main:
                Step1Panel.Visibility = Visibility.Collapsed;
                WizardHeaderPanel.Visibility = Visibility.Collapsed;
                WizardFooterPanel.Visibility = Visibility.Collapsed;
                CaptionPanel.Visibility = Visibility.Visible;
                GptWebViewOpacityHost.Visibility = Visibility.Visible;
                CaptionRowDefinition.Height = new GridLength(3, GridUnitType.Star);
                ContentMiddleRowDefinition.Height = new GridLength(7, GridUnitType.Star);
                WizardFooterRowDefinition.Height = new GridLength(0);
                Grid.SetRow(CaptionPanel, 0);
                Grid.SetRow(GptWebViewOpacityHost, 1);
                Grid.SetRowSpan(GptWebViewOpacityHost, 1);
                ApplyShellInterviewChrome();
                EnterMainInterviewMode();
                break;
        }

        if (_step != WizardStep.Step2Login)
            WizardGptLoginBlocker.Visibility = Visibility.Collapsed;

        UpdateGptSideToolsVisibility();
        UpdateRestartWizardButtonVisibility();
        if (_webViewInitialized && GptWebViewOpacityHost.Visibility == Visibility.Visible)
            _ = PushChatGptDomOpacityAsync();
    }

    private void SetWizardTitleAnimated(string newTitle)
    {
        var current = WizardTitleText.Text ?? "";
        if (string.IsNullOrEmpty(current) || string.Equals(current, newTitle, StringComparison.Ordinal))
        {
            _wizardTitleStoryboard?.Stop();
            _wizardTitleStoryboard = null;
            WizardTitleOutgoing.Visibility = Visibility.Collapsed;
            WizardTitleText.Text = newTitle;
            ScheduleWizardTitlePlacement(WizardTitleText, WizardTitleSlot.Center, 1);
            WizardTitleOutgoing.Opacity = 1;
            return;
        }

        _wizardTitleStoryboard?.Stop();
        WizardTitleOutgoing.Text = current;
        WizardTitleText.Text = newTitle;
        WizardTitleOutgoing.Visibility = Visibility.Visible;

        void BeginTransition()
        {
            if (!TryGetWizardTitleViewportWidth(out var viewportW))
            {
                void OnSized(object? s, SizeChangedEventArgs e)
                {
                    if (!TryGetWizardTitleViewportWidth(out _))
                        return;
                    WizardTitleViewport.SizeChanged -= OnSized;
                    BeginTransition();
                }

                WizardTitleViewport.SizeChanged += OnSized;
                return;
            }

            SyncWizardTitleHostWidth(viewportW);
            var outCenter = GetWizardTitleLeft(WizardTitleOutgoing, WizardTitleSlot.Center, viewportW);
            var outLeft = GetWizardTitleLeft(WizardTitleOutgoing, WizardTitleSlot.Left, viewportW);
            var inRight = GetWizardTitleLeft(WizardTitleText, WizardTitleSlot.Right, viewportW);
            var inCenter = GetWizardTitleLeft(WizardTitleText, WizardTitleSlot.Center, viewportW);

            // Start: old center @1, new right @0
            Canvas.SetLeft(WizardTitleOutgoing, outCenter);
            WizardTitleOutgoing.Opacity = 1;
            Canvas.SetLeft(WizardTitleText, inRight);
            WizardTitleText.Opacity = 0;

            var outMove = new DoubleAnimation(outCenter, outLeft, WizardTitleAnimDuration);
            var inMove = new DoubleAnimation(inRight, inCenter, WizardTitleAnimDuration);
            var outFade = new DoubleAnimation(1, 0, WizardTitleAnimDuration);
            var inFade = new DoubleAnimation(0, 1, WizardTitleAnimDuration);

            Storyboard.SetTarget(outMove, WizardTitleOutgoing);
            Storyboard.SetTargetProperty(outMove, new PropertyPath(Canvas.LeftProperty));
            Storyboard.SetTarget(inMove, WizardTitleText);
            Storyboard.SetTargetProperty(inMove, new PropertyPath(Canvas.LeftProperty));
            Storyboard.SetTarget(outFade, WizardTitleOutgoing);
            Storyboard.SetTargetProperty(outFade, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(inFade, WizardTitleText);
            Storyboard.SetTargetProperty(inFade, new PropertyPath(UIElement.OpacityProperty));

            var sb = new Storyboard { FillBehavior = FillBehavior.HoldEnd };
            sb.Children.Add(outMove);
            sb.Children.Add(inMove);
            sb.Children.Add(outFade);
            sb.Children.Add(inFade);
            sb.Completed += (_, _) =>
            {
                _wizardTitleStoryboard = null;
                WizardTitleOutgoing.Visibility = Visibility.Collapsed;
                PlaceWizardTitle(WizardTitleText, WizardTitleSlot.Center, 1, viewportW);
                WizardTitleOutgoing.Opacity = 1;
            };
            _wizardTitleStoryboard = sb;
            sb.Begin();
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BeginTransition);
    }

    private enum WizardTitleSlot
    {
        Left,
        Center,
        Right,
    }

    private static double MeasureWizardTitleWidth(TextBlock title)
    {
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(1, title.DesiredSize.Width);
    }

    private static double GetWizardTitleLeft(TextBlock title, WizardTitleSlot slot, double viewportW)
    {
        var w = MeasureWizardTitleWidth(title);
        return slot switch
        {
            WizardTitleSlot.Left => 0,
            WizardTitleSlot.Center => Math.Max(0, (viewportW - w) / 2),
            WizardTitleSlot.Right => Math.Max(0, viewportW - w),
            _ => 0,
        };
    }

    private static void PlaceWizardTitle(TextBlock title, WizardTitleSlot slot, double opacity, double viewportW)
    {
        Canvas.SetLeft(title, GetWizardTitleLeft(title, slot, viewportW));
        title.Opacity = opacity;
    }

    private bool TryGetWizardTitleViewportWidth(out double viewportW)
    {
        WizardHeaderPanel.UpdateLayout();
        WizardTitleViewport.UpdateLayout();
        viewportW = WizardTitleViewport.ActualWidth;
        if (viewportW < 8)
        {
            viewportW = 0;
            return false;
        }

        return true;
    }

    private void SyncWizardTitleHostWidth(double viewportW) =>
        WizardTitleHost.Width = viewportW;

    private void ScheduleWizardTitlePlacement(TextBlock title, WizardTitleSlot slot, double opacity)
    {
        void Place()
        {
            if (!TryGetWizardTitleViewportWidth(out var viewportW))
                return;
            SyncWizardTitleHostWidth(viewportW);
            PlaceWizardTitle(title, slot, opacity, viewportW);
        }

        Place();
        if (WizardTitleViewport.ActualWidth >= 8)
            return;

        void OnSized(object? s, SizeChangedEventArgs e)
        {
            if (!TryGetWizardTitleViewportWidth(out _))
                return;
            WizardTitleViewport.SizeChanged -= OnSized;
            Place();
        }

        WizardTitleViewport.SizeChanged += OnSized;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
    }

    private void ApplyLoginGateUi()
    {
        var block = _step == WizardStep.Step2Login && !_loginPageReady;
        WizardGptLoginBlocker.Visibility = block ? Visibility.Visible : Visibility.Collapsed;
        if (GptWebView is not null)
            GptWebView.IsHitTestVisible = !block;

        if (_step == WizardStep.Step2Login)
            WizardPrimaryButton.IsEnabled = _loginPageReady && !_sendInProgress;
    }

    private async Task<bool> QueryLoginChatReadyAsync()
    {
        if (!_webViewInitialized || GptCore is null)
            return false;
        if (!_pipelineInjected)
            await EnsurePipelineInjectedAsync().ConfigureAwait(true);
        if (!_pipelineInjected)
            return false;
        var raw = await ExecuteWebViewScriptAsync(LoginChatReadyScript).ConfigureAwait(true);
        return string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshLoginPageReadyAsync()
    {
        if (_step != WizardStep.Step2Login)
            return;
        try
        {
            var ready = await QueryLoginChatReadyAsync().ConfigureAwait(true);
            if (ready == _loginPageReady)
                return;
            _loginPageReady = ready;
            await Dispatcher.InvokeAsync(ApplyLoginGateUi);
        }
        catch
        {
            if (_loginPageReady)
            {
                _loginPageReady = false;
                await Dispatcher.InvokeAsync(ApplyLoginGateUi);
            }
        }
    }

    private void UpdateGptSideToolsVisibility() =>
        GptSideToolStack.Visibility = _step == WizardStep.Main && !_settingsViewActive
            ? Visibility.Visible
            : Visibility.Collapsed;

    private async void GptImageButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunSnipThenAsync(attachImage: true).ConfigureAwait(true);

    private async void GptTextButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            && _interviewSessionActive
            && _step == WizardStep.Main)
        {
            await PasteCurrentDraftToComposerAsync().ConfigureAwait(true);
            return;
        }

        await RunSnipThenAsync(attachImage: false).ConfigureAwait(true);
    }

    private async Task PasteCurrentDraftToComposerAsync()
    {
        if (!_webViewInitialized || GptCore is null)
        {
            ToastService.Show("ChatGPT panel is not ready yet.", ToastLevel.Warning);
            return;
        }

        var draft = (_interview.GetDraftTail() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(draft))
        {
            ToastService.Show("No live caption draft to paste.", ToastLevel.Warning);
            return;
        }

        var payload = ToastMessages.FormatCapturedTextForPaste(draft);
        var result = await PasteTextToComposerAsync(payload).ConfigureAwait(true);
        if (result?.Ok == true)
            ToastService.Show("Live caption pasted into prompt.", ToastLevel.Success);
        else
            ToastService.Show(ToastMessageForTextPaste(result), ToastLevel.Warning);
    }

    private async Task RunSnipThenAsync(bool attachImage)
    {
        if (_snipInProgress)
            return;

        if (_step != WizardStep.Main || !_webViewInitialized || GptCore is null)
        {
            ToastService.Show("Snip is available in the main interview view.", ToastLevel.Warning);
            return;
        }

        _snipCaptureCts?.Cancel();
        _snipCaptureCts?.Dispose();
        _snipCaptureCts = new CancellationTokenSource();
        var snipCt = _snipCaptureCts.Token;

        _snipInProgress = true;
        GptImageButton.IsEnabled = false;
        GptTextButton.IsEnabled = false;
        var wasTopmost = Topmost;
        var pipelineWarm = EnsurePipelineInjectedAsync();
        try
        {
            var png = await CaptureScreenSnipAsync(forTextOcr: !attachImage, snipCt).ConfigureAwait(true);
            await pipelineWarm.ConfigureAwait(true);
            if (png is null || png.Length == 0)
            {
                ToastService.Show("Snip cancelled.", ToastLevel.Warning);
                return;
            }

            Topmost = wasTopmost;
            GptWebView.Focus();

            if (attachImage)
            {
                var b64 = Convert.ToBase64String(png);
                var result = await AttachImageToComposerAsync(b64).ConfigureAwait(true);
                if (result?.Ok == true)
                    ToastService.Show("Image added to prompt. Type more or send when ready.", ToastLevel.Success);
                else
                    ToastService.Show(ToastMessageForImageAttach(result), ToastLevel.Warning);
                return;
            }

            ToastService.Clear();

            SnipOcrResult ocr;
            try
            {
                ocr = await Task.Run(() => SnipImageOcr.ExtractAsync(png)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
                return;
            }

            // Paste only text recognized from the snip image — never clipboard or toast strings.
            var text = (ocr.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                var msg = !ocr.EngineAvailable
                    ? ocr.Hint ?? "Windows OCR is not installed."
                    : ocr.Hint ?? "No text in snip. Select on-screen text only (not this app or IDE chat).";
                ToastService.Show(ToastMessages.Trim(msg), ToastLevel.Warning);
                return;
            }

            var pastePayload = ToastMessages.FormatCapturedTextForPaste(text);
            var pasteResult = await PasteTextToComposerAsync(pastePayload).ConfigureAwait(true);
            if (pasteResult?.Ok == true)
                ToastService.Show("Text pasted into prompt.", ToastLevel.Success);
            else
                ToastService.Show(ToastMessageForTextPaste(pasteResult), ToastLevel.Warning);
        }
        catch (Exception ex)
        {
            ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
        }
        finally
        {
            Topmost = wasTopmost;
            GptImageButton.IsEnabled = true;
            GptTextButton.IsEnabled = true;
            _snipInProgress = false;
            _snipCaptureCts?.Dispose();
            _snipCaptureCts = null;
        }
    }

    private async void GptFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            OpenInterviewTranscriptsFolder();
            return;
        }

        if (_folderCombineBusy)
            return;

        if (!_webViewInitialized || GptCore is null)
        {
            ToastService.Show("ChatGPT panel is not ready yet.", ToastLevel.Warning);
            return;
        }

        var dlg = new VistaFolderBrowserDialog
        {
            Description =
                "Choose a project folder. Text/code files are combined into one .txt snapshot and uploaded to the prompt (nothing is sent).",
            Multiselect = false,
            UseDescriptionForTitle = true,
        };

        if (dlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dlg.SelectedPath))
            return;

        var selectedPath = dlg.SelectedPath;

        _folderCombineBusy = true;
        GptFolderButton.IsEnabled = false;
        try
        {
            Directory.CreateDirectory(SnapshotExportDirectory);
            var label = SafeExportFileLabel(selectedPath);
            var outName = $"project-snapshot-{label}-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
            var outPath = Path.Combine(SnapshotExportDirectory, outName);

            ProjectDirectoryCombine.FileExportResult export;
            try
            {
                export = await Task.Run(() => ProjectDirectoryCombine.ExportCombinedToFile(selectedPath, outPath))
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
                return;
            }

            if (!string.IsNullOrEmpty(export.TruncateNote) && export.FilesIncluded == 0)
            {
                ToastService.Show(ToastMessages.Trim(export.TruncateNote), ToastLevel.Warning);
                return;
            }

            var folderDisplay =
                Path.GetFileName(selectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (export.FilesIncluded == 0)
            {
                ToastService.Show(ToastMessages.ForProjectCombineToast(0, false, folderDisplay), ToastLevel.Warning);
                return;
            }

            try
            {
                _ = new FileInfo(outPath).Length;
            }
            catch (Exception ex)
            {
                ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
                return;
            }

            // Fetch snapshot from host-mapped HTTPS URL inside WebView (same-origin-friendly), build File, attach like PNG.
            var attachResult = await AttachSnapshotFromExportViaVirtualHostAsync(Path.GetFileName(outPath), "text/plain")
                .ConfigureAwait(true);
            if (attachResult?.Ok != true)
            {
                attachResult = await AttachSnapshotFromExportViaVirtualHostAsync(
                        Path.GetFileName(outPath),
                        "application/octet-stream")
                    .ConfigureAwait(true);
            }

            if (attachResult?.Ok != true)
            {
                TryOpenExplorerSelectFile(outPath);
                ToastService.Show(
                    ToastMessages.Trim(
                        $"{ToastMessageForFileAttach(attachResult)} Explorer opened on the .txt — drag it into ChatGPT if needed."),
                    ToastLevel.Warning);
                return;
            }

            var extraAttach = export.Truncated && !string.IsNullOrEmpty(export.TruncateNote)
                ? $" {ToastMessages.Trim(export.TruncateNote)}"
                : "";
            ToastService.Show(
                ToastMessages.Trim($"Snapshot uploaded to prompt ({export.FilesIncluded} files).{extraAttach}"),
                ToastLevel.Success);
        }
        finally
        {
            _folderCombineBusy = false;
            GptFolderButton.IsEnabled = true;
        }
    }

    private static string SafeExportFileLabel(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
            name = "project";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return name.Length > 40 ? name[..40] : name;
    }

    private static bool TryOpenExplorerSelectFile(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + filePath + "\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OpenInterviewTranscriptsFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(InterviewHistory.DefaultSavePath())!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
        }
    }

    private async void GptCopyResultButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _gptCopyInFlight, 1, 0) != 0)
            return;

        try
        {
            var text = await ResolveLatestGptResultTextAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastService.Show("No GPT reply to copy yet.", ToastLevel.Warning);
                return;
            }

            if (await ClipboardHelper.TrySetTextAsync(text).ConfigureAwait(true))
                ToastService.Show("Copied.", ToastLevel.Success);
            else
                ToastService.Show("Copy failed. Clipboard busy — try again.", ToastLevel.Warning);
        }
        catch (Exception ex)
        {
            ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _gptCopyInFlight, 0);
        }
    }

    private async Task<string> ResolveLatestGptResultTextAsync()
    {
        if (!string.IsNullOrWhiteSpace(_latestGptAnswer))
            return _latestGptAnswer;

        if (!_webViewInitialized || GptCore is null)
            return "";

        if (!_pipelineInjected)
            await EnsurePipelineInjectedAsync().ConfigureAwait(true);

        var raw = await ExecuteWebViewScriptAsync(
                "(() => { try { if (typeof __iaWpfGetLatestAssistantText === 'function') return __iaWpfGetLatestAssistantText(); if (typeof __iaRefreshLatestAnswer === 'function') return __iaRefreshLatestAnswer('', 1); return ''; } catch(_e) { return ''; } })()")
            .ConfigureAwait(true);
        return UnquoteWebViewJsonString(raw);
    }

    private static string UnquoteWebViewJsonString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var t = raw.Trim();
        try
        {
            var parsed = JsonSerializer.Deserialize<string>(t);
            return (parsed ?? "").Trim();
        }
        catch
        {
            if (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
                return t[1..^1].Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\"", "\"").Trim();
            return t.Trim();
        }
    }

    private async void LoginPollTimer_OnTick(object? sender, EventArgs e) =>
        await RefreshLoginPageReadyAsync().ConfigureAwait(true);

    private async void Step1Continue_OnClick(object sender, RoutedEventArgs e)
    {
        ResumeJdStore.Save(ResumeTextBox.Text, JdTextBox.Text);
        _step = WizardStep.Step2Login;
        await EnsureWebViewAsync().ConfigureAwait(true);
        ApplyWizardUi();
    }

    private async void WizardPrimary_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sendInProgress)
            return;
        switch (_step)
        {
            case WizardStep.Step2Login:
                if (!_loginPageReady)
                {
                    ToastService.Show(
                        "Wait for ChatGPT to finish loading, then log in and open a chat.",
                        ToastLevel.Info);
                    return;
                }

                _loginPollTimer.Stop();
                _step = WizardStep.Step3ResumeSummary;
                ApplyWizardUi();
                break;
            case WizardStep.Step3ResumeSummary:
                await RunPrepSendAsync(PrepSendKind.Resume).ConfigureAwait(true);
                break;
            case WizardStep.Step4JdSummary:
                await RunPrepSendAsync(PrepSendKind.Jd).ConfigureAwait(true);
                break;
            case WizardStep.Step5Interview:
                await RunPrepSendAsync(PrepSendKind.InitialInterview).ConfigureAwait(true);
                break;
        }
    }

    private void WizardSkip_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sendInProgress)
            return;
        switch (_step)
        {
            case WizardStep.Step3ResumeSummary:
            case WizardStep.Step4JdSummary:
                _step = (WizardStep)((int)_step + 1);
                ApplyWizardUi();
                break;
            case WizardStep.Step5Interview:
                _step = WizardStep.Main;
                ApplyWizardUi();
                break;
        }
    }

    private void ApplyWizardSendStepButtonsEnabled()
    {
        var enabled = !_sendInProgress;
        WizardPrimaryButton.IsEnabled = enabled;
        WizardSkipButton.IsEnabled = enabled;
    }

    private void SetWizardFooterButtonsEnabled(bool enabled)
    {
        WizardPrimaryButton.IsEnabled = enabled;
        WizardSkipButton.IsEnabled = enabled;
    }

    private async Task RunPrepSendAsync(PrepSendKind kind)
    {
        if (_sendInProgress || !_webViewInitialized || GptCore is null)
            return;
        if (_step == WizardStep.Step2Login && !await QueryLoginChatReadyAsync().ConfigureAwait(true))
        {
            ToastService.Show(
                "ChatGPT is still loading. Log in and open a chat before continuing.",
                ToastLevel.Info);
            return;
        }

        _sendInProgress = true;
        SetWizardFooterButtonsEnabled(false);
        await Dispatcher.Yield(DispatcherPriority.Render);
        try
        {
            if (!_pipelineInjected)
                await EnsurePipelineInjectedAsync().ConfigureAwait(true);
            if (!_pipelineInjected)
            {
                ToastService.Show("Send failed. Pipeline missing.", ToastLevel.Warning);
                return;
            }

            var prompt = BuildPrepPrompt(kind);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                ToastService.Show("Send failed. Missing template or resume/JD.", ToastLevel.Warning);
                return;
            }

            var requestId = Guid.NewGuid().ToString("N");
            Trace.WriteLine($"[InterviewAssistant][send][{requestId[..8]}] RunPrepSendAsync start step={_step} kind={kind}");
            var tcs = new TaskCompletionSource<GptSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _prepSendCompletes[requestId] = tcs;
            try
            {
                var script = await BuildStartSendOnlyScriptAsync(requestId, prompt).ConfigureAwait(true);
                var starterRaw = await ExecuteWebViewScriptAsync(script).ConfigureAwait(true);
                LogWebViewScriptResult(starterRaw);

                var delay = Task.Delay(TimeSpan.FromMinutes(5));
                var completed = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(true);
                if (ReferenceEquals(completed, delay))
                {
                    if (_prepSendCompletes.TryRemove(requestId, out var pending))
                    {
                        pending.TrySetResult(new GptSendResult
                        {
                            Ok = false,
                            Error =
                                "send_confirmation_timeout: ChatGPT did not finish within 6 minutes (no ia_send_result from the page).",
                        });
                    }
                }

                var parsed = await tcs.Task.ConfigureAwait(true);
                Trace.WriteLine(
                    $"[InterviewAssistant][send][{requestId[..8]}] result ok={parsed.Ok} phase={parsed.Phase ?? "(null)"} error={parsed.Error ?? "(none)"}");
                var sendConfirmed = parsed.Ok
                    && string.Equals(parsed.Phase, "sent", StringComparison.OrdinalIgnoreCase);
                if (sendConfirmed)
                {
                    if (_step == WizardStep.Step5Interview)
                        _step = WizardStep.Main;
                    else
                        _step = (WizardStep)((int)_step + 1);
                    _sendInProgress = false;
                    ApplyWizardUi();
                }
                else
                {
                    ToastService.Show(ToastMessages.ForSendFailure(parsed), ToastLevel.Warning);
                }
            }
            finally
            {
                _prepSendCompletes.TryRemove(requestId, out _);
            }
        }
        catch (Exception ex)
        {
            ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
        }
        finally
        {
            if (_sendInProgress)
            {
                _sendInProgress = false;
                if (_step is WizardStep.Step3ResumeSummary or WizardStep.Step4JdSummary or WizardStep.Step5Interview)
                    ApplyWizardSendStepButtonsEnabled();
            }
        }
    }

    private string BuildPrepPrompt(PrepSendKind kind)
    {
        return kind switch
        {
            PrepSendKind.Resume => PromptTemplateResolver.BuildResumePrompt(
                ResumeTextBox.Text ?? "",
                PromptTemplateResolver.TryReadResumeTemplate()),
            PrepSendKind.Jd => PromptTemplateResolver.BuildJdPrompt(
                JdTextBox.Text ?? "",
                PromptTemplateResolver.TryReadJdTemplate()),
            PrepSendKind.InitialInterview => PromptTemplateResolver.TryReadInitialInterviewTemplate().Trim(),
            _ => "",
        };
    }

    private async Task<string> BuildStartSendOnlyScriptAsync(
        string requestId,
        string prompt,
        bool appendToComposer = false)
    {
        var rid = JsonSerializer.Serialize(requestId);
        var arg = JsonSerializer.Serialize(prompt);
        var appendLit = appendToComposer ? "true" : "false";
        var tail =
            $"if(typeof window.__iaWpfStartSendOnly!==\"function\"){{throw new Error(\"__iaWpfStartSendOnly_missing\");}}" +
            $"window.__iaWpfStartSendOnly({rid},{arg},{appendLit});";
        return await ComposePipelineScriptAsync(tail).ConfigureAwait(false);
    }

    private async Task<string> ComposePipelineScriptAsync(string tail)
    {
        if (_pipelineInjected)
            return tail;
        var path = Path.Combine(AppContext.BaseDirectory, "GptSendPipeline.js");
        var js = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        return $"{js}\n{tail}";
    }

    private static void LogWebViewScriptResult(string? raw)
    {
        var line = "[InterviewAssistant] WebView ExecuteScriptAsync raw: " + (raw ?? "(null)");
        Trace.WriteLine(line);
        Debug.WriteLine(line);
    }

    private static void LogSendDebugMessage(JsonElement root)
    {
        var requestId = root.TryGetProperty("requestId", out var rid) && rid.ValueKind == JsonValueKind.String
            ? rid.GetString() ?? ""
            : "";
        var stage = root.TryGetProperty("stage", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() ?? ""
            : "";
        var detail = root.TryGetProperty("detail", out var det) ? det.GetRawText() : "{}";
        var ridShort = requestId.Length <= 8 ? requestId : requestId[..8];
        var line = $"[InterviewAssistant][send][{ridShort}]{stage} {detail}";
        Trace.WriteLine(line);
        Debug.WriteLine(line);
    }

    private void Step1Panel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDescendantOfInteractiveEditorOrChrome(e.OriginalSource as DependencyObject))
            return;
        DragRegion_OnMouseLeftButtonDown(sender, e);
    }

    private static bool IsDescendantOfInteractiveEditorOrChrome(DependencyObject? source)
    {
        while (source != null)
        {
            switch (source)
            {
                case TextBox:
                case Slider:
                case ButtonBase:
                    return true;
                default:
                    source = VisualTreeHelper.GetParent(source);
                    break;
            }
        }

        return false;
    }

    private void DragRegion_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        try
        {
            DragMove();
        }
        catch
        {
            // ignore
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e)
    {
        LeaveMainInterviewMode();
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        StartupDiagnostics.Log("MainWindow: OnClosed (window closed — app will exit if last window)");
        LeaveMainInterviewMode();
        _captureStealthMonitor?.Dispose();
        _captureStealthMonitor = null;
        _clickThroughController?.Dispose();
        _clickThroughController = null;
        _clickThroughHotkeys.Dispose();
        _stealthHotkeys.Dispose();
        _snipHotkeys.Dispose();
        _opacityHotkeys.Dispose();
        _hotkeys.Dispose();
        _interview.Dispose();
        base.OnClosed(e);
    }
}
