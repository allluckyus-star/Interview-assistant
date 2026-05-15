using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.Ui;

namespace InterviewAssistant.App;

public partial class SettingsPanel : UserControl
{
    private static readonly SolidColorBrush NavSelectedBg = new(Color.FromRgb(238, 242, 255));
    private static readonly SolidColorBrush NavSelectedBorder = new(Color.FromRgb(199, 210, 254));
    private static readonly SolidColorBrush NavIdleBg = new(Colors.Transparent);
    private static readonly SolidColorBrush NavIdleBorder = new(Colors.Transparent);

    private static readonly HashSet<string> PrepKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "resume_summary",
        "jd_summary",
        "initial_interview",
    };

    private ModePromptStore? _modes;
    private string _editingKey = "resume_summary";
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> PromptTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["resume_summary"] = "Resume summary",
        ["jd_summary"] = "JD summary",
        ["initial_interview"] = "Initial interview",
        ["read"] = "Read",
        ["type"] = "Type",
        ["behavioral"] = "Behavioral",
    };

    public event EventHandler? BackRequested;

    public SettingsPanel()
    {
        InitializeComponent();
        _navButtons["resume_summary"] = NavResumeSummary;
        _navButtons["jd_summary"] = NavJdSummary;
        _navButtons["initial_interview"] = NavInitialInterview;
        _navButtons["read"] = NavRead;
        _navButtons["type"] = NavType;
        _navButtons["behavioral"] = NavBehavioral;
    }

    public void Bind(ModePromptStore modes)
    {
        _modes = modes;
        SelectPrompt("resume_summary");
    }

    public void RefreshCurrentMode() => SelectPrompt(_editingKey);

    private void SelectPrompt(string key)
    {
        _editingKey = key;
        if (PrepKeys.Contains(key))
            SettingsEditor.Text = PromptTemplateResolver.ReadPrepTemplate(key);
        else if (_modes?.All.TryGetValue(key, out var modeText) == true)
            SettingsEditor.Text = modeText;
        else
            SettingsEditor.Text = "";

        foreach (var (navKey, btn) in _navButtons)
        {
            var selected = string.Equals(navKey, key, StringComparison.OrdinalIgnoreCase);
            btn.Background = selected ? NavSelectedBg : NavIdleBg;
            btn.BorderBrush = selected ? NavSelectedBorder : NavIdleBorder;
        }

        EditorModeTitle.Text = PromptTitles.TryGetValue(key, out var title) ? title : key;
    }

    private void ModeNav_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string key)
            SelectPrompt(key);
    }

    private void SettingsSavePromptButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PrepKeys.Contains(_editingKey))
                PromptTemplateResolver.SavePrepTemplate(_editingKey, SettingsEditor.Text);
            else
            {
                _modes?.SetTemplate(_editingKey, SettingsEditor.Text);
                _modes?.SaveToDisk();
            }

            ToastService.Show("Saved.", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            ToastService.Show(ToastMessages.ForFileSaveException(ex), ToastLevel.Error);
        }
    }

    private void SettingsBackButton_OnClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
