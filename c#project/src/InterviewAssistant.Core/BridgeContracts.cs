namespace InterviewAssistant.Core;

public sealed class NextPromptPayload
{
    public string RequestId { get; set; } = "";
    public long CreatedAt { get; set; }
    public string Prompt { get; set; } = "";
}

public sealed class LatestAnswerPayload
{
    public string RequestId { get; set; } = "";
    public long CreatedAt { get; set; }
    public string Answer { get; set; } = "";
}

public sealed class ContextPayload
{
    public string Resume { get; set; } = "";
    public string JobDescription { get; set; } = "";
    public Dictionary<string, string> Templates { get; set; } = new();
}
