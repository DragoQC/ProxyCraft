using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProxyCraft.Configuration;
using ProxyCraft.Api.Documentation;
using ProxyCraft.Api.Hubs;
using ProxyCraft.Data;
using ProxyCraft.Models;
using ProxyCraft.Security;
using ProxyCraft.Services;
using System.Threading.RateLimiting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews((MvcOptions options) => options.Filters.AddService<ApiAntiforgeryFilter>());
builder.Services.AddScoped<ApiAntiforgeryFilter>();
builder.Services.AddScoped<ApiReferenceService>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<DomainHubConnections>();
builder.Services.AddSingleton<DomainEventPublisher>();
builder.Services.AddProblemDetails();
builder.Services.AddAntiforgery((Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions options) => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection(ProxyOptions.SectionName));
builder.Services.AddSingleton<DataPaths>();
string? keysDirectory = builder.Configuration["DataProtection:KeysDirectory"];
if (!string.IsNullOrWhiteSpace(keysDirectory))
{
    builder.Services.AddDataProtection().SetApplicationName("ProxyCraft")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keysDirectory, builder.Environment.ContentRootPath)));
}
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddDbContext<ProxyCraftDbContext>((IServiceProvider services, DbContextOptionsBuilder options) =>
{
    DataPaths paths = services.GetRequiredService<DataPaths>();
    SqliteConnectionStringBuilder connectionString = new()
    {
        DataSource = paths.DatabasePath
    };
    options.UseSqlite(connectionString.ToString());
});
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<ProxyCraft.Services.AuthenticationService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<ProxyService>();
builder.Services.AddScoped<PublicDashboardService>();
builder.Services.AddScoped<ConnectionEventService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddSingleton<MinecraftProxyService>();
builder.Services.AddHostedService((IServiceProvider services) => services.GetRequiredService<MinecraftProxyService>());
builder.Services.AddSingleton<SystemMetricsService>();
builder.Services.AddHostedService((IServiceProvider services) => services.GetRequiredService<SystemMetricsService>());
builder.Services.AddScoped<SessionCookieEvents>();
builder.Services.AddAuthentication("Management")
    .AddPolicyScheme("Management", "Dashboard or API key", (PolicySchemeOptions options) =>
        options.ForwardDefaultSelector = (HttpContext context) => context.Request.Path.StartsWithSegments("/api")
            && context.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                ? ApiKeyAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
    .AddCookie((CookieAuthenticationOptions options) =>
    {
        options.Cookie.Name = "ProxyCraft.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.EventsType = typeof(SessionCookieEvents);
    });
builder.Services.AddSingleton<IAuthorizationHandler, OptionalAuthenticationHandler>();
builder.Services.AddAuthorization((Microsoft.AspNetCore.Authorization.AuthorizationOptions options) =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().AddRequirements(new OptionalAuthenticationRequirement()).Build();
    options.AddPolicy("DomainEvents", (AuthorizationPolicyBuilder policy) =>
        policy.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
});
builder.Services.AddRateLimiter((RateLimiterOptions options) =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", (HttpContext context) => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        (string address) => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

WebApplication app = builder.Build();
await DatabaseInitializer.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
    if (builder.Configuration.GetValue("Http:RedirectToHttps", true))
    {
        app.UseHttpsRedirection();
    }
}
app.UseWhen((HttpContext context) => !context.Request.Path.StartsWithSegments("/api"),
    (IApplicationBuilder branch) => branch.UseStatusCodePagesWithReExecute("/error", "?statusCode={0}"));
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
app.MapStaticAssets().AllowAnonymous();
app.MapControllers();
app.MapHub<DomainHub>("/api/v1/hubs/domains", (Microsoft.AspNetCore.Http.Connections.HttpConnectionDispatcherOptions options) =>
    options.CloseOnAuthenticationExpiration = true);

await app.RunAsync();
