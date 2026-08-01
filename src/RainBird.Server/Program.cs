using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using RainBird.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using RainBird.Server.Api;
using RainBird.Server.Data;
using RainBird.Server.Hubs;
using RainBird.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- persistence

// Named "store" rather than "data" so it can never collide with the Data/ source
// folder on a case-insensitive filesystem.
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "store");
Directory.CreateDirectory(dataDirectory);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "rainbird.db")}"));

// Controller passwords are encrypted at rest with these keys, so they must outlive a
// restart or every stored controller would need re-adding.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("RainBird");

// -------------------------------------------------------------------- services

// A controller is a small device on the local network: it answers in well under a
// second or it is not there. A conventional 30s timeout, combined with three
// retries, means an unreachable controller takes over a minute to report itself —
// long enough that the app looks hung rather than informative.
builder.Services.AddHttpClient("rainbird", client => client.Timeout = TimeSpan.FromSeconds(5));

// Newer LNK firmware serves the protocol over TLS with a self-signed Rain Bird
// certificate. It cannot chain to a public root, so this client pins the certificate
// the hardware presents — real verification rather than trusting whatever answers on
// port 443.
builder.Services
    .AddHttpClient("rainbird-tls", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(RainBirdCertificates.CreatePinnedHandler);
builder.Services.AddHttpClient("weather", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("RainBirdApp/1.0");
});

builder.Services.AddSingleton<ControllerRegistry>();
builder.Services.AddSingleton<HistoryRecorder>();
builder.Services.AddSingleton<SkipEvaluator>();
builder.Services.AddScoped<ControllerService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<WeatherService>();

// The simulator lets the whole app run — and be demonstrated — with no hardware.
var useSimulator = builder.Configuration.GetValue("RainBird:UseSimulator", false);
if (useSimulator)
    builder.Services.AddSingleton<IControllerTransportFactory, SimulatorTransportFactory>();
else
    builder.Services.AddSingleton<IControllerTransportFactory, HttpControllerTransportFactory>();

builder.Services.AddHostedService<PollingService>();
builder.Services.AddHostedService<SkipEvaluationService>();

// The plan engine is both a background loop and something the API calls into (to
// start a plan on demand, or cancel one), so it is registered once and hosted from
// that same instance.
builder.Services.AddSingleton<PlanRunTracker>();
builder.Services.AddSingleton<PlanExecutionService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PlanExecutionService>());

builder.Services.AddSignalR();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums travel as their names so the client never has to know the numbers.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// No CORS configuration on purpose. In production the SPA is served from this host,
// and in development the Vite dev server proxies /api, /media and /hubs here — so
// every request is same-origin and there is no cross-origin surface to allow.

var app = builder.Build();

// -------------------------------------------------------------------- pipeline

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated does not alter an existing database, and recreating one would
    // discard controller credentials the user cannot recover.
    await SchemaUpgrader.UpgradeAsync(
        db, scope.ServiceProvider.GetRequiredService<ILogger<Program>>());

    if (useSimulator)
        await SimulatorSeed.EnsureSeededAsync(scope.ServiceProvider);
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = SpaContentTypes(),
    OnPrepareResponse = ApplySpaCaching,
});

// Zone photos live outside wwwroot so rebuilding the SPA never deletes them.
var mediaRoot = Path.Combine(app.Environment.ContentRootPath, "media");
Directory.CreateDirectory(mediaRoot);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(mediaRoot),
    RequestPath = "/media",
});

app.MapRainBirdApi();
app.MapPlanApi();
app.MapHub<ControllerHub>("/hubs/controller");

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    simulator = useSimulator,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
}));

// Client-side routing: anything not matched above returns the SPA shell.
app.MapFallbackToFile("index.html");

WarnIfReachableFromTheNetwork(app);

app.Run();

/// <summary>
/// Static file types the built-in map may not know.
///
/// An unmapped extension is not served as some default type — it is not served at
/// all, because <c>ServeUnknownFileTypes</c> is off. A missing web manifest does not
/// announce itself either: the app still loads, and only the install prompt quietly
/// never appears.
/// </summary>
static FileExtensionContentTypeProvider SpaContentTypes()
{
    var provider = new FileExtensionContentTypeProvider();
    provider.Mappings[".webmanifest"] = "application/manifest+json";
    return provider;
}

/// <summary>
/// Cache headers for the built SPA.
///
/// Vite fingerprints everything under /assets, so those can be cached hard and
/// forever. The three files that decide which build you are running cannot be: a
/// cached service worker is a service worker that can never replace itself, which
/// would strand an installed app on whatever version it first saw.
/// </summary>
static void ApplySpaCaching(StaticFileResponseContext context)
{
    var path = context.Context.Request.Path.Value ?? string.Empty;

    var isVersioned = path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase);
    var isEntryPoint =
        path.EndsWith("/sw.js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/registerSW.js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("index.html", StringComparison.OrdinalIgnoreCase)
        || path == "/";

    context.Context.Response.Headers.CacheControl = isEntryPoint
        ? "no-cache"
        : isVersioned
            ? "public, max-age=31536000, immutable"
            : "public, max-age=86400";
}

/// <summary>
/// Says plainly, at startup, when the app is listening beyond this machine.
///
/// There is no authentication on this API: anyone who can reach the port can run
/// the sprinklers. That is a reasonable trade on a private mesh or a home LAN and a
/// bad one on an untrusted network, so which of those it is should not be something
/// you have to infer from a config file.
/// </summary>
static void WarnIfReachableFromTheNetwork(WebApplication app)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Urls.ToList();
        var exposed = addresses.Where(url =>
            !url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("127.0.0.1", StringComparison.Ordinal)
            && !url.Contains("[::1]", StringComparison.Ordinal)).ToList();

        if (exposed.Count == 0) return;

        app.Logger.LogWarning(
            "Listening on {Addresses}, so this app is reachable from the network. It has no "
            + "authentication — anyone who can reach the port can run the sprinklers. Keep it on a "
            + "trusted network, and never forward the port from the internet.",
            string.Join(", ", exposed));
    });
}

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
