using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using RainBird.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using RainBird.Server.Api;
using RainBird.Server.Data;
using RainBird.Server.Hubs;
using RainBird.Server.Services;

// The container image is chiseled: no shell, no curl, nothing to write a health
// check with. So the app checks itself. This runs before anything is built, so it
// costs a process start and one loopback request rather than a second web host.
if (args.Contains("--healthcheck")) return await RunHealthCheckAsync();

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- persistence

// Named "store" rather than "data" so it can never collide with the Data/ source
// folder on a case-insensitive filesystem.
// Overridable so the database, keys and photos can live outside the container on a
// path you can back up, rather than inside a volume you have to go digging for.
var dataDirectory = builder.Configuration["REIGNBIRD_DATA_DIR"] is { Length: > 0 } configured
    ? configured
    : Path.Combine(builder.Environment.ContentRootPath, "store");

Directory.CreateDirectory(dataDirectory);
EnsureWritable(dataDirectory);

// Zone photos, kept apart from the database so a large pile of images can live on a
// different disk if it wants to.
var mediaDirectory = builder.Configuration["REIGNBIRD_MEDIA_DIR"] is { Length: > 0 } configuredMedia
    ? configuredMedia
    : Path.Combine(builder.Environment.ContentRootPath, "media");

Directory.CreateDirectory(mediaDirectory);
EnsureWritable(mediaDirectory);

builder.Services.AddSingleton(new StoragePaths(dataDirectory, mediaDirectory));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "rainbird.db")}"));

// Controller passwords are encrypted at rest with these keys, so they must outlive a
// restart or every stored controller would need re-adding.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("RainBird");

// ------------------------------------------------------------ authentication

// Read before the host is built, because authentication has to be configured
// before there is a DbContext to ask for it.
var signingKey = SigningKeyStore.LoadOrCreate(dataDirectory, builder.Configuration);

builder.Services.AddSingleton(signingKey);
builder.Services.AddScoped<AuthService>(provider => new AuthService(
    provider.GetRequiredService<AppDbContext>(),
    signingKey,
    provider.GetRequiredService<ILogger<AuthService>>()));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = AuthService.ValidationParameters(signingKey);

        // Off, so a claim called "sub" stays called "sub". Left on, the handler
        // rewrites the standard JWT names into WS-Federation URIs from 2005, and code
        // reading the claim it just wrote finds nothing under that name.
        options.MapInboundClaims = false;

        options.Events = new JwtBearerEvents
        {
            // A browser cannot set an Authorization header on a WebSocket handshake,
            // so SignalR passes the token in the query string instead. Accepted only
            // for the hub path, so it cannot become a general-purpose way to put a
            // credential somewhere it will be logged.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },

            // A valid signature is not the whole question. The token also has to
            // agree with the account as it stands now — this is what makes deleting
            // an account or changing a password take effect immediately rather than
            // whenever the token would have run out.
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var users = context.HttpContext.RequestServices.GetRequiredService<AuthService>();

                var id = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
                var stamp = principal?.FindFirstValue(AuthService.SecurityStampClaim);

                if (!int.TryParse(id, out var userId)
                    || stamp is null
                    || !await users.IsStillValidAsync(userId, stamp))
                {
                    context.Fail("The account this token was issued for has changed.");
                }
            },
        };
    });

builder.Services.AddAuthorization();

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

    await SeedAccountFromEnvironmentAsync(scope.ServiceProvider, app.Configuration);
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = SpaContentTypes(),
    OnPrepareResponse = ApplySpaCaching,
});

app.UseAuthentication();
app.UseAuthorization();

// Zone photos live outside wwwroot so rebuilding the SPA never deletes them, and
// behind authentication because the filenames are predictable — zone-3-1.jpg is a
// photo of someone's garden that anyone who could reach the port would otherwise be
// able to guess their way to.
var mediaRoot = mediaDirectory;

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/media"),
    media =>
    {
        media.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });

        media.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(mediaRoot),
            RequestPath = "/media",
        });
    });

app.MapAuthApi();
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

// Reached when the host shuts down cleanly. Present because the health-check path
// above returns an exit code, which obliges every path to do the same.
return 0;

/// <summary>
/// Fails immediately, and legibly, if the data directory cannot be written to.
///
/// This exists for bind mounts. A host directory mounted into the container keeps
/// its own ownership, so if it belongs to your user and the container runs as
/// another, nothing here can write. Left alone that surfaces several layers later as
/// "unable to open database file", which sends people looking at SQLite rather than
/// at the one line of their compose file that is actually wrong.
/// </summary>
static void EnsureWritable(string directory)
{
    var probe = Path.Combine(directory, ".write-test");

    try
    {
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        var uid = OperatingSystem.IsWindows() ? "this account" : $"uid {Environment.UserName}";

        throw new InvalidOperationException(
            $"Cannot write to the data directory '{directory}' as {uid}. "
            + "If this is a bind mount, the host directory has to be writable by the user the "
            + "container runs as — either chown it to that user, or set PUID/PGID to your own "
            + "(id -u / id -g). Nothing can start until this is settled: the database, the "
            + "encryption keys for controller passwords, and zone photos all live here.", ex);
    }
}

/// <summary>
/// Creates an account from REIGNBIRD_ADMIN_USER and REIGNBIRD_ADMIN_PASSWORD.
///
/// For a container that comes up unattended, where waiting for someone to open the
/// setup screen is not much of a plan. Only ever creates: if the username already
/// exists the password is left alone, because a stale variable in a compose file
/// silently resetting a password somebody had since changed is a worse surprise than
/// the variable appearing to do nothing.
///
/// Locked out with no way in? Set these to a *new* username. Every account is equal,
/// so the account that appears can remove the one you cannot get into.
/// </summary>
static async Task SeedAccountFromEnvironmentAsync(IServiceProvider services, IConfiguration configuration)
{
    var username = configuration["REIGNBIRD_ADMIN_USER"]?.Trim();
    var password = configuration["REIGNBIRD_ADMIN_PASSWORD"];
    var logger = services.GetRequiredService<ILogger<Program>>();

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return;

    var problem = AuthService.ValidateUsername(username) ?? AuthService.ValidatePassword(password);
    if (problem is not null)
    {
        logger.LogWarning("REIGNBIRD_ADMIN_USER/PASSWORD ignored: {Problem}", problem);
        return;
    }

    var users = services.GetRequiredService<AuthService>();

    if (await users.FindAsync(username) is not null)
    {
        logger.LogInformation(
            "Account {Username} already exists; REIGNBIRD_ADMIN_PASSWORD left unapplied", username);
        return;
    }

    await users.CreateAsync(username, password);
    logger.LogWarning(
        "Created account {Username} from the environment. Consider removing "
        + "REIGNBIRD_ADMIN_PASSWORD now that the account exists.", username);
}

/// <summary>
/// Asks the running server whether it is healthy. Exit code 0 means yes.
/// </summary>
static async Task<int> RunHealthCheckAsync()
{
    // Whatever the app was told to listen on, ask there. A check hard-coded to 5056
    // would report a healthy container as sick the moment the port is overridden.
    var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
    var port = string.IsNullOrWhiteSpace(ports) ? "5056" : ports.Split(';')[0].Trim();

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

    try
    {
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

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
/// Sign-in protects the API, but nothing here terminates TLS: over plain HTTP a
/// password and the token it returns cross the network in the clear. That is a
/// reasonable trade on a private mesh or a home LAN and a bad one anywhere else, and
/// which of those you are on should not be something you have to infer from a config
/// file.
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
            "Listening on {Addresses}, so this app is reachable from the network. Accounts protect "
            + "the API, but this is plain HTTP with no TLS — passwords and tokens cross the network "
            + "in the clear. Keep it on a trusted network, and never forward the port from the "
            + "internet without a reverse proxy terminating HTTPS.",
            string.Join(", ", exposed));
    });
}

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
