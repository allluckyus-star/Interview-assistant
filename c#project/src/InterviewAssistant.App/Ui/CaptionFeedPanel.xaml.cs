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

    private const double BubbleWidthRatio = 0.85;
    private const double CaptionIconSize = 14;
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



    private Border? _editingPanel;

    private string _editingOriginalText = "";



    /// <summary>Builds full GPT prompt (mode template + caption) for bubble Tag after send.</summary>

    public Func<string, string?>? CopyPromptBuilder { get; set; }



    public event EventHandler<CaptionBubbleEditEventArgs>? BubbleEditFinished;



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
        if (_editingPanel is not null)
            return false;
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

    }



    public void UpdateDraft(string text, string? copyPrompt = null)

    {

        if (_draftRow is null)

        {

            (_draftRow, _draftPanel, _draftLabel) = CreateBubbleRow(alignRight: true, withCopy: true, editable: true);

            BubbleStack.Children.Add(_draftRow);

        }



        _draftLabel!.Text = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();

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

            ScrollToEnd();

            return;

        }



        AddInterviewerBubble(text, copyPrompt);

    }



    public void AddInterviewerBubble(string text, string? copyPrompt)

    {

        var (row, panel, label) = CreateBubbleRow(alignRight: true, withCopy: true, editable: true);

        label.Text = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();

        if (!string.IsNullOrEmpty(copyPrompt))

            panel.Tag = copyPrompt;

        BubbleStack.Children.Add(row);

        ApplyBubbleWidth(panel);

        ScrollToEnd();

    }



    public void AddGptBubble(string text)

    {

        var (row, panel, label) = CreateBubbleRow(alignRight: false, withCopy: false, editable: false);

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



    private (Grid Row, Border Panel, TextBlock Label) CreateBubbleRow(bool alignRight, bool withCopy, bool editable)

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
                    BeginInlineEdit(panel);
                }
            };

        }



        var stack = new StackPanel();

        var label = CreateCaptionLabel();

        stack.Children.Add(label);



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



        var col = alignRight ? 1 : 0;

        Grid.SetColumn(panel, col);

        panel.HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        row.Children.Add(panel);



        return (row, panel, label);

    }



    private static TextBlock CreateCaptionLabel()

    {

        var label = new TextBlock();

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



        var editor = new TextBox

        {

            Text = _editingOriginalText,

            AcceptsReturn = true,

            TextWrapping = TextWrapping.Wrap,

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

            HorizontalAlignment = HorizontalAlignment.Right,

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



    private void ApplyBubbleWidth(Border panel)

    {

        var hostWidth = ActualWidth > 0 ? ActualWidth : 400;

        panel.MaxWidth = Math.Max(200, hostWidth * BubbleWidthRatio);

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

}


