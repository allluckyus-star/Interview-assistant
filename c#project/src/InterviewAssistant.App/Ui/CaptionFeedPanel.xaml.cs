using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShapesPath = System.Windows.Shapes.Path;

namespace InterviewAssistant.App.Ui;

/// <summary>Caption transcript as bordered rows (mirrors live.py create_row / finalize_draft).</summary>
public partial class CaptionFeedPanel : UserControl
{
    private const double BubbleWidthRatio = 1.0;
    private const double CaptionIconSize = 14;
    private const int EndpointPickerWordsPerStep = 20;
    private static readonly Brush EndpointWordBrush = new SolidColorBrush(Color.FromRgb(21, 101, 192));
    private static readonly Brush EndpointWordBgBrush = new SolidColorBrush(Color.FromRgb(227, 242, 253));
    private const double UserScrollCooldownSeconds = 2.0;
    private const double StickToBottomThreshold = 6.0;

    private readonly DispatcherTimer _captionAutoscrollTimer;
    private double _userScrollCooldownUntil;
    private int _suppressUserScrollDepth;
    private bool _scrollBarHooked;
    private static int _copyToastInFlight;

    private Grid? _draftRow;
    private Border? _draftPanel;
    private TextBlock? _draftLabel;
    private WrapPanel? _draftEndpointPicker;

    private int _endpointPickerWordCount;
    private int _endpointPickerShownCount;
    private Border? _editingPanel;
    private string _editingOriginalText = "";

    /// <summary>Builds full GPT prompt (mode template + caption) for bubble Tag after send.</summary>
    public Func<string, string?>? CopyPromptBuilder { get; set; }

    /// <summary>Snapshots pending draft chunk when opening edit on the live draft row.</summary>
    public Func<string, string>? CaptureDraftForEdit { get; set; }

    public Func<int, IReadOnlyList<Services.EndpointWordOption>>? GetEndpointWordChoices { get; set; }

    public Func<int, bool>? SetEndpointAtIndex { get; set; }

    public event EventHandler<CaptionBubbleEditEventArgs>? BubbleEditFinished;

    public bool IsEditing => _editingPanel is not null;

    public CaptionFeedPanel()
    {
        InitializeComponent();
        Scroller.ScrollChanged += Scroller_OnScrollChanged;
        Scroller.PreviewMouseWheel += (_, _) => MarkUserScrollActivity();
        _captionAutoscrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _captionAutoscrollTimer.Tick += (_, _) => TryStickToBottom();
        Loaded += CaptionFeedPanel_OnLoaded;
        Unloaded += (_, _) => _captionAutoscrollTimer.Stop();
    }

    private void CaptionFeedPanel_OnLoaded(object sender, RoutedEventArgs e)
    {
        _captionAutoscrollTimer.Start();
        HookVerticalScrollBar();
        ApplyAllBubbleWidths();
    }

    private void HookVerticalScrollBar()
    {
        if (_scrollBarHooked)
            return;
        Scroller.ApplyTemplate();
        if (Scroller.Template.FindName("PART_VerticalScrollBar", Scroller) is not ScrollBar bar)
            return;
        bar.PreviewMouseLeftButtonDown += (_, _) => MarkUserScrollActivity();
        bar.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                MarkUserScrollActivity();
        };
        _scrollBarHooked = true;
    }

    private void MarkUserScrollActivity()
    {
        if (_suppressUserScrollDepth > 0)
            return;
        _userScrollCooldownUntil = Environment.TickCount64 / 1000.0 + UserScrollCooldownSeconds;
    }

    private void Scroller_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_suppressUserScrollDepth > 0)
            return;
        if (e.ExtentHeightChange != 0)
            return;
        if (Math.Abs(e.VerticalChange) > 0.01)
            MarkUserScrollActivity();
    }

    private bool ShouldAutoScroll()
    {
        var now = Environment.TickCount64 / 1000.0;
        if (now < _userScrollCooldownUntil)
            return false;
        return true;
    }

    private bool IsNearBottom()
    {
        var offset = Scroller.VerticalOffset;
        var max = Scroller.ScrollableHeight;
        return max <= 0 || (max - offset) <= StickToBottomThreshold;
    }

    private void TryStickToBottom()
    {
        if (!ShouldAutoScroll())
            return;
        ScrollToEnd(force: true);
    }

    public static string FormatInterviewerCopyText(string caption)
    {
        var body = (caption ?? "").Trim();
        if (string.IsNullOrEmpty(body) || body == " ")
            return "";
        return $"Interviewer said:\n\"\"\"\n{body}\n\"\"\"";
    }

    public void Clear()
    {
        CancelActiveEdit(restoreOriginal: true);
        BubbleStack.Children.Clear();
        _draftRow = null;
        _draftPanel = null;
        _draftLabel = null;
        _draftEndpointPicker = null;
        _endpointPickerWordCount = 0;
        _endpointPickerShownCount = 0;
    }

    public void UpdateDraft(string text, string? copyPrompt = null)
    {
        if (_draftRow is null)
            CreateNewEmptyDraft();

        if (_draftLabel is null)
            return;

        _draftLabel.Text = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();

        if (!string.IsNullOrEmpty(copyPrompt))
            _draftPanel!.Tag = copyPrompt;

        ApplyBubbleWidth(_draftPanel!);
        ScrollToEnd();
    }

    public void ClearDraft()
    {
        if (_draftRow is not null)
            BubbleStack.Children.Remove(_draftRow);
        _draftRow = null;
        _draftPanel = null;
        _draftLabel = null;
        _draftEndpointPicker = null;
        _endpointPickerWordCount = 0;
        _endpointPickerShownCount = 0;
    }

    public void SpawnNewDraftWhileEditing() => CreateNewEmptyDraft();

    private void CreateNewEmptyDraft()
    {
        if (_draftRow is not null)
            return;

        (_draftRow, _draftPanel, _draftLabel, _draftEndpointPicker) = CreateBubbleRow(
            alignRight: true,
            withCopy: true,
            editable: true,
            isLiveDraft: true);
        _endpointPickerWordCount = 0;
        _endpointPickerShownCount = 0;
        _draftLabel.Text = " ";
        BubbleStack.Children.Add(_draftRow);
        ApplyBubbleWidth(_draftPanel!);
        ScrollToEnd(force: true);
    }

    public void FinalizeDraft(string text, string? copyPrompt)
    {
        if (_draftRow is not null && _draftLabel is not null && _draftPanel is not null)
        {
            _draftLabel.Text = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
            if (!string.IsNullOrEmpty(copyPrompt))
                _draftPanel.Tag = copyPrompt;
            ApplyBubbleWidth(_draftPanel);
            _draftRow = null;
            _draftPanel = null;
            _draftLabel = null;
            _draftEndpointPicker = null;
            _endpointPickerWordCount = 0;
            _endpointPickerShownCount = 0;
            ScrollToEnd();
            return;
        }

        AddInterviewerBubble(text, copyPrompt);
    }

    public void AddInterviewerBubble(string text, string? copyPrompt)
    {
        var (row, panel, label, _) = CreateBubbleRow(
            alignRight: true,
            withCopy: true,
            editable: true,
            isLiveDraft: false);
        label.Text = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
        if (!string.IsNullOrEmpty(copyPrompt))
            panel.Tag = copyPrompt;
        BubbleStack.Children.Add(row);
        ApplyBubbleWidth(panel);
        ScrollToEnd();
    }

    public void AddGptBubble(string text)
    {
        var (row, panel, label, _) = CreateBubbleRow(
            alignRight: false,
            withCopy: false,
            editable: false,
            isLiveDraft: false);
        label.Text = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
        BubbleStack.Children.Add(row);
        ApplyBubbleWidth(panel);
        ScrollToEnd();
    }

    public void SetBubbleCopyPrompt(Border panel, string? copyPrompt)
    {
        if (!string.IsNullOrEmpty(copyPrompt))
            panel.Tag = copyPrompt;
    }

    private static Button CreateCaptionIconButton(ShapesPath icon, string toolTip, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Content = new Viewbox
            {
                Width = CaptionIconSize,
                Height = CaptionIconSize,
                Child = icon,
            },
        };
        btn.Click += (s, e) =>
        {
            e.Handled = true;
            click(s, e);
        };
        btn.MouseEnter += (_, _) => btn.Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));
        btn.MouseLeave += (_, _) => btn.Background = Brushes.Transparent;
        return btn;
    }

    private (Grid Row, Border Panel, TextBlock Label, WrapPanel? EndpointPicker) CreateBubbleRow(
        bool alignRight,
        bool withCopy,
        bool editable,
        bool isLiveDraft)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var panel = new Border
        {
            MinHeight = 44,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 17, 17, 17)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 6, 0, 8),
            Cursor = editable ? Cursors.Hand : Cursors.Arrow,
        };

        if (editable)
        {
            panel.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (IsWithinCaptionChrome(e.OriginalSource as DependencyObject))
                    return;
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    if (isLiveDraft && ReferenceEquals(panel, _draftPanel))
                        BeginLatestDraftInlineEdit(panel);
                    else
                        BeginInlineEdit(panel);
                }
            };
        }

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var label = CreateCaptionLabel();
        stack.Children.Add(label);

        WrapPanel? endpointPicker = null;

        if (isLiveDraft)
        {
            var draftChrome = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            var nudgeBtn = CreateCaptionIconButton(
                TopBarIcons.CreateEndpointNudgeIcon(CaptionIconSize),
                "Show up to 20 earlier words — click one to move draft start",
                (_, _) => OnEndpointMoveClicked());
            nudgeBtn.HorizontalAlignment = HorizontalAlignment.Left;
            draftChrome.Children.Add(nudgeBtn);

            endpointPicker = new WrapPanel
            {
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 4, 0, 0),
            };
            draftChrome.Children.Add(endpointPicker);
            stack.Children.Insert(0, draftChrome);
        }

        if (withCopy)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
            };
            actions.Children.Add(CreateCaptionIconButton(
                TopBarIcons.CreateCopyGlyphIcon(CaptionIconSize),
                "Copy interviewer caption",
                (_, _) => CopyCaptionText(label)));
            stack.Children.Add(actions);
        }

        panel.Child = stack;

        Grid.SetColumn(panel, 0);
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.Children.Add(panel);

        return (row, panel, label, endpointPicker);
    }

    private void OnEndpointMoveClicked()
    {
        if (_draftEndpointPicker is null || GetEndpointWordChoices is null)
            return;

        var previousRequest = _endpointPickerWordCount;
        var newRequest = previousRequest + EndpointPickerWordsPerStep;
        var choices = GetEndpointWordChoices(newRequest);

        if (choices.Count == 0)
        {
            ToastService.Show("Already at start of live caption.", ToastLevel.Info);
            return;
        }

        if (previousRequest > 0 && choices.Count <= _endpointPickerShownCount)
        {
            ToastService.Show("No more words before draft start.", ToastLevel.Info);
            return;
        }

        _endpointPickerWordCount = newRequest;
        _endpointPickerShownCount = choices.Count;
        RebuildEndpointPicker(choices);
    }

    private void RebuildEndpointPicker(IReadOnlyList<Services.EndpointWordOption> choices)
    {
        if (_draftEndpointPicker is null)
            return;

        _draftEndpointPicker.Children.Clear();
        if (choices.Count == 0)
        {
            _draftEndpointPicker.Visibility = Visibility.Collapsed;
            return;
        }

        _draftEndpointPicker.Visibility = Visibility.Visible;
        foreach (var opt in choices)
            _draftEndpointPicker.Children.Add(CreateEndpointWordButton(opt));
    }

    private Button CreateEndpointWordButton(Services.EndpointWordOption opt)
    {
        var btn = new Button
        {
            Content = opt.Word,
            Foreground = EndpointWordBrush,
            Background = EndpointWordBgBrush,
            BorderBrush = EndpointWordBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = Cursors.Hand,
            FontSize = 14,
        };
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            if (SetEndpointAtIndex?.Invoke(opt.StartIndexInFull) != true)
                return;

            _endpointPickerWordCount = 0;
            _endpointPickerShownCount = 0;
            if (_draftEndpointPicker is not null)
            {
                _draftEndpointPicker.Children.Clear();
                _draftEndpointPicker.Visibility = Visibility.Collapsed;
            }
        };
        return btn;
    }

    private static TextBlock CreateCaptionLabel()
    {
        var label = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Left,
        };
        if (Application.Current.TryFindResource("CaptionBubbleTextStyle") is Style captionStyle)
            label.Style = captionStyle;
        else
        {
            label.FontFamily = new FontFamily("Segoe UI, Segoe UI Variable Text, sans-serif");
            label.FontSize = 16;
            label.FontWeight = FontWeights.Normal;
            label.Foreground = new SolidColorBrush(Color.FromRgb(17, 17, 17));
            label.TextWrapping = TextWrapping.Wrap;
        }

        return label;
    }

    private void BeginLatestDraftInlineEdit(Border panel)
    {
        if (_editingPanel is not null)
            return;
        if (panel.Child is not StackPanel stack)
            return;

        var label = stack.Children.OfType<TextBlock>().FirstOrDefault();
        if (label is null)
            return;

        var fallback = NormalizeCaptionDisplay(label.Text);
        var captured = CaptureDraftForEdit?.Invoke(fallback) ?? fallback;
        if (string.IsNullOrWhiteSpace(captured))
            return;

        _draftRow = null;
        _draftPanel = null;
        _draftLabel = null;
        _draftEndpointPicker = null;

        _editingPanel = panel;
        _editingOriginalText = captured;
        ShowInlineEditor(panel, label, captured);
        CreateNewEmptyDraft();
    }

    private void BeginInlineEdit(Border panel)
    {
        if (_editingPanel is not null)
            return;
        if (panel.Child is not StackPanel stack)
            return;

        var label = stack.Children.OfType<TextBlock>().FirstOrDefault();
        if (label is null)
            return;

        _editingPanel = panel;
        _editingOriginalText = NormalizeCaptionDisplay(label.Text);
        ShowInlineEditor(panel, label, _editingOriginalText);
    }

    private void ShowInlineEditor(Border panel, TextBlock label, string editorText)
    {
        var editor = new TextBox
        {
            Text = editorText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 72,
            MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = label.FontFamily,
            FontSize = 15,
            FontWeight = FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(17, 17, 17)),
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 17, 17, 17)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var rejectBtn = CreateCaptionIconButton(
            TopBarIcons.CreateCancelIcon(CaptionIconSize),
            "Reject (keep edited text, do not send)",
            (_, _) => CompleteInlineEdit(reject: true));
        rejectBtn.Margin = new Thickness(0, 0, 6, 0);

        var sendBtn = CreateCaptionIconButton(
            TopBarIcons.CreateSendPlaneIcon(CaptionIconSize),
            "Send caption + mode prompt to ChatGPT",
            (_, _) => CompleteInlineEdit(reject: false));

        actions.Children.Add(rejectBtn);
        actions.Children.Add(sendBtn);

        var editStack = new StackPanel();
        editStack.Children.Add(editor);
        editStack.Children.Add(actions);
        panel.Child = editStack;
        editor.Focus();
        editor.SelectAll();
    }

    private void CompleteInlineEdit(bool reject)
    {
        if (_editingPanel is null)
            return;

        var panel = _editingPanel;
        string text;
        if (panel.Child is StackPanel editStack)
        {
            var editor = editStack.Children.OfType<TextBox>().FirstOrDefault();
            text = (editor?.Text ?? "").Trim();
        }
        else
        {
            text = _editingOriginalText;
        }

        string? copyPrompt = null;
        if (!reject && !string.IsNullOrWhiteSpace(text))
            copyPrompt = CopyPromptBuilder?.Invoke(text);

        var display = string.IsNullOrWhiteSpace(text) ? " " : text;
        if (reject)
            display = string.IsNullOrWhiteSpace(_editingOriginalText) ? " " : _editingOriginalText;

        RestoreBubbleView(panel, display, copyPrompt, clearTagOnReject: reject);

        _editingPanel = null;
        _editingOriginalText = "";

        BubbleEditFinished?.Invoke(this, new CaptionBubbleEditEventArgs
        {
            Reject = reject,
            Text = text,
            CopyPrompt = copyPrompt,
        });
    }

    private void CancelActiveEdit(bool restoreOriginal)
    {
        if (_editingPanel is null)
            return;

        var display = restoreOriginal ? _editingOriginalText : _editingOriginalText;
        RestoreBubbleView(_editingPanel, string.IsNullOrWhiteSpace(display) ? " " : display, null, clearTagOnReject: false);
        _editingPanel = null;
        _editingOriginalText = "";
    }

    private void RestoreBubbleView(Border panel, string displayText, string? copyPrompt, bool clearTagOnReject)
    {
        var stack = new StackPanel();
        var label = CreateCaptionLabel();
        label.Text = displayText;
        stack.Children.Add(label);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        actions.Children.Add(CreateCaptionIconButton(
            TopBarIcons.CreateCopyGlyphIcon(CaptionIconSize),
            "Copy interviewer caption",
            (_, _) => CopyCaptionText(label)));
        stack.Children.Add(actions);

        panel.Child = stack;
        panel.Cursor = Cursors.Hand;

        if (clearTagOnReject)
            panel.Tag = null;
        else if (!string.IsNullOrEmpty(copyPrompt))
            panel.Tag = copyPrompt;
    }

    private static string NormalizeCaptionDisplay(string? text)
    {
        var t = (text ?? "").Trim();
        return t == " " ? "" : t;
    }

    private static void CopyCaptionText(TextBlock label)
    {
        if (Interlocked.CompareExchange(ref _copyToastInFlight, 1, 0) != 0)
            return;

        try
        {
            var caption = NormalizeCaptionDisplay(label.Text);
            if (string.IsNullOrWhiteSpace(caption))
                return;

            var toCopy = FormatInterviewerCopyText(caption);
            _ = CopyCaptionTextAsync(toCopy);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _copyToastInFlight, 0);
            ToastService.Show(ToastMessages.ForException(ex), ToastLevel.Error);
        }
    }

    private static async Task CopyCaptionTextAsync(string toCopy)
    {
        try
        {
            var ok = await ClipboardHelper.TrySetTextAsync(toCopy).ConfigureAwait(true);
            if (ok)
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
            Interlocked.Exchange(ref _copyToastInFlight, 0);
        }
    }

    private static bool IsWithinCaptionChrome(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or TextBox or ScrollBar or Thumb)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private double GetCaptionHostWidth()
    {
        var w = ActualWidth;
        return w > 0 ? w : 400;
    }

    private void ApplyBubbleWidth(Border panel)
    {
        var w = Math.Max(200, GetCaptionHostWidth() * BubbleWidthRatio);
        panel.Width = w;
        panel.MinWidth = w;
        panel.MaxWidth = w;
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;

        if (VisualTreeHelper.GetParent(panel) is Grid row)
        {
            row.Width = w;
            row.MinWidth = w;
            row.MaxWidth = w;
            row.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    private void ApplyAllBubbleWidths()
    {
        var w = Math.Max(200, GetCaptionHostWidth() * BubbleWidthRatio);
        BubbleStack.Width = w;
        BubbleStack.MinWidth = w;
        BubbleStack.HorizontalAlignment = HorizontalAlignment.Stretch;

        foreach (var child in BubbleStack.Children)
        {
            if (child is not Grid row)
                continue;
            foreach (UIElement c in row.Children)
            {
                if (c is Border b)
                    ApplyBubbleWidth(b);
            }
        }
    }

    private void ScrollToEnd(bool force = false)
    {
        if (!ShouldAutoScroll())
            return;
        if (!force && !IsNearBottom())
            return;
        _suppressUserScrollDepth++;
        try
        {
            Scroller.ScrollToEnd();
        }
        finally
        {
            _suppressUserScrollDepth--;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyAllBubbleWidths();
    }
}
