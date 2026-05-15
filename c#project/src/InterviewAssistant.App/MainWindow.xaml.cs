using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.Ui;
using InterviewAssistant.Bridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

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
    private bool _webViewInitialized;
    private bool _pipelineInjected;
    private bool _sendInProgress;
    private readonly string _webViewUserDataDir;
    private readonly DispatcherTimer _loginPollTimer;
    private double _lastChatPageOpacity01 = 1.0;
    private readonly SemaphoreSlim _webViewScriptGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GptSendResult>> _prepSendCompletes = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GptSendResult>> _attachCompletes = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GptSendResult>> _pasteCompletes = new();
    private static readonly JsonSerializerOptions sGptSendJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly InterviewSessionCoordinator _interview;
    private bool _interviewSessionActive;
    private bool _settingsViewActive;
    private bool _chunkSendInProgress;
    private bool _snipInProgress;
    private readonly string? _startupToastMessage;
    private readonly ToastLevel _startupToastLevel;
    private string _latestGptAnswer = "";
    private static int _gptCopyInFlight;

    public MainWindow(PromptStore promptStore, string bridgeHost, int bridgePort, string? startupToastMessage = null, ToastLevel startupToastLevel = ToastLevel.Warning)
    {
        InitializeComponent();
        ToastService.Register(AppToastHost);
        _startupToastMessage = startupToastMessage;
        _startupToastLevel = startupToastLevel;
        _interview = new InterviewSessionCoordinator(promptStore, bridgeHost, bridgePort);
        WireInterviewSession();
        InitInterviewTopBarIcons();
        InterviewSettingsPanel.Bind(_interview.ModePrompts);
        InterviewSettingsPanel.BackRequested += (_, _) => ShowInterviewSettings(false);
        CaptionFeed.CopyPromptBuilder = text =>
        {
            var (_, finalPrompt) = ChunkPromptBuilder.Build(text, _interview.ModePrompts.GetActiveTemplate());
            return string.IsNullOrWhiteSpace(finalPrompt) ? null : finalPrompt;
        };

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
        ApplyShellPrepChrome();
        GptCopyResultIconHost.Child = TopBarIcons.CreateCopyGlyphIcon(14, "#111111");
        GptImageIconHost.Child = TopBarIcons.CreateImageIcon(14, "#111111");
        GptTextIconHost.Child = TopBarIcons.CreateTextGlyphIcon(14, "#111111");
        GptFolderIconHost.Child = TopBarIcons.CreateFolderIcon(14, "#111111");
        ApplyWizardUi();
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
                    var (_, clip) = ChunkPromptBuilder.Build(text, _interview.ModePrompts.GetActiveTemplate());
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
        SessionModeIconHost.Child = TopBarIcons.CreateReadModeIcon(16);
        SaveTranscriptIconHost.Child = TopBarIcons.CreateSaveIcon(16);
        SettingsNavIconHost.Child = TopBarIcons.CreateKebabIcon(16);
        UpdateSessionModeButtonUi("read");
    }

    private void UpdateSessionModeButtonUi(string mode)
    {
        _interview.ModePrompts.SessionMode = mode;
        SessionModeLabel.Text = char.ToUpper(mode[0]) + mode[1..];
        SessionModeIconHost.Child = mode switch
        {
            "type" => TopBarIcons.CreateTypeModeIcon(16),
            "behavioral" => TopBarIcons.CreateBehavioralModeIcon(16),
            _ => TopBarIcons.CreateReadModeIcon(16),
        };
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
        UpdateSessionModeButtonUi(_interview.ModePrompts.SessionMode);
    }

    private void LeaveMainInterviewMode()
    {
        if (!_interviewSessionActive)
            return;
        _interview.Stop();
        _interviewSessionActive = false;
        InterviewTopBarPanel.Visibility = Visibility.Collapsed;
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

    private void SessionModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.ContextMenu is not null)
        {
            b.ContextMenu.PlacementTarget = b;
            b.ContextMenu.IsOpen = true;
        }
    }

    private void SessionModeMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string mode)
            return;
        UpdateSessionModeButtonUi(mode);
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
        if (GptWebView.CoreWebView2 is null)
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
        await Task.Delay(120).ConfigureAwait(true);

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
            GptWebView.CoreWebView2.PostWebMessageAsJson(payload);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(true);
            if (completed != tcs.Task)
                return new GptSendResult { Ok = false, Error = "attach_timeout" };

            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            _attachCompletes.TryRemove(requestId, out _);
        }
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

    private async Task<GptSendResult?> PasteTextToComposerAsync(string text, bool append = true)
    {
        if (GptWebView.CoreWebView2 is null)
            return new GptSendResult { Ok = false, Error = "webview_not_ready" };

        if (!await EnsurePipelineInjectedAsync().ConfigureAwait(true))
            return new GptSendResult { Ok = false, Error = "paste_missing" };

        GptWebView.Focus();
        await Task.Delay(150).ConfigureAwait(true);

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
        if (GptWebView.CoreWebView2 is null)
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
            GptWebView.CoreWebView2.PostWebMessageAsJson(payload);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(true);
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

    private async Task<byte[]?> CaptureScreenSnipAsync(bool forTextOcr = false)
    {
        Topmost = false;
        ToastService.Clear();
        var hint = forTextOcr
            ? "Snip on-screen text only (not this app or chat)."
            : "Snip: Win+Shift+S, then select an area.";
        ToastService.Show(hint, ToastLevel.Info);
        return await WindowsScreenSnipCapture.CaptureViaOsSnipAsync(Dispatcher).ConfigureAwait(true);
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

        if (!_webViewInitialized || GptWebView.CoreWebView2 is null)
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
        if (!_webViewInitialized || GptWebView.CoreWebView2 is null)
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
        if (GptWebView.CoreWebView2 is null)
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
        ApplyContentOpacity(OpacitySlider.Value / 100.0);
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_startupToastMessage))
            ToastService.Show(_startupToastMessage, _startupToastLevel);
        _ = EnsureWebViewAsync();
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewInitialized)
            return;
        try
        {
            var env = await CoreWebView2Environment
                .CreateAsync(userDataFolder: _webViewUserDataDir)
                .ConfigureAwait(true);
            GptWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);
            await GptWebView.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            GptWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            GptWebView.CoreWebView2.WebMessageReceived += CoreWebView2_OnWebMessageReceived;
            GptWebView.NavigationCompleted += GptWebView_OnNavigationCompleted;
            GptWebView.Source = new Uri("https://chatgpt.com/");
            _webViewInitialized = true;
            await PushChatGptDomOpacityAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
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
        if (!e.IsSuccess || GptWebView.CoreWebView2 is null)
            return;
        var uri = GptWebView.Source?.ToString() ?? "";
        if (!uri.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase))
            return;
        _pipelineInjected = false;
        try
        {
            await EnsurePipelineInjectedAsync().ConfigureAwait(true);
            await PushChatGptDomOpacityAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
    }

    private async Task<bool> EnsurePipelineInjectedAsync()
    {
        if (GptWebView.CoreWebView2 is null)
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
                WizardHeaderPanel.Visibility = Visibility.Collapsed;
                WizardFooterPanel.Visibility = Visibility.Collapsed;
                CaptionPanel.Visibility = Visibility.Collapsed;
                CaptionRowDefinition.Height = new GridLength(0);
                ContentMiddleRowDefinition.Height = new GridLength(1, GridUnitType.Star);
                WizardFooterRowDefinition.Height = new GridLength(0);
                Grid.SetRow(GptWebViewOpacityHost, 1);
                Grid.SetRowSpan(GptWebViewOpacityHost, 1);
                ApplyShellPrepChrome();
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
                        WizardTitleText.Text = "Log In and Open Chat";
                        WizardPrimaryButton.Content = "Continue";
                        WizardPrimaryButton.IsEnabled = false;
                        _loginPollTimer.Start();
                        break;
                    case WizardStep.Step3ResumeSummary:
                        WizardTitleText.Text = "Send Resume";
                        WizardPrimaryButton.Content = "Send";
                        ApplyWizardSendStepButtonsEnabled();
                        break;
                    case WizardStep.Step4JdSummary:
                        WizardTitleText.Text = "Send Job Description";
                        WizardPrimaryButton.Content = "Send";
                        ApplyWizardSendStepButtonsEnabled();
                        break;
                    case WizardStep.Step5Interview:
                        WizardTitleText.Text = "Prepare for Interview";
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

        UpdateGptSideToolsVisibility();
        if (_webViewInitialized && GptWebViewOpacityHost.Visibility == Visibility.Visible)
            _ = PushChatGptDomOpacityAsync();
    }

    private void UpdateGptSideToolsVisibility() =>
        GptSideToolStack.Visibility = _step == WizardStep.Main && !_settingsViewActive
            ? Visibility.Visible
            : Visibility.Collapsed;

    private async void GptImageButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunSnipThenAsync(attachImage: true).ConfigureAwait(true);

    private async void GptTextButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunSnipThenAsync(attachImage: false).ConfigureAwait(true);

    private async Task RunSnipThenAsync(bool attachImage)
    {
        if (_snipInProgress)
            return;

        if (_step != WizardStep.Main || !_webViewInitialized || GptWebView.CoreWebView2 is null)
        {
            ToastService.Show("Snip is available in the main interview view.", ToastLevel.Warning);
            return;
        }

        _snipInProgress = true;
        GptImageButton.IsEnabled = false;
        GptTextButton.IsEnabled = false;
        var wasTopmost = Topmost;
        try
        {
            var png = await CaptureScreenSnipAsync(forTextOcr: !attachImage).ConfigureAwait(true);
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
            Trace.WriteLine($"[InterviewAssistant][ocr] raw chars={text.Length}");
            Trace.WriteLine($"[InterviewAssistant][paste] payload={pastePayload}");
            ToastService.Show(ToastMessages.ForSnipRecognizedToast(text), ToastLevel.Info);

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
            if (Topmost != wasTopmost)
                Topmost = wasTopmost;
            GptImageButton.IsEnabled = true;
            GptTextButton.IsEnabled = true;
            _snipInProgress = false;
        }
    }

    private void GptFolderButton_OnClick(object sender, RoutedEventArgs e)
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

        if (!_webViewInitialized || GptWebView.CoreWebView2 is null)
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

    private async void LoginPollTimer_OnTick(object? sender, EventArgs e)
    {
        if (_step != WizardStep.Step2Login || !_webViewInitialized || GptWebView.CoreWebView2 is null)
            return;
        try
        {
            if (!_pipelineInjected)
                await EnsurePipelineInjectedAsync().ConfigureAwait(true);
            if (!_pipelineInjected)
                return;
            var raw = await ExecuteWebViewScriptAsync(
                    "(() => { try { return !!__iaFindComposer(); } catch(_e) { return false; } })()")
                .ConfigureAwait(true);
            var ready = string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            WizardPrimaryButton.IsEnabled = ready;
        }
        catch
        {
            WizardPrimaryButton.IsEnabled = false;
        }
    }

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
        if (_step is WizardStep.Step3ResumeSummary or WizardStep.Step4JdSummary or WizardStep.Step5Interview)
        {
            _step = WizardStep.Main;
            ApplyWizardUi();
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
        if (_sendInProgress || !_webViewInitialized || GptWebView.CoreWebView2 is null)
            return;
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
        LeaveMainInterviewMode();
        _interview.Dispose();
        base.OnClosed(e);
    }
}
