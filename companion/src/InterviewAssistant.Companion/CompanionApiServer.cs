using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewAssistant.App;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

public sealed class CompanionApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CompanionSessionService _session;
    private readonly string _host;
    private readonly int _port;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly ConcurrentDictionary<Guid, StreamWriter> _sseClients = new();

    public CompanionApiServer(CompanionSessionService session, string host = "127.0.0.1", int port = 1212)
    {
        _session = session;
        _host = host;
        _port = port;
        _session.DraftChanged += _ => BroadcastSse("draft", _session.GetDraftPayload());
        _session.HistoryAdded += ev => BroadcastSse("history", ev);
        _session.EndPressed += () => BroadcastSse("hotkey", new { key = "end" });
        _session.DeletePressed += () => BroadcastSse("hotkey", new { key = "delete" });
    }

    public string Prefix => $"http://{_host}:{_port}/";

    public void Start()
    {
        if (_listener is not null)
            return;

        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
        Debug.WriteLine($"[Companion] API {Prefix}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // ignore
        }

        foreach (var kv in _sseClients)
        {
            try
            {
                kv.Value.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _sseClients.Clear();
        _listener?.Close();
        _listener = null;
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false);
            }
            catch when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                break;
            }

            if (ctx is not null)
                _ = Task.Run(() => HandleRequest(ctx), token);
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            AddCors(ctx);
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            path = path.TrimEnd('/');
            if (path.Length == 0)
                path = "/";

            if (ctx.Request.HttpMethod == "GET")
            {
                if (path is "/health" or "/ping")
                {
                    WriteJson(ctx, new { ok = true, service = "interview-assistant-companion" });
                    return;
                }

                if (path == "/draft")
                {
                    WriteJson(ctx, _session.GetDraftPayload());
                    return;
                }

                if (path == "/history")
                {
                    WriteJson(ctx, new { events = _session.GetHistorySnapshot() });
                    return;
                }

                if (path == "/events")
                {
                    HandleSse(ctx);
                    return;
                }

                if (path == "/modes")
                {
                    WriteJson(ctx, new
                    {
                        active = _session.ModePrompts.SessionMode,
                        modes = new[] { "read", "type", "behavioral" },
                    });
                    return;
                }

                if (path == "/endpoint-words")
                {
                    var count = int.TryParse(ctx.Request.QueryString["count"], out var c) ? c : 20;
                    WriteJson(ctx, new { words = _session.GetEndpointWords(count) });
                    return;
                }

                if (path == "/context")
                {
                    var (resume, jd) = ResumeJdStore.Load();
                    WriteJson(ctx, new
                    {
                        resume,
                        job_description = jd,
                        templates = BuildTemplatesMap(),
                    });
                    return;
                }

                if (path.StartsWith("/prompts/", StringComparison.Ordinal))
                {
                    var key = path["/prompts/".Length..];
                    var text = GetPromptByKey(key);
                    WriteJson(ctx, new { key, text });
                    return;
                }
            }

            if (ctx.Request.HttpMethod == "POST")
            {
                if (path == "/session/start")
                {
                    if (!_session.IsRunning)
                        _session.Start();
                    WriteJson(ctx, new { ok = true, running = true });
                    return;
                }

                if (path == "/session/stop")
                {
                    _session.Stop();
                    WriteJson(ctx, new { ok = true, running = false });
                    return;
                }

                if (path == "/end")
                {
                    string? overrideChunk = null;
                    if (ctx.Request.ContentLength64 > 0)
                    {
                        var body = ReadJsonBody(ctx);
                        if (body.TryGetProperty("chunk", out var c))
                            overrideChunk = c.GetString();
                    }

                    var result = _session.TryEnd(overrideChunk);
                    WriteJson(ctx, new
                    {
                        ok = result.Ok,
                        chunk = result.Chunk,
                        prompt = result.Prompt,
                        draft = result.Draft,
                        full = _session.GetFullCaption(),
                        pending_start = _session.GetPendingStartIndex(),
                        message = result.Message,
                    });
                    return;
                }

                if (path == "/delete")
                {
                    var skip = _session.TryDelete();
                    WriteJson(ctx, new
                    {
                        ok = true,
                        skipped = skip.Skipped,
                        message = skip.Message,
                        draft = _session.GetDraft(),
                        full = _session.GetFullCaption(),
                        pending_start = _session.GetPendingStartIndex(),
                    });
                    return;
                }

                if (path == "/endpoint")
                {
                    var body = ReadJsonBody(ctx);
                    var idx = body.TryGetProperty("start_index", out var p) ? p.GetInt32() : -1;
                    var ok = idx >= 0 && _session.SetEndpoint(idx);
                    WriteJson(ctx, new { ok, draft = _session.GetDraft() });
                    return;
                }

                if (path == "/mode")
                {
                    var body = ReadJsonBody(ctx);
                    if (body.TryGetProperty("mode", out var m))
                        _session.ModePrompts.SessionMode = m.GetString() ?? "read";
                    WriteJson(ctx, new { ok = true, active = _session.ModePrompts.SessionMode });
                    return;
                }

                if (path == "/context/extract-text")
                {
                    var body = ReadJsonBody(ctx);
                    var fileName = body.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? "file.txt" : "file.txt";
                    var b64 = body.TryGetProperty("content_base64", out var b) ? b.GetString() ?? "" : "";
                    try
                    {
                        var bytes = Convert.FromBase64String(b64);
                        var text = DocumentTextExtractor.Extract(fileName, bytes);
                        WriteJson(ctx, new { ok = true, text });
                    }
                    catch (Exception ex)
                    {
                        WriteJson(ctx, new { ok = false, error = ex.Message });
                    }
                    return;
                }

                if (path == "/context/resume")
                {
                    var body = ReadJsonBody(ctx);
                    var text = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    var (_, jd) = ResumeJdStore.Load();
                    ResumeJdStore.Save(text, jd);
                    WriteJson(ctx, new { ok = true });
                    return;
                }

                if (path == "/context/jd")
                {
                    var body = ReadJsonBody(ctx);
                    var text = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    var (resume, _) = ResumeJdStore.Load();
                    ResumeJdStore.Save(resume, text);
                    WriteJson(ctx, new { ok = true });
                    return;
                }

                if (path.StartsWith("/prompts/", StringComparison.Ordinal))
                {
                    var key = path["/prompts/".Length..];
                    var body = ReadJsonBody(ctx);
                    var text = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    SavePromptByKey(key, text);
                    WriteJson(ctx, new { ok = true });
                    return;
                }

                if (path == "/gpt-answer")
                {
                    var body = ReadJsonBody(ctx);
                    var answer = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(answer))
                    {
                        _session.History.AppendGpt(answer);
                        var ev = new HistoryEventDto("gpt", answer.Trim(), "extension");
                        BroadcastSse("history", ev);
                    }

                    WriteJson(ctx, new { ok = true });
                    return;
                }
            }

            WriteJson(ctx, new { error = "not_found" }, 404);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Companion] API error: {ex.Message}");
            try
            {
                WriteJson(ctx, new { error = ex.Message }, 500);
            }
            catch
            {
                // ignore
            }
        }
    }

    private void HandleSse(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.Add("Cache-Control", "no-cache");
        AddCors(ctx);
        ctx.Response.SendChunked = true;

        var id = Guid.NewGuid();
        var writer = new StreamWriter(ctx.Response.OutputStream, Encoding.UTF8) { AutoFlush = true };
        _sseClients[id] = writer;

        writer.WriteLine(": connected");
        writer.WriteLine($"data: {JsonSerializer.Serialize(new { type = "draft", payload = _session.GetDraftPayload() }, JsonOptions)}");
        writer.WriteLine();

        try
        {
            while (_listener is { IsListening: true } && ctx.Response.OutputStream.CanWrite)
            {
                Thread.Sleep(15000);
                writer.WriteLine(": keepalive");
                writer.WriteLine();
            }
        }
        catch
        {
            // client disconnected
        }
        finally
        {
            _sseClients.TryRemove(id, out _);
            try
            {
                writer.Dispose();
                ctx.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private void BroadcastSse(string type, object payload)
    {
        var line = $"data: {JsonSerializer.Serialize(new { type, payload }, JsonOptions)}";
        foreach (var kv in _sseClients.ToArray())
        {
            try
            {
                kv.Value.WriteLine(line);
                kv.Value.WriteLine();
            }
            catch
            {
                _sseClients.TryRemove(kv.Key, out _);
            }
        }
    }

    private static void AddCors(HttpListenerContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }

    private static JsonElement ReadJsonBody(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var json = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(json))
            return default;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void WriteJson(HttpListenerContext ctx, object payload, int status = 200)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), JsonOptions);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private Dictionary<string, string> BuildTemplatesMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _session.ModePrompts.All)
            map[kv.Key] = kv.Value;
        map["resume_summary"] = PromptTemplateResolver.TryReadResumeTemplate();
        map["jd_summary"] = PromptTemplateResolver.TryReadJdTemplate();
        map["initial_interview"] = PromptTemplateResolver.TryReadInitialInterviewTemplate();
        return map;
    }

    private string GetPromptByKey(string key) => key.ToLowerInvariant() switch
    {
        "resume_summary" => PromptTemplateResolver.TryReadResumeTemplate(),
        "jd_summary" => PromptTemplateResolver.TryReadJdTemplate(),
        "initial_interview" => PromptTemplateResolver.TryReadInitialInterviewTemplate(),
        "read" or "type" or "error" or "behavioral" or "closing" => GetModeTemplate(key),
        _ => "",
    };

    private string GetModeTemplate(string key)
    {
        var prev = _session.ModePrompts.SessionMode;
        _session.ModePrompts.SessionMode = key;
        var t = _session.ModePrompts.GetActiveTemplate() ?? "";
        _session.ModePrompts.SessionMode = prev;
        return t;
    }

    private void SavePromptByKey(string key, string text)
    {
        switch (key.ToLowerInvariant())
        {
            case "resume_summary":
                PromptTemplateResolver.SavePrepTemplate("resume_summary", text);
                break;
            case "jd_summary":
                PromptTemplateResolver.SavePrepTemplate("jd_summary", text);
                break;
            case "initial_interview":
                PromptTemplateResolver.SavePrepTemplate("initial_interview", text);
                break;
            case "read":
            case "type":
            case "error":
            case "behavioral":
            case "closing":
                _session.ModePrompts.SetTemplate(key, text);
                _session.ModePrompts.SaveToDisk();
                break;
        }
    }

    public void Dispose() => Stop();
}
