using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewAssistant.App;
using InterviewAssistant.App.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

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
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private readonly ConcurrentDictionary<Guid, StreamWriter> _sseClients = new();

    public CompanionApiServer(CompanionSessionService session, string host = "127.0.0.1", int port = 1212)
    {
        _session = session;
        _host = host;
        _port = port;
        _session.DraftChanged += _ =>
        {
            if (_session.TryBuildDraftPayload(forceFullCaption: false, out var payload) && payload is not null)
                BroadcastSse("draft", payload);
        };
        _session.HistoryAdded += ev => BroadcastSse("history", ev);
        _session.EndPressed += () => BroadcastSse("hotkey", new { key = "end" });
        _session.DeletePressed += () => BroadcastSse("hotkey", new { key = "delete" });
    }

    public string Prefix => $"http://{_host}:{_port}/";

    public void Start()
    {
        if (_app is not null)
            return;

        var url = Prefix.TrimEnd('/');
        StartupDiagnostics.Log($"Companion: Kestrel binding {url}");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);

        var app = builder.Build();
        app.Use((HttpContext ctx, RequestDelegate _) => HandleRequestAsync(ctx));

        _app = app;
        _cts = new CancellationTokenSource();
        _serverTask = app.StartAsync(_cts.Token);
        StartupDiagnostics.Log($"Companion: Kestrel listening on {url}");
        Debug.WriteLine($"[Companion] API {url}/");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        CloseAllSseClients();

        var app = _app;
        var serverTask = _serverTask;
        _app = null;
        _serverTask = null;

        if (serverTask is not null)
        {
            try
            {
                serverTask.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"Companion: server task wait: {ex.Message}");
            }
        }

        if (app is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                app.StopAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                StartupDiagnostics.Log($"Companion: Kestrel stop: {ex.Message}");
            }

            try
            {
                app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    private void CloseAllSseClients()
    {
        foreach (var key in _sseClients.Keys.ToArray())
        {
            if (!_sseClients.TryRemove(key, out var writer))
                continue;
            try
            {
                writer.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task HandleRequestAsync(HttpContext ctx)
    {
        try
        {
            AddCors(ctx);
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            var path = ctx.Request.Path.Value ?? "/";
            path = path.TrimEnd('/');
            if (path.Length == 0)
                path = "/";

            if (HttpMethods.IsGet(ctx.Request.Method))
            {
                if (path is "/health" or "/ping")
                {
                    await WriteJsonAsync(ctx, new { ok = true, service = "interview-assistant-companion" });
                    return;
                }

                if (path == "/draft")
                {
                    var forceFull = string.Equals(ctx.Request.Query["full"], "1", StringComparison.Ordinal);
                    if (_session.TryBuildDraftPayloadForPoll(forceFull, out var payload) && payload is not null)
                    {
                        await WriteJsonAsync(ctx, payload);
                    }
                    else
                    {
                        await WriteJsonAsync(ctx, new
                        {
                            changed = false,
                            running = _session.IsRunning,
                            session_generation = _session.SessionGeneration,
                        });
                    }

                    return;
                }

                if (path == "/history")
                {
                    await WriteJsonAsync(ctx, new { events = _session.GetHistorySnapshot() });
                    return;
                }

                if (path == "/events")
                {
                    await HandleSseAsync(ctx);
                    return;
                }

                if (path == "/modes")
                {
                    await WriteJsonAsync(ctx, new
                    {
                        active = _session.ModePrompts.SessionMode,
                        modes = new[] { "read", "type", "behavioral" },
                    });
                    return;
                }

                if (path == "/languages")
                {
                    await WriteJsonAsync(ctx, new
                    {
                        active = _session.LanguagePrompts.SessionLanguage,
                        languages = new[] { "english", "chinese" },
                    });
                    return;
                }

                if (path == "/endpoint-words")
                {
                    var countRaw = ctx.Request.Query["count"].ToString();
                    var count = int.TryParse(countRaw, out var c) ? c : 20;
                    await WriteJsonAsync(ctx, new { words = _session.GetEndpointWords(count) });
                    return;
                }

                if (path == "/context")
                {
                    await WriteJsonAsync(ctx, BuildContextPayload());
                    return;
                }

                if (path.StartsWith("/prompts/", StringComparison.Ordinal))
                {
                    var key = path["/prompts/".Length..];
                    var text = GetPromptByKey(key);
                    await WriteJsonAsync(ctx, new { key, text });
                    return;
                }

                // ── Interview history ──────────────────────────────────────
                if (path == "/interview-history")
                {
                    var list = InterviewHistoryStore.List()
                        .Select(m => new { name = m.Name, created_utc = m.CreatedUtc, pair_count = m.PairCount });
                    await WriteJsonAsync(ctx, new { files = list });
                    return;
                }

                if (path.StartsWith("/interview-history/", StringComparison.Ordinal))
                {
                    var fname = Uri.UnescapeDataString(path["/interview-history/".Length..].Trim());
                    var session = InterviewHistoryStore.Load(fname);
                    if (session is null)
                    {
                        await WriteJsonAsync(ctx, new { ok = false, error = "not_found" }, 404);
                        return;
                    }
                    await WriteJsonAsync(ctx, session);
                    return;
                }
            }

            if (HttpMethods.IsDelete(ctx.Request.Method))
            {
                if (path.StartsWith("/context/resume/", StringComparison.Ordinal))
                {
                    var name = Uri.UnescapeDataString(path["/context/resume/".Length..].Trim());
                    var ok = ResumeJdHistoryStore.DeleteResume(name);
                    await WriteJsonAsync(ctx, ok ? BuildContextPayload() : new { ok = false, error = "not_found" }, ok ? 200 : 404);
                    return;
                }

                if (path.StartsWith("/context/jd/", StringComparison.Ordinal))
                {
                    var name = Uri.UnescapeDataString(path["/context/jd/".Length..].Trim());
                    var ok = ResumeJdHistoryStore.DeleteJd(name);
                    await WriteJsonAsync(ctx, ok ? BuildContextPayload() : new { ok = false, error = "not_found" }, ok ? 200 : 404);
                    return;
                }

                if (path.StartsWith("/interview-history/", StringComparison.Ordinal))
                {
                    var fname = Uri.UnescapeDataString(path["/interview-history/".Length..].Trim());
                    var ok = InterviewHistoryStore.Delete(fname);
                    await WriteJsonAsync(ctx, new { ok }, ok ? 200 : 404);
                    return;
                }
            }

            if (HttpMethods.IsPatch(ctx.Request.Method) || (HttpMethods.IsPost(ctx.Request.Method) && path.EndsWith("/rename", StringComparison.Ordinal)))
            {
                if (path.StartsWith("/interview-history/", StringComparison.Ordinal))
                {
                    var fname = Uri.UnescapeDataString(path["/interview-history/".Length..].TrimEnd('/').Replace("/rename", "").Trim());
                    var body  = await ReadJsonBodyAsync(ctx);
                    var newName = body.TryGetProperty("new_name", out var nn) ? nn.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        await WriteJsonAsync(ctx, new { ok = false, error = "new_name required" }, 400);
                        return;
                    }
                    var ok = InterviewHistoryStore.Rename(fname, newName);
                    await WriteJsonAsync(ctx, new { ok }, ok ? 200 : 409);
                    return;
                }
            }

            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                if (path == "/session/start")
                {
                    if (!_session.IsRunning)
                        _session.Start();
                    await WriteJsonAsync(ctx, new { ok = true, running = true });
                    return;
                }

                if (path == "/session/stop")
                {
                    _session.Stop();
                    await WriteJsonAsync(ctx, new { ok = true, running = false });
                    return;
                }

                if (path == "/interview-history")
                {
                    try
                    {
                        var body = await ReadJsonBodyAsync(ctx);
                        var session = new InterviewHistoryStore.Session
                        {
                            CreatedUtc = body.TryGetProperty("created_utc", out var c) && c.TryGetDateTime(out var dt)
                                ? dt : DateTime.UtcNow,
                        };

                        if (body.TryGetProperty("pairs", out var pairsEl))
                        {
                            foreach (var p in pairsEl.EnumerateArray())
                            {
                                session.Pairs.Add(new InterviewHistoryStore.QaPair
                                {
                                    Caption = p.TryGetProperty("caption", out var cap) ? cap.GetString() ?? "" : "",
                                    Result  = p.TryGetProperty("result",  out var res) ? res.GetString() ?? "" : "",
                                    TsUtc   = p.TryGetProperty("ts_utc", out var ts) && ts.TryGetDateTime(out var tdt)
                                                ? tdt : DateTime.UtcNow,
                                });
                            }
                        }

                        var name = InterviewHistoryStore.Save(session);
                        await WriteJsonAsync(ctx, new { ok = true, name });
                    }
                    catch (Exception ex)
                    {
                        await WriteJsonAsync(ctx, new { ok = false, error = ex.Message }, 400);
                    }
                    return;
                }

                if (path == "/end")
                {
                    string? overrideChunk = null;
                    if (ctx.Request.ContentLength is > 0)
                    {
                        var body = await ReadJsonBodyAsync(ctx);
                        if (body.TryGetProperty("chunk", out var c))
                            overrideChunk = c.GetString();
                    }

                    var result = _session.TryEnd(overrideChunk);
                    await WriteJsonAsync(ctx, new
                    {
                        ok = result.Ok,
                        chunk = result.Chunk,
                        prompt = result.Prompt,
                        draft = result.Draft,
                        full = _session.GetFullCaption(),
                        pending_start = _session.GetPendingStartIndex(),
                        session_generation = _session.SessionGeneration,
                        message = result.Message,
                    });
                    return;
                }

                if (path == "/captions/restart")
                {
                    _session.RestartCaptions();
                    _session.TryBuildDraftPayload(forceFullCaption: true, out var payload);
                    await WriteJsonAsync(ctx, payload ?? _session.GetDraftPayload(forceFullCaption: true));
                    return;
                }

                if (path == "/delete")
                {
                    var skip = _session.TryDelete();
                    await WriteJsonAsync(ctx, new
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
                    var body = await ReadJsonBodyAsync(ctx);
                    var idx = body.TryGetProperty("start_index", out var p) ? p.GetInt32() : -1;
                    var ok = idx >= 0 && _session.SetEndpoint(idx);
                    await WriteJsonAsync(ctx, new { ok, draft = _session.GetDraft() });
                    return;
                }

                if (path == "/mode")
                {
                    var body = await ReadJsonBodyAsync(ctx);
                    if (body.TryGetProperty("mode", out var m))
                        _session.ModePrompts.SessionMode = m.GetString() ?? "read";
                    await WriteJsonAsync(ctx, new { ok = true, active = _session.ModePrompts.SessionMode });
                    return;
                }

                if (path == "/language")
                {
                    var body = await ReadJsonBodyAsync(ctx);
                    if (body.TryGetProperty("language", out var lang))
                        _session.LanguagePrompts.SessionLanguage = lang.GetString() ?? "english";
                    await WriteJsonAsync(ctx, new { ok = true, active = _session.LanguagePrompts.SessionLanguage });
                    return;
                }

                if (path == "/context/extract-text")
                {
                    var body = await ReadJsonBodyAsync(ctx);
                    var fileName = body.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? "file.txt" : "file.txt";
                    var b64 = body.TryGetProperty("content_base64", out var b) ? b.GetString() ?? "" : "";
                    try
                    {
                        var bytes = Convert.FromBase64String(b64);
                        var text = DocumentTextExtractor.Extract(fileName, bytes);
                        await WriteJsonAsync(ctx, new { ok = true, text });
                    }
                    catch (Exception ex)
                    {
                        await WriteJsonAsync(ctx, new { ok = false, error = ex.Message });
                    }
                    return;
                }

                if (path == "/context/resume")
                {
                    try
                    {
                        var body = await ReadJsonBodyAsync(ctx);
                        var name = body.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var text = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        ResumeJdHistoryStore.SaveResume(name, text);
                        await WriteJsonAsync(ctx, new { ok = true, context = BuildContextPayload() });
                    }
                    catch (ArgumentException ex)
                    {
                        await WriteJsonAsync(ctx, new { ok = false, error = ex.Message }, StatusCodes.Status400BadRequest);
                    }

                    return;
                }

                if (path == "/context/jd")
                {
                    try
                    {
                        var body = await ReadJsonBodyAsync(ctx);
                        var name = body.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var text = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        ResumeJdHistoryStore.SaveJd(name, text);
                        await WriteJsonAsync(ctx, new { ok = true, context = BuildContextPayload() });
                    }
                    catch (ArgumentException ex)
                    {
                        await WriteJsonAsync(ctx, new { ok = false, error = ex.Message }, StatusCodes.Status400BadRequest);
                    }

                    return;
                }

                if (path.StartsWith("/prompts/", StringComparison.Ordinal))
                {
                    var key = path["/prompts/".Length..];
                    var body = await ReadJsonBodyAsync(ctx);
                    var text = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    SavePromptByKey(key, text);
                    await WriteJsonAsync(ctx, new { ok = true });
                    return;
                }

                if (path == "/gpt-answer")
                {
                    var body = await ReadJsonBodyAsync(ctx);
                    var answer = body.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(answer))
                    {
                        _session.History.AppendGpt(answer);
                        var ev = new HistoryEventDto("gpt", answer.Trim(), "extension");
                        BroadcastSse("history", ev);
                    }

                    await WriteJsonAsync(ctx, new { ok = true });
                    return;
                }
            }

            await WriteJsonAsync(ctx, new { error = "not_found" }, StatusCodes.Status404NotFound);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Companion] API error: {ex.Message}");
            try
            {
                await WriteJsonAsync(ctx, new { error = ex.Message }, StatusCodes.Status500InternalServerError);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task HandleSseAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        AddCors(ctx);

        var id = Guid.NewGuid();
        var writer = new StreamWriter(ctx.Response.Body, Encoding.UTF8) { AutoFlush = true };
        _sseClients[id] = writer;

        await writer.WriteLineAsync(": connected");
        if (_session.TryBuildDraftPayload(forceFullCaption: true, out var connectPayload) && connectPayload is not null)
        {
            await writer.WriteLineAsync(
                $"data: {JsonSerializer.Serialize(new { type = "draft", payload = connectPayload }, JsonOptions)}");
            await writer.WriteLineAsync();
        }

        try
        {
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                ctx.RequestAborted,
                _cts?.Token ?? CancellationToken.None);

            while (_app is not null && !shutdown.Token.IsCancellationRequested)
            {
                await Task.Delay(1000, shutdown.Token);
                if (_app is null)
                    break;
                await writer.WriteLineAsync(": keepalive");
                await writer.WriteLineAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
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
                await writer.DisposeAsync();
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
                kv.Value.Flush();
            }
            catch
            {
                _sseClients.TryRemove(kv.Key, out _);
            }
        }
    }

    private static void AddCors(HttpContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PATCH, DELETE, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Access-Control-Request-Private-Network";
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    private static async Task<JsonElement> ReadJsonBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json))
            return default;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task WriteJsonAsync(HttpContext ctx, object payload, int status = StatusCodes.Status200OK)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, payload.GetType(), JsonOptions);
    }

    private object BuildContextPayload()
    {
        var snap = ResumeJdHistoryStore.LoadSnapshot();
        var (resume, jd) = ResumeJdHistoryStore.GetActiveTexts();
        return new
        {
            resume,
            job_description = jd,
            active_resume = snap.ActiveResumeName,
            active_jd = snap.ActiveJdName,
            resumes = snap.Resumes.Select(e => new { name = e.Name, text = e.Text, updated_utc = e.UpdatedUtc }),
            jds = snap.Jds.Select(e => new { name = e.Name, text = e.Text, updated_utc = e.UpdatedUtc }),
            templates = BuildTemplatesMap(),
        };
    }

    private Dictionary<string, string> BuildTemplatesMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _session.ModePrompts.All)
            map[kv.Key] = kv.Value;
        foreach (var kv in _session.LanguagePrompts.All)
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
        "english" or "chinese" => GetLanguageTemplate(key),
        _ => "",
    };

    private string GetLanguageTemplate(string key)
    {
        var prev = _session.LanguagePrompts.SessionLanguage;
        _session.LanguagePrompts.SessionLanguage = key;
        var t = _session.LanguagePrompts.GetActiveTemplate() ?? "";
        _session.LanguagePrompts.SessionLanguage = prev;
        return t;
    }

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
            case "english":
            case "chinese":
                _session.LanguagePrompts.SetTemplate(key, text);
                _session.LanguagePrompts.SaveToDisk();
                break;
        }
    }

    public void Dispose() => Stop();
}
