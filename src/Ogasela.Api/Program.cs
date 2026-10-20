using System.Text;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using Ogasela.Api.Authorization;
using Ogasela.Api.Common;
using Ogasela.Api.HealthChecks;
using Ogasela.Api.Middleware;
using Ogasela.Api.OpenApi;
using Ogasela.Api.RateLimiting;
using Ogasela.Application;
using Ogasela.Application.Accounts;
using Ogasela.Application.Accounts.Interfaces;
using Ogasela.Application.Common.Interfaces;
using Ogasela.Domain.Accounts;
using Ogasela.Infrastructure;
using Ogasela.Infrastructure.AdIntegrations;
using Ogasela.Infrastructure.Accounts;
using Ogasela.Infrastructure.Listings;
using Ogasela.Infrastructure.Messaging;
using Ogasela.Infrastructure.Geo;
using Ogasela.Infrastructure.Persistence;
using Ogasela.Infrastructure.Rbac;
using Ogasela.Infrastructure.Reviews;
using Ogasela.Infrastructure.Verification;
using Serilog;

// "seed-superadmin" runs the SuperAdminSeeder once from SuperAdminSeed:* config, prints what it
// did, and exits without serving requests - see scripts/seed-superadmin.sh.
var isSeedSuperAdminCommand = args.Length > 0 && args[0] == "seed-superadmin";
if (isSeedSuperAdminCommand)
{
    args = args[1..];
}

var builder = WebApplication.CreateBuilder(args);

// Credentials come from the repo-root .env (see DotEnvConfiguration) - not User Secrets.
builder.Configuration.AddDotEnv(builder.Environment.ContentRootPath);

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();

    if (context.HostingEnvironment.IsDevelopment())
    {
        configuration.WriteTo.Console();
    }
});

builder.Services.AddApplication(builder.Configuration);

// The master OTP (Default_Global_OTP) is honoured in every environment, Production included.
var masterOtpConfigured = !string.IsNullOrWhiteSpace(builder.Configuration[$"{OtpSettings.SectionName}:MasterCode"]);
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<ExampleSchemaTransformer>();
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Ogasela API - Bira Docs";
        document.Info.Description = "REST API for the Ogasela marketplace: listings, seller verification, wallet payments, messaging, reviews, notifications, AI listing tools, Facebook/TikTok ad integrations, and the internal moderation/admin back-office.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The OpenAPI document generator reads System.Text.Json's *general* HTTP JSON options, not MVC's
// - without this, /openapi/v1.json shows enums as raw integers even though every actual request/
// response over the wire uses their string names (per AddJsonOptions above).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddApiRateLimiting(builder.Configuration);

// Behind nginx - and, for web traffic, OgaselaWebClient's server - every request reaches the API
// from a proxy's address, so per-IP rate limits (and logs) would lump all users together. Trust
// X-Forwarded-For/-Proto only from the proxies listed in ForwardedHeaders:KnownProxies (IPs) or
// ForwardedHeaders:KnownNetworks (CIDR, e.g. "10.0.0.0/8"); a client sending its own header from
// anywhere else is ignored. Nothing listed (the default) means the headers are never trusted.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    // Unwrap as many hops as there are trusted proxies (nginx, then the web client's server).
    options.ForwardLimit = null;
    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        if (System.Net.IPAddress.TryParse(proxy.Trim(), out var address)) options.KnownProxies.Add(address);
    }
    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        if (System.Net.IPNetwork.TryParse(network.Trim(), out var parsed)) options.KnownIPNetworks.Add(parsed);
    }
});

// For OgaselaControlPortalUI (and any other browser-based client) - the API is called with a
// bearer token, not cookies, so credentials aren't needed here. Origins come from config
// (Cors:AllowedOrigins), defaulting to nothing in production; appsettings.Development.json adds
// the admin portal's local dev server.
const string AdminPortalCorsPolicy = "AdminPortal";
var corsAllowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy(AdminPortalCorsPolicy, policy =>
    {
        if (corsAllowedOrigins.Length > 0)
        {
            policy.WithOrigins(corsAllowedOrigins).AllowAnyHeader().AllowAnyMethod();
        }
    });
});

var jwtSecret = builder.Configuration["Jwt:Secret"] is { Length: > 0 } configuredSecret
    ? configuredSecret
    : throw new InvalidOperationException("Jwt:Secret was not found in configuration.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer was not found in configuration.");
var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience was not found in configuration.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // SignalR/WebSocket clients can't set an Authorization header on the handshake, so the
        // JWT bearer handler is taught to also accept the token from the query string - but
        // only for the hub's own path, never for ordinary REST requests.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    foreach (var role in Enum.GetNames<UserRole>())
    {
        options.AddPolicy(role, policy => policy.RequireRole(role));
    }
});

// New RBAC/Staff/Geo endpoints authorize on dynamic "perm:<key>" policy names (see
// PermissionPolicyProvider) instead of the fixed per-UserRole policies above, which stay exactly
// as they are for every existing endpoint.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Connection string 'Redis' was not found.");

builder.Services
    .AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql", tags: ["ready"])
    .AddRedis(redisConnectionString, name: "redis", tags: ["ready"])
    .AddCheck<S3HealthCheck>("s3", tags: ["ready"]);

var app = builder.Build();

if (masterOtpConfigured)
{
    app.Logger.LogWarning(
        "Master OTP (Default_Global_OTP) is ENABLED in the {Environment} environment - every OTP check also accepts it.",
        app.Environment.EnvironmentName);
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<OgaselaDbContext>();
    await dbContext.Database.MigrateAsync();

    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    var superAdminSeedOutcome = await SuperAdminSeeder.SeedAsync(dbContext, passwordHasher, app.Configuration, CancellationToken.None);
    await RbacSeeder.SeedAsync(dbContext, CancellationToken.None);
    await NigeriaGeoSeeder.SeedAsync(dbContext, CancellationToken.None);

    if (isSeedSuperAdminCommand)
    {
        var email = app.Configuration["SuperAdminSeed:Email"];
        var phone = app.Configuration["SuperAdminSeed:Phone"];
        var (message, exitCode) = superAdminSeedOutcome switch
        {
            SuperAdminSeedOutcome.Created => ($"Created SuperAdmin {email} ({phone}).", 0),
            SuperAdminSeedOutcome.PromotedExistingAccount => ($"Promoted the existing account with phone {phone} to SuperAdmin and set its email/password.", 0),
            SuperAdminSeedOutcome.AlreadySuperAdmin => ($"A SuperAdmin with phone {phone} already exists - nothing changed. Use the control portal's \"Forgot password?\" if you need a new password.", 0),
            SuperAdminSeedOutcome.EmailBelongsToAnotherAccount => ($"Email {email} already belongs to a different account than phone {phone} - nothing changed.", 1),
            _ => ("SuperAdminSeed:Phone and SuperAdminSeed:Password must both be set - nothing changed.", 1)
        };

        Console.WriteLine(message);
        return exitCode;
    }
}

var recurringJobManager = app.Services.GetRequiredService<IRecurringJobManager>();

recurringJobManager.AddOrUpdate<RawImageExpiryJob>(
    "purge-expired-biometric-images",
    job => job.RunAsync(),
    Cron.Daily());

recurringJobManager.AddOrUpdate<ListingExpiryJob>(
    "expire-listings",
    job => job.RunAsync(),
    Cron.Hourly());

recurringJobManager.AddOrUpdate<ListingExpiringSoonJob>(
    "flag-listings-expiring-soon",
    job => job.RunAsync(),
    Cron.Hourly());

recurringJobManager.AddOrUpdate<RecomputeTrustScoresJob>(
    "recompute-trust-scores",
    job => job.RunAsync(),
    Cron.Daily(3));

recurringJobManager.AddOrUpdate<AdCampaignMetricsSyncJob>(
    "sync-ad-campaign-metrics",
    job => job.RunAsync(),
    "*/20 * * * *");

// Available in every environment (not just Development) - the docs page reveals route/schema
// shapes only, never data, and every route it describes still enforces its own auth. The raw
// document lives at /openapi/v1.json; /api/v1/bira-doc is the classic Swagger UI over it.
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Ogasela API - Bira Docs");
    options.RoutePrefix = "api/v1/bira-doc";
    options.DocumentTitle = "Ogasela API - Bira Docs";
});

app.UseForwardedHeaders();
app.UseCorrelationId();

app.UseHttpsRedirection();

app.UseCors(AdminPortalCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<MessagingHub>("/hubs/messaging");

// /health: every check, for a full diagnostic view. /health/live: process-up only, no external
// dependency checks - a transient DB/Redis/S3 blip must never make an orchestrator kill and
// restart an otherwise-healthy pod. /health/ready: the "ready" tagged checks (Postgres, Redis,
// S3), for load balancers/orchestrators deciding whether to route traffic to this instance.
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
return 0;

public partial class Program;
