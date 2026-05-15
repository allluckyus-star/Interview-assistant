using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewAssistant.Core;

namespace InterviewAssistant.Bridge;

public sealed class BridgeHttpServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly PromptStore _store;
    private readonly string _host;
    private readonly int _port;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BridgeHttpServer(PromptStore store, string host, int port)
    {
        _store = store;
        _host = host;
        _port = port;
    }

    public string Prefix => $"http://{_host}:{_port}/";

    public void Start()
    {
        if (_listener is not null)
            return;

        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Close();
            throw;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => AcceptLoop(token), token);
        Debug.WriteLine($"[bridge] listening on {Prefix}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
        catch (HttpListenerException)
        {
            // ignore
        }

        try
        {
            _loop?.GetAwaiter().GetResult();
        }
        catch
        {
            // ignore cancellation / stop races
        }

        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        Debug.WriteLine("[bridge] stopped");
    }

    private async Task AcceptLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ctx is null)
                continue;

            _ = Task.Run(() => HandleRequest(ctx), cancellationToken);
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "/";
            if (!path.StartsWith('/'))
                path = "/" + path;
            path = path.TrimEnd('/');

            Debug.WriteLine($"[bridge] {req.HttpMethod} {req.Url?.PathAndQuery}");

            if (req.HttpMethod == "GET")
            {
                if (path is "/ping" or "/health")
                {
                    WriteJson(ctx, new Dictionary<string, object> { ["ok"] = true, ["service"] = "interview-assistant-bridge" });
                    return;
                }

                if (path == "/next-prompt")
                {
                    WriteJson(ctx, _store.GetPrompt());
                    return;
                }

                if (path == "/latest-answer")
                {
                    WriteJson(ctx, _store.GetAnswer());
                    return;
                }

                if (path == "/context")
                {
                    WriteJson(ctx, _store.GetContext());
                    return;
                }
            }

            if (req.HttpMethod == "POST" && path == "/ack")
            {
                WriteJson(ctx, new Dictionary<string, string> { ["status"] = "ok" });
                return;
            }

            WriteJson(ctx, new Dictionary<string, string> { ["error"] = "not found" }, 404);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[bridge] handler error: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void WriteJson(HttpListenerContext ctx, object payload, int status = 200)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), JsonOptions);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    public void Dispose() => Stop();
}
