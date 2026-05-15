namespace InterviewAssistant.App.Ui;

public sealed class CaptionBubbleEditEventArgs : EventArgs
{
    public required bool Reject { get; init; }
    public required string Text { get; init; }
    public string? CopyPrompt { get; init; }
}
