using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RainBird.Protocol;

/// <summary>
/// Carries a JSON-RPC request to the controller and brings back the response.
///
/// Abstracted so tests and the simulator can bypass sockets entirely while still
/// exercising the same encryption and JSON-RPC framing.
/// </summary>
public interface IRainBirdTransport
{
    Task<JsonObject> SendAsync(string method, JsonObject parameters, CancellationToken ct = default);

    /// <summary>
    /// Sends a request whose effect must not be repeated if the answer goes missing.
    ///
    /// A read can be retried freely: asking twice costs a round trip. A command that
    /// opens a valve cannot, because a timeout does not say whether the controller
    /// acted. Retrying a queue command that was in fact received stacks the zone a
    /// second time, and the retry budget is three — so a lost reply could water a
    /// zone three times over.
    /// </summary>
    /// <remarks>
    /// A default implementation, so transports with no retry ladder of their own —
    /// the simulator, test fakes — need no change.
    /// </remarks>
    Task<JsonObject> SendWithoutRetryAsync(string method, JsonObject parameters, CancellationToken ct = default)
        => SendAsync(method, parameters, ct);
}

/// <summary>Raw SIP exchange, surfaced for the diagnostics panel.</summary>
public sealed record SipExchange(DateTimeOffset At, string Method, string RequestHex, string? ResponseHex, string? Error);

/// <summary>
/// A transport that can report the raw traffic passing through it.
///
/// Separate from <see cref="IRainBirdTransport"/> because observing traffic is not
/// required to talk to a controller — but for a binary protocol with no published
/// specification, being able to see the actual bytes is worth a great deal, so every
/// transport we ship implements it.
/// </summary>
public interface ISipExchangeSource
{
    Action<SipExchange>? OnExchange { get; set; }
}

/// <summary>
/// The real transport: JSON-RPC 2.0 in an AES-256-CBC encrypted body, POSTed to
/// <c>/stick</c>.
///
/// Paced for what the controller is — a small embedded device that will drop
/// requests it cannot keep up with:
/// <list type="bullet">
///   <item>One request in flight at a time, with a 50 ms gap between posts.</item>
///   <item>IOException retries after 1.5 s, up to 3 times.</item>
///   <item>HTTP 503 retries after 50 ms, up to 3 times.</item>
/// </list>
/// </summary>
public sealed class HttpRainBirdTransport : IRainBirdTransport, ISipExchangeSource, IDisposable
{
    private const int DelayBetweenPostsMs = 50;
    private const int MaxIoRetries = 3;
    private const int IoRetryDelayMs = 1500;
    private const int MaxBusyRetries = 3;
    private const int BusyRetryDelayMs = 50;

    /// <summary>
    /// How long to stop trying after the retry ladder has been exhausted.
    ///
    /// Without this, an unreachable controller is catastrophic for responsiveness:
    /// requests are serialised, so every caller queues behind the previous one's full
    /// retry sequence and a simple status read can take well over a minute to fail.
    /// One caller pays the cost of discovering the controller is gone; everyone else
    /// is told immediately until it is worth checking again.
    /// </summary>
    private static readonly TimeSpan UnreachableCooldown = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _password;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _random = new();

    private DateTimeOffset _lastPostAt = DateTimeOffset.MinValue;
    private DateTimeOffset _unreachableUntil = DateTimeOffset.MinValue;
    private string? _unreachableReason;

    public HttpRainBirdTransport(string host, string password, HttpClient? httpClient = null, bool useHttps = false)
    {
        _password = password;
        var scheme = useHttps ? "https" : "http";
        _endpoint = new Uri($"{scheme}://{host}/stick");

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        if (_ownsHttpClient) _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Set to capture raw SIP traffic for the diagnostics view.</summary>
    public Action<SipExchange>? OnExchange { get; set; }

    /// <summary>True while the controller is being treated as unreachable.</summary>
    public bool IsCircuitOpen => DateTimeOffset.UtcNow < _unreachableUntil;

    public Task<JsonObject> SendAsync(string method, JsonObject parameters, CancellationToken ct = default) =>
        SendCoreAsync(method, parameters, retry: true, ct);

    /// <inheritdoc />
    public Task<JsonObject> SendWithoutRetryAsync(
        string method, JsonObject parameters, CancellationToken ct = default) =>
        SendCoreAsync(method, parameters, retry: false, ct);

    private async Task<JsonObject> SendCoreAsync(
        string method, JsonObject parameters, bool retry, CancellationToken ct)
    {
        // Fail fast rather than queueing behind another caller's retry ladder.
        if (IsCircuitOpen)
            throw new RainBirdConnectionException(
                _unreachableReason ?? $"The controller at {_endpoint.Host} is not responding.");

        // The controller cannot service concurrent requests; queue them.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have discovered the controller is gone while we waited.
            if (IsCircuitOpen)
                throw new RainBirdConnectionException(
                    _unreachableReason ?? $"The controller at {_endpoint.Host} is not responding.");

            await ThrottleAsync(ct).ConfigureAwait(false);
            var result = retry
                ? await SendWithRetriesAsync(method, parameters, ct).ConfigureAwait(false)
                : await SendExactlyOnceAsync(method, parameters, ct).ConfigureAwait(false);

            _unreachableUntil = DateTimeOffset.MinValue;
            _unreachableReason = null;
            return result;
        }
        catch (RainBirdConnectionException ex)
        {
            _unreachableUntil = DateTimeOffset.UtcNow + UnreachableCooldown;
            _unreachableReason = ex.Message;
            throw;
        }
        finally
        {
            _lastPostAt = DateTimeOffset.UtcNow;
            _gate.Release();
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        var sinceLast = DateTimeOffset.UtcNow - _lastPostAt;
        var wait = TimeSpan.FromMilliseconds(DelayBetweenPostsMs) - sinceLast;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exactly one attempt, for commands whose effect must not be repeated.
    ///
    /// A timeout here does not mean the controller did nothing — it means we do not
    /// know. Reporting that is the honest outcome; retrying would turn "maybe
    /// watered" into "watered twice".
    /// </summary>
    private async Task<JsonObject> SendExactlyOnceAsync(
        string method, JsonObject parameters, CancellationToken ct)
    {
        try
        {
            return await SendOnceAsync(method, parameters, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new RainBirdAuthenticationException("The controller rejected the password.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            throw new RainBirdConnectionException(
                $"The controller at {_endpoint.Host} did not answer. The command may or may not "
                + "have been carried out; check the controller before sending it again.", ex);
        }
    }

    private async Task<JsonObject> SendWithRetriesAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var ioAttempts = 0;
        var busyAttempts = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await SendOnceAsync(method, parameters, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
                when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                // TLS firmware answers a bad password with 403 "Invalid Password".
                // Retrying cannot help, and reporting it as "unreachable" would send
                // the user looking for a network fault that isn't there.
                throw new RainBirdAuthenticationException(
                    "The controller rejected the password.", ex);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                // The controller is busy with another operation. The app backs off only
                // briefly here — the device clears these quickly.
                if (++busyAttempts >= MaxBusyRetries)
                    throw new RainBirdConnectionException(
                        "Controller reported it was busy three times in a row.", ex);
                await Task.Delay(BusyRetryDelayMs, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       && !ct.IsCancellationRequested)
            {
                if (++ioAttempts >= MaxIoRetries)
                    throw new RainBirdConnectionException(
                        $"Could not reach the controller at {_endpoint.Host} after {MaxIoRetries} attempts.", ex);
                await Task.Delay(IoRetryDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonObject> SendOnceAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var envelope = new JsonObject
        {
            ["id"] = _random.Next(1, int.MaxValue),
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters.DeepClone(),
        };

        var plaintext = envelope.ToJsonString();
        var body = RainBirdCipher.Encrypt(_password, plaintext);

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        request.Headers.ConnectionClose = false;

        string? responseHex = null;
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var responseText = RainBirdCipher.Decrypt(_password, responseBody);

            var parsed = JsonNode.Parse(responseText) as JsonObject
                ?? throw new RainBirdProtocolException($"Controller response was not a JSON object: {responseText}");

            if (parsed.TryGetPropertyValue("error", out var error) && error is not null)
                throw new RainBirdProtocolException($"Controller returned a JSON-RPC error: {error.ToJsonString()}");

            var result = parsed["result"] as JsonObject
                ?? throw new RainBirdProtocolException($"Controller response had no result object: {responseText}");

            responseHex = result["data"]?.GetValue<string>();
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnExchange?.Invoke(new SipExchange(
                DateTimeOffset.UtcNow, method, ExtractHex(parameters), null, ex.Message));
            throw;
        }
        finally
        {
            if (responseHex is not null)
                OnExchange?.Invoke(new SipExchange(
                    DateTimeOffset.UtcNow, method, ExtractHex(parameters), responseHex, null));
        }
    }

    private static string ExtractHex(JsonObject parameters) =>
        parameters["data"]?.GetValue<string>() ?? parameters.ToJsonString();

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttpClient) _http.Dispose();
    }
}
