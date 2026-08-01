using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RainBird.Protocol;

namespace RainBird.Simulator;

/// <summary>
/// Serves a <see cref="VirtualController"/> over the JSON-RPC layer, including the
/// real encryption. Shared by the in-process transport and the HTTP listener so both
/// paths exercise identical code.
/// </summary>
public sealed class SimulatorRpcHandler
{
    private readonly VirtualController _controller;
    private readonly string _password;

    public SimulatorRpcHandler(VirtualController controller, string password)
    {
        _controller = controller;
        _password = password;
    }

    public VirtualController Controller => _controller;

    /// <summary>Handles a decrypted JSON-RPC envelope and returns the response envelope.</summary>
    public JsonObject HandleEnvelope(JsonObject envelope)
    {
        var id = envelope["id"]?.GetValue<int>() ?? 0;
        var method = envelope["method"]?.GetValue<string>() ?? "";
        var parameters = envelope["params"] as JsonObject ?? new JsonObject();

        var result = Handle(method, parameters);

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        };
    }

    public JsonObject Handle(string method, JsonObject parameters)
    {
        switch (method)
        {
            case "tunnelSip":
            {
                var data = parameters["data"]?.GetValue<string>() ?? "";
                var response = _controller.Execute(data);
                return new JsonObject
                {
                    ["length"] = response.Length / 2,
                    ["data"] = response,
                };
            }

            case "getSettings":
                return new JsonObject
                {
                    ["StickId"] = _controller.SerialNumber,
                    ["country"] = "US",
                    ["code"] = "00000",
                };

            case "getNetworkStatus":
                return new JsonObject
                {
                    ["networkUp"] = true,
                    ["localIpAddress"] = "127.0.0.1",
                };

            case "getWifiParams":
                return new JsonObject
                {
                    ["wifiSsid"] = "Simulated Network",
                    ["macAddress"] = "AABBCCDDEEFF",
                    ["rssi"] = -52,
                };

            case "getApMode":
                return new JsonObject { ["apMode"] = false };

            case "getServerMode":
                return new JsonObject { ["serverMode"] = false, ["checkInInterval"] = 10 };

            // Accept-and-ignore: configuration writes the simulator has no use for.
            case "setWifiParams":
            case "setApMode":
            case "setServerMode":
            case "setPassword":
            case "setZipCode":
            case "setProgramInfo":
            case "setSoilType":
                return new JsonObject { ["accepted"] = true };

            default:
                return new JsonObject { ["unsupportedMethod"] = method };
        }
    }

    /// <summary>Decrypts a request body, handles it, and returns an encrypted response body.</summary>
    public byte[] HandleEncrypted(byte[] requestBody)
    {
        var plaintext = RainBirdCipher.Decrypt(_password, requestBody);
        var envelope = JsonNode.Parse(plaintext) as JsonObject
            ?? throw new RainBirdProtocolException("Simulator received a non-object request.");

        var response = HandleEnvelope(envelope);
        // Controllers hash the padded plaintext; the app hashes the unpadded one. Use
        // the device-side convention so this path tests what real hardware sends.
        return RainBirdCipher.EncryptAsController(_password, response.ToJsonString());
    }
}

/// <summary>
/// In-process transport. Skips sockets but still runs the full encrypt/decrypt and
/// JSON-RPC round trip, so tests over this path genuinely cover the crypto.
/// </summary>
public sealed class SimulatorTransport : IRainBirdTransport, ISipExchangeSource
{
    private readonly SimulatorRpcHandler _handler;
    private readonly string _password;

    public SimulatorTransport(VirtualController controller, string password = "simulator")
    {
        _handler = new SimulatorRpcHandler(controller, password);
        _password = password;
    }

    public VirtualController Controller => _handler.Controller;

    /// <summary>Artificial latency, so timing-sensitive UI work can be exercised.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    /// <summary>Set to make the next N requests fail, for retry testing.</summary>
    public int FailNextRequests { get; set; }

    /// <inheritdoc />
    public Action<SipExchange>? OnExchange { get; set; }

    public async Task<JsonObject> SendAsync(string method, JsonObject parameters, CancellationToken ct = default)
    {
        if (Latency > TimeSpan.Zero)
            await Task.Delay(Latency, ct).ConfigureAwait(false);

        var requestHex = parameters["data"]?.GetValue<string>() ?? parameters.ToJsonString();

        if (FailNextRequests > 0)
        {
            FailNextRequests--;
            OnExchange?.Invoke(new SipExchange(
                DateTimeOffset.UtcNow, method, requestHex, null, "Simulated transport failure."));
            throw new HttpRequestException("Simulated transport failure.", null, HttpStatusCode.ServiceUnavailable);
        }

        var envelope = new JsonObject
        {
            ["id"] = 1,
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters.DeepClone(),
        };

        // Round-trip through the cipher so this path covers encryption too.
        var encrypted = RainBirdCipher.Encrypt(_password, envelope.ToJsonString());
        var responseBody = _handler.HandleEncrypted(encrypted);
        var responseText = RainBirdCipher.Decrypt(_password, responseBody);

        var parsed = JsonNode.Parse(responseText) as JsonObject
            ?? throw new RainBirdProtocolException("Simulator produced a non-object response.");

        var result = parsed["result"] as JsonObject
            ?? throw new RainBirdProtocolException("Simulator response had no result.");

        OnExchange?.Invoke(new SipExchange(
            DateTimeOffset.UtcNow, method, requestHex, result["data"]?.GetValue<string>(), null));

        return result;
    }
}

/// <summary>
/// Serves the simulator over real HTTP at <c>/stick</c>, so the app can be pointed at
/// it exactly as it would be pointed at hardware.
/// </summary>
public sealed class SimulatorHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly SimulatorRpcHandler _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public SimulatorHttpServer(VirtualController controller, string password, int port)
    {
        _handler = new SimulatorRpcHandler(controller, password);
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }
    public string Host => $"127.0.0.1:{Port}";
    public VirtualController Controller => _handler.Controller;

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(context), CancellationToken.None);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            if (!context.Request.Url!.AbsolutePath.EndsWith("/stick", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body).ConfigureAwait(false);

            var responseBody = _handler.HandleEncrypted(body.ToArray());

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = responseBody.Length;
            await context.Response.OutputStream.WriteAsync(responseBody).ConfigureAwait(false);
        }
        catch (RainBirdAuthenticationException)
        {
            // A real controller can't distinguish this either; it just fails to decrypt.
            context.Response.StatusCode = 403;
        }
        catch (Exception)
        {
            context.Response.StatusCode = 500;
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception) { /* client gone */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _cts.Dispose();
    }
}
