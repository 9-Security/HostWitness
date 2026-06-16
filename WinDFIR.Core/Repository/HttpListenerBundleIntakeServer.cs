using System.Net;
using System.Text;
using System.Text.Json;

namespace WinDFIR.Core.Repository;

/// <summary>
/// A minimal, dependency-free HTTP front for <see cref="BundleIntakeService"/> built on <see cref="HttpListener"/>.
/// It hosts the <see cref="HttpIntakeContract"/> endpoints so a <see cref="HttpArtifactSink"/> on an investigated
/// host can deliver its bundle to a central case repository. Intended for a small, operator-run intake host;
/// terminate TLS and add authentication at a reverse proxy (or extend this) before exposing beyond a trusted LAN.
/// </summary>
public sealed class HttpListenerBundleIntakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly BundleIntakeService _service;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public HttpListenerBundleIntakeServer(BundleIntakeService service, string urlPrefix)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        ArgumentException.ThrowIfNullOrWhiteSpace(urlPrefix);
        _listener.Prefixes.Add(urlPrefix.EndsWith('/') ? urlPrefix : urlPrefix + "/");
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
            {
                break; // listener stopped/disposed — normal shutdown
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var segments = context.Request.Url!.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3 || !segments[0].Equals("bundles", StringComparison.OrdinalIgnoreCase))
            {
                await WriteStatusAsync(context, HttpStatusCode.NotFound);
                return;
            }

            var collectionId = Uri.UnescapeDataString(segments[1]);
            var action = segments[2].ToLowerInvariant();
            var method = context.Request.HttpMethod;

            switch (action)
            {
                case "status" when method == HttpMethod.Get.Method:
                    var status = await _service.GetStatusAsync(collectionId, cancellationToken);
                    await WriteJsonAsync(context, HttpStatusCode.OK, status);
                    return;

                case "files" when method == HttpMethod.Put.Method:
                    var relative = context.Request.QueryString["path"];
                    var sha = context.Request.Headers[HttpIntakeContract.Sha256Header];
                    if (string.IsNullOrEmpty(relative) || string.IsNullOrEmpty(sha))
                    {
                        await WriteStatusAsync(context, HttpStatusCode.BadRequest);
                        return;
                    }

                    var stored = await _service.ReceiveFileAsync(collectionId, relative, sha, context.Request.InputStream, cancellationToken);
                    await WriteStatusAsync(context, stored ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
                    return;

                case "complete" when method == HttpMethod.Post.Method:
                    var result = await _service.CompleteAsync(collectionId, cancellationToken);
                    await WriteJsonAsync(context, HttpStatusCode.OK, result);
                    return;

                default:
                    await WriteStatusAsync(context, HttpStatusCode.NotFound);
                    return;
            }
        }
        catch
        {
            try { await WriteStatusAsync(context, HttpStatusCode.InternalServerError); } catch { /* connection gone */ }
        }
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, HttpStatusCode statusCode, T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, HttpIntakeContract.Json);
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task WriteStatusAsync(HttpListenerContext context, HttpStatusCode statusCode)
    {
        context.Response.StatusCode = (int)statusCode;
        await context.Response.OutputStream.FlushAsync();
        context.Response.Close();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { if (_listener.IsListening) _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
