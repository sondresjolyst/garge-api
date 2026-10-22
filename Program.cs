using garge_api.Models;
using garge_api.Constants;
using Mapster;
using MapsterMapper;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using System.Reflection;
using garge_api.Services;
using garge_api.Models.Admin;
using Serilog;
using SjolystInnovation.AppKit.Auth;
using SjolystInnovation.AppKit.Hosting;
using SjolystInnovation.AppKit.Email;
using SjolystInnovation.AppKit.AdminStats;
using SjolystInnovation.AppKit.SiteSettings;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;
using garge_api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace garge_api
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .Enrich.FromLogContext()
                .ReadFrom.Configuration(builder.Configuration)
                .CreateLogger();

            builder.Host.UseSerilog();

            builder.Configuration.AddEnvironmentVariables();

            builder.Services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
            });
            builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
            {
                options.Level = CompressionLevel.Fastest;
            });
            builder.Services.Configure<GzipCompressionProviderOptions>(options =>
            {
                options.Level = CompressionLevel.Fastest;
            });

            var mapsterConfig = TypeAdapterConfig.GlobalSettings;
            mapsterConfig.Scan(typeof(MappingProfile).Assembly);
            builder.Services.AddSingleton(mapsterConfig);
            builder.Services.AddScoped<IMapper, ServiceMapper>();

            builder.Services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

            builder.Services.AddAppKitIdentity<User, ApplicationDbContext>();

            builder.Services.AddControllers()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
                });
            builder.Services.AddHttpClient<NordPoolService>();
            builder.Services.AddHostedService<ElectricityPriceFetchService>();
            builder.Services.AddSignalR();
            builder.Services.AddSingleton<garge_api.Hubs.IHubConnectionTracker, garge_api.Hubs.HubConnectionTracker>();
            builder.Services.AddSingleton<IDeviceOwnershipService, DeviceOwnershipService>();
            builder.Services.AddSingleton<CoalescingDispatcher>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<CoalescingDispatcher>());
            builder.Services.AddSingleton<IDeviceSettingsPublisher>(sp => sp.GetRequiredService<CoalescingDispatcher>());
            builder.Services.AddSingleton<BatteryHealthAnalyzerService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<BatteryHealthAnalyzerService>());
            builder.Services.AddHostedService<PostgresNotificationService>();
            builder.Services.AddSingleton<PostgresNotificationService>();
            builder.Services.AddHttpClient("webpush");
            builder.Services.AddSingleton<IWebPushService, WebPushService>();
            builder.Services.AddHostedService<SensorOfflineCheckService>();
            builder.Services.AddAppKitBrevo(builder.Configuration.GetSection("BrevoSettings"));
            builder.Services.AddScoped<IEmailService, EmailService>();
            builder.Services.AddScoped<IEmailBrand, AuthEmailBrand>();
            var frontend = builder.Configuration["App:FrontendBaseUrl"]?.TrimEnd('/');
            builder.Services.AddAppKitAuthEmails<User>(o =>
            {
                o.SetPasswordUrl = $"{frontend}/reset-password";
                o.VerifyEmailUrl = $"{frontend}/profile";
            });
            builder.Services.AddAppKitVerificationEmails<User>();
            builder.Services.AddScoped<IUserRolesChangedHandler<User>, SecurityRoleSync>();
            builder.Services.AddAppKitAccounts<User, ApplicationDbContext>(o => o.AdminRole = RoleNames.Admin);
            builder.Services.AddScoped<IAccountDataEraser<User>, AccountDataEraser>();
            builder.Services.AddScoped<IAccountDeletedHandler<User>, AccountHubDisconnect>();
            builder.Services.AddAppKitAuth<ApplicationDbContext>(builder.Configuration.GetSection("Jwt"));
            builder.Services.AddProblemDetails();
            builder.Services.AddEndpointsApiExplorer();

            builder.Services.AddGargeJwtBearer();

            builder.Services.AddAppKitHealthChecks<ApplicationDbContext>();

            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
                options.AddPolicy("ActiveSubscription", policy =>
                    policy.AddRequirements(new ActiveSubscriptionRequirement()));
            });
            builder.Services.AddSingleton<IAuthorizationHandler, ActiveSubscriptionHandler>();
            builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbidResultHandler>();
            builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
            builder.Services.Configure<VippsSettings>(builder.Configuration.GetSection("Vipps"));
            builder.Services.AddDataProtection()
                .PersistKeysToDbContext<ApplicationDbContext>()
                .SetApplicationName("garge-api")
                .ProtectKeysWithConfiguredCertificate(builder.Configuration, required: builder.Environment.IsProduction());
            builder.Services.AddSingleton<IAppSettingsCache, AppSettingsCache>();
            builder.Services.AddSingleton<IPdfRenderer, PuppeteerPdfRenderer>();
            builder.Services.AddScoped<ISubscriptionCapacityService, SubscriptionCapacityService>();
            builder.Services.AddScoped<IPermissionService, PermissionService>();
            builder.Services.AddScoped<IMqttAclService, MqttAclService>();
            builder.Services.AddScoped<IDeviceLeaseService, DeviceLeaseService>();
            builder.Services.AddScoped<IDeviceCommandService, DeviceCommandService>();
            builder.Services.AddHttpClient<IEmqxAdminClient, EmqxAdminClient>();
            builder.Services.AddHostedService<DeviceLeaseMaintenanceService>();
            builder.Services.AddScoped<ISecurityNotifier, SecurityNotifier>();
            builder.Services.AddScoped<ISecurityModeService, SecurityModeService>();
            builder.Services.AddScoped<IPipelineHealthService, PipelineHealthService>();
            builder.Services.AddScoped<ISecurityAlertService, SecurityAlertService>();
            builder.Services.AddHostedService<QuotaReconciliationService>();
            builder.Services.AddHostedService<SuspendedSensorPurgeService>();
            builder.Services.AddAppKitDailyStats<ApplicationDbContext, DailyStatSnapshot>(DailyStatsSamplers.Configure);
            builder.Services.AddHostedService<DeletedUserPurgeService>();
            builder.Services.AddScoped<IInvoiceService, InvoiceService>();
            builder.Services.AddScoped<IVatThresholdService, VatThresholdService>();
            builder.Services.AddHostedService<InvoicePdfRetryService>();
            builder.Services.AddScoped<IAnonymizationService, AnonymizationService>();
            builder.Services.AddScoped<IOrderEmailService, OrderEmailService>();
            builder.Services.AddScoped<ISubscriptionEmailService, SubscriptionEmailService>();
            builder.Services.AddSingleton<IVippsEnvironment, GargeVippsEnvironment>();
            builder.Services.AddAppKitVipps(_ => { });
            builder.Services.AddOptions<VippsOptions>().Configure<Microsoft.Extensions.Options.IOptions<VippsSettings>>((vipps, settings) =>
            {
                var s = settings.Value;
                vipps.Live = new VippsCredentials
                {
                    BaseUrl = s.BaseUrl, ClientId = s.ClientId, ClientSecret = s.ClientSecret,
                    SubscriptionKey = s.SubscriptionKey, MerchantSerialNumber = s.MerchantSerialNumber,
                };
                vipps.Test = new VippsCredentials
                {
                    BaseUrl = s.TestBaseUrl, ClientId = s.TestClientId, ClientSecret = s.TestClientSecret,
                    SubscriptionKey = s.TestSubscriptionKey, MerchantSerialNumber = s.TestMerchantSerialNumber,
                };
                var assembly = Assembly.GetExecutingAssembly();
                vipps.SystemName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "garge";
                vipps.SystemVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? assembly.GetName().Version?.ToString() ?? "1.0.0";
            });
            builder.Services.AddTransient<IVippsService, VippsService>();
            builder.Services.AddGargeVippsWebhooks();
            builder.Services.AddHostedService<SubscriptionChargeSchedulerService>();
            builder.Services.AddScoped<IPriceChangeService, PriceChangeService>();
            builder.Services.AddSingleton<PriceChangeSweepService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PriceChangeSweepService>());

            var allowedOrigins = builder.Configuration
                .GetSection("Cors:AllowedOrigins")
                .Get<string[]>() ?? [];

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAllOrigins",
                    policy =>
                    {
                        policy.WithOrigins(allowedOrigins)
                              .AllowAnyMethod()
                              .AllowAnyHeader()
                              .AllowCredentials();
                    });
            });

            builder.Services.AddMemoryCache();
            builder.Services.AddAppKitRateLimiting(builder.Configuration.GetSection("RateLimiting"));

            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Garge API", Version = "v1" });
                c.IncludeXmlComments(xmlPath);

                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    In = ParameterLocation.Header,
                    Description = "Please enter a valid token",
                    Name = "Authorization",
                    Type = SecuritySchemeType.Http,
                    BearerFormat = "JWT",
                    Scheme = "Bearer"
                });

                c.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecuritySchemeReference("Bearer"),
                        new List<string>()
                    }
                });
            });

            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("Starting API.");
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var postgresNotificationService = scope.ServiceProvider.GetRequiredService<PostgresNotificationService>();
                logger.LogInformation("PostgresNotificationService started");

                await app.Services.MigrateAppKitDatabaseAsync<ApplicationDbContext>(app.Configuration, o =>
                {
                    // An index build on a large table takes longer than the default command timeout.
                    o.CommandTimeout = TimeSpan.FromMinutes(15);
                });

                context.EnsureTriggers();

                await app.Services.SeedAppKitRolesAsync(RoleNames.AllRoles);
                await app.Services.SeedAppKitLegalPagesAsync<ApplicationDbContext>(LegalSeedText.All);

                foreach (var rolePermission in RoleNames.RolePermissions)
                {
                    var roleName = rolePermission.Key;
                    var permissions = rolePermission.Value;

                    foreach (var permission in permissions)
                    {
                        var rolePermissionEntry = new RolePermission
                        {
                            RoleName = roleName,
                            Permission = permission
                        };

                        if (!context.RolePermissions.Any(rp => rp.RoleName == roleName && rp.Permission == permission))
                        {
                            context.RolePermissions.Add(rolePermissionEntry);
                        }
                    }
                }

                await context.SaveChangesAsync();

                if (app.Environment.IsDevelopment())
                {
                    await DevDataSeeder.SeedAsync(context, logger);
                }
            }

            app.UseAppKitForwardedHeaders();

            app.UseExceptionHandler();

            app.UseAppKitSecurityHeaders();

            app.UseAppKitHttps();

            app.UseResponseCompression();
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Garge API V1");
            });

            app.UseRouting();
            app.UseAppKitRequestLogging();
            app.UseCors("AllowAllOrigins");
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAppKitRateLimiting();
            app.UseAppKitBadRequests();
            app.MapControllers();
            app.MapGargeVippsWebhooks();
            app.MapAppKitSiteSettings<ApplicationDbContext, AppSettings>("/api", SiteSettingsHooks.Configure);
            app.MapAppKitLegalPages<ApplicationDbContext>(configure: LegalSeedText.Configure);
            app.MapAppKitEmailStats(EmailStatsEndpoint.Path, EmailStatsEndpoint.Configure);
            app.MapAppKitDailyStats<ApplicationDbContext, DailyStatSnapshot>(StatsHistoryEndpoint.Prefix, StatsHistoryEndpoint.Configure);
            app.MapAppKitAuth<User, ApplicationDbContext>(configure: o => o.IsActive = u => !u.IsDeleted);
            app.MapAppKitUserAdmin<User, ApplicationDbContext>(configure: o =>
            {
                o.Roles = RoleNames.AllRoles;
                o.NewUser = u => new User { FirstName = u.FirstName, LastName = u.LastName };
            });
            app.MapHub<garge_api.Hubs.DeviceHub>(GargeAuthentication.DeviceHubPath);
            app.MapAppKitHealthChecks();
            app.Run();
        }
    }


}
