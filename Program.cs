using System.Security.Cryptography;
using System.Text;

var cliArgs = Environment.GetCommandLineArgs();

// If invoked with a subcommand, run the CLI instead of the web server.
if (cliArgs.Length > 1 && !cliArgs[1].StartsWith('-') && cliArgs[1] != "--")
{
    var cliBuilder = Host.CreateApplicationBuilder(cliArgs);
    cliBuilder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
    cliBuilder.Configuration.AddEnvironmentVariables();
    cliBuilder.Services.AddSingleton<VaultIndex>();
    cliBuilder.Services.AddHostedService<VaultIndexBackgroundService>();
    cliBuilder.Services.AddSingleton<VaultKernel>();
    cliBuilder.Services.AddHostedService(provider => provider.GetRequiredService<VaultKernel>());
    cliBuilder.Logging.ClearProviders();
    cliBuilder.Logging.AddConsole();
    using var cliHost = cliBuilder.Build();
    await cliHost.StartAsync();
    try
    {
        return await VaultCli.RunAsync(cliArgs[1..], cliHost.Services);
    }
    finally
    {
        await cliHost.StopAsync();
    }
}

var builder = WebApplication.CreateBuilder(cliArgs);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Information;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Static bearer token for public deployments.
// Prefer MCP_BEARER_TOKEN env var (recommended on VPS).
var mcpConfig = builder.Configuration.GetSection("Mcp");
var configuredBearerToken =
    Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN")
    ?? mcpConfig["BearerToken"];
var configuredBearerTokenHash = string.IsNullOrWhiteSpace(configuredBearerToken)
    ? null
    : SHA256.HashData(Encoding.UTF8.GetBytes(configuredBearerToken));

if (string.IsNullOrWhiteSpace(configuredBearerToken) || configuredBearerToken.Contains("change-me"))
{
    Console.WriteLine("WARNING: No valid MCP bearer token configured. Set MCP_BEARER_TOKEN or Mcp:BearerToken.");
}

// Critical for remote AI clients (Grok web, Claude web, ChatGPT connectors, etc.):
// Stateful Streamable HTTP requires clients to keep Mcp-Session-Id and often
// open a long-lived SSE GET. Many hosted clients fail that handshake, so they
// "connect" (initialize works) but never see tools (tools/list fails / empty).
// Stateless mode makes every request self-contained — no session affinity needed.
builder.Services.AddSingleton<VaultIndex>();
builder.Services.AddHostedService<VaultIndexBackgroundService>();
builder.Services.AddSingleton<VaultKernel>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<VaultKernel>());

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
    })
    .WithTools<ObsidianTools>();

var app = builder.Build();

app.UseCors("AllowAll");

// === Public endpoints ===
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "aimcp", mode = "stateless" }));
app.MapMethods("/", new[] { "HEAD", "OPTIONS" }, () => Results.Ok());
app.MapMethods("/health", new[] { "HEAD", "OPTIONS" }, () => Results.Ok());
app.MapMethods("/mcp", new[] { "OPTIONS" }, () => Results.Ok());

// Protected resource metadata (bearer-friendly)
app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx) =>
{
    var scheme = ctx.Request.IsHttps ? "https" : "http";
    var host = ctx.Request.Host.Value;
    return Results.Json(new
    {
        resource = $"{scheme}://{host}/mcp",
        bearer_methods_supported = new[] { "header" },
        token_types_supported = new[] { "Bearer" }
    });
});

// Do not advertise a full authorization server (avoids OAuth client-registration wizard)
app.MapGet("/.well-known/oauth-authorization-server", () => Results.NotFound());

// === Bearer auth for /mcp only (skip OPTIONS preflight) ===
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp")
               && !HttpMethods.IsOptions(context.Request.Method),
    appBuilder =>
    {
        appBuilder.Use(async (ctx, next) =>
        {
            if (configuredBearerTokenHash == null)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("MCP server misconfigured: no bearer token set");
                return;
            }

            var authHeader = ctx.Request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";

            if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                await ctx.Response.WriteAsync("Unauthorized: Bearer token required");
                return;
            }

            var providedToken = authHeader.Substring(bearerPrefix.Length).Trim();
            var providedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedToken));
            if (!CryptographicOperations.FixedTimeEquals(providedTokenHash, configuredBearerTokenHash))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                await ctx.Response.WriteAsync("Unauthorized: invalid token");
                return;
            }

            await next();
        });
    });

app.MapMcp("/mcp");

await app.RunAsync();
return 0;
