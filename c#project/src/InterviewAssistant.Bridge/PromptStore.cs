using InterviewAssistant.Core;

namespace InterviewAssistant.Bridge;

/// <summary>In-memory state analogous to <c>bridge_server.PromptStore</c> (subset).</summary>
public sealed class PromptStore
{
    private readonly object _lock = new();
    private string _resumeText = "";
    private string _jobDescriptionText = "";
    private readonly Dictionary<string, string> _templates = new()
    {
        ["resume_summary"] = "",
        ["jd_summary"] = "",
        ["initial_interview"] = "",
    };

    private NextPromptPayload _latest = new() { RequestId = "", CreatedAt = 0, Prompt = "" };
    private LatestAnswerPayload _latestAnswer = new() { RequestId = "", CreatedAt = 0, Answer = "" };

    public NextPromptPayload GetPrompt()
    {
        lock (_lock)
        {
            return new NextPromptPayload
            {
                RequestId = _latest.RequestId,
                CreatedAt = _latest.CreatedAt,
                Prompt = _latest.Prompt,
            };
        }
    }

    public LatestAnswerPayload GetAnswer()
    {
        lock (_lock)
        {
            return new LatestAnswerPayload
            {
                RequestId = _latestAnswer.RequestId,
                CreatedAt = _latestAnswer.CreatedAt,
                Answer = _latestAnswer.Answer,
            };
        }
    }

    public ContextPayload GetContext()
    {
        lock (_lock)
        {
            return new ContextPayload
            {
                Resume = _resumeText,
                JobDescription = _jobDescriptionText,
                Templates = new Dictionary<string, string>(_templates),
            };
        }
    }

    public void SetResumeText(string text)
    {
        lock (_lock)
            _resumeText = text ?? "";
    }

    public void SetJobDescriptionText(string text)
    {
        lock (_lock)
            _jobDescriptionText = text ?? "";
    }

    public NextPromptPayload SetPrompt(string prompt)
    {
        lock (_lock)
        {
            _latest = new NextPromptPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Prompt = prompt ?? "",
            };
            return new NextPromptPayload
            {
                RequestId = _latest.RequestId,
                CreatedAt = _latest.CreatedAt,
                Prompt = _latest.Prompt,
            };
        }
    }

    public LatestAnswerPayload SetAnswer(string requestId, string answer)
    {
        lock (_lock)
        {
            _latestAnswer = new LatestAnswerPayload
            {
                RequestId = requestId ?? "",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Answer = answer ?? "",
            };
            return new LatestAnswerPayload
            {
                RequestId = _latestAnswer.RequestId,
                CreatedAt = _latestAnswer.CreatedAt,
                Answer = _latestAnswer.Answer,
            };
        }
    }
}
