using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using YtGrab.Configuration;
using YtGrab.Endpoints;
using YtGrab.Models;
using YtGrab.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = 16 * 1024; // API only accepts tiny JSON bodies
});

builder.Services.Configure<DownloaderOptions>(builder.Configuration.GetSection(DownloaderOptions.SectionName));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<YtDlpService>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddHostedService<CleanupService>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<YtDlpUpdater>();
builder.Services.AddHealthChecks();

// Behind nginx/Caddy/Cloudflare we need the real client IP for rate limiting.
// Only safe if the app is reachable ONLY through your reverse proxy.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); // .NET 10 name (KnownNetworks is obsolete)
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
        await ctx.HttpContext.Response.WriteAsJsonAsync(new ApiError("Too many requests. Please slow down."), ct);

    // General limit per IP (includes polling and static files)
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.ClientIp(), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Stricter limit for the expensive endpoints (/info, /download)
    o.AddPolicy("heavy", ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(ctx.ClientIp(), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 15,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 4,
            QueueLimit = 0
        }));
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new ApiError("Something went wrong. Please try again."));
    }));
    app.UseHsts();
}

app.UseHttpsRedirection();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' https://i.ytimg.com data:; " +
        "style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    await next();
});

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapApi();
app.MapHealthChecks("/healthz");

app.Run();