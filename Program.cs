using garge_api.Models;
using garge_api.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using System.Reflection;
using garge_api.Services;
using AspNetCoreRateLimit;
using garge_api.Models.Admin;
using Serilog;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;
using garge_api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
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
            var jwtIssuer = builder.Configuration.GetSection("Jwt:Issuer").Get<string>();
            var jwtKey = builder.Configuration.GetSection("Jwt:Key").Get<string>() ?? string.Empty;

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

            builder.Services.AddAutoMapper(_ => { }, typeof(MappingProfile));

            builder.Services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

            builder.Services.AddIdentity<User, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            builder.Services.AddControllers()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
                });
            builder.Services.AddHttpClient<NordPoolService>();
            builder.Services.AddHostedService<ElectricityPriceFetchService>();
            builder.Services.AddHttpClient<WebhookNotificationService>();
            builder.Services.AddHostedService<PostgresNotificationService>();
            builder.Services.AddSingleton<PostgresNotificationService>();
            builder.Services.AddHostedService<garge_api.Services.RefreshTokenCleanupService>();
            builder.Services.AddHttpClient("webpush");
            builder.Services.AddScoped<IWebPushService, WebPushService>();
            builder.Services.AddHostedService<SensorOfflineCheckService>();
            builder.Services.AddScoped<IEmailService, EmailService>();
            builder.Services.AddEndpointsApiExplorer();

            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtIssuer,
                    ValidAudience = jwtIssuer,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
                };
                options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var userId = ctx.Principal?.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
                        if (string.IsNullOrEmpty(userId))
                        {
                            ctx.Fail("Missing user id claim.");
                            return;
                        }

                        var cache = ctx.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
                        var cacheKey = $"user_exists:{userId}";
                        if (cache.TryGetValue(cacheKey, out bool exists))
                        {
                            if (!exists) ctx.Fail("User no longer exists.");
                            return;
                        }

                        var db = ctx.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                        exists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId);
                        cache.Set(cacheKey, exists, TimeSpan.FromSeconds(60));
                        if (!exists) ctx.Fail("User no longer exists.");
                    }
                };
            });

            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
                options.AddPolicy("ActiveSubscription", policy =>
                    policy.AddRequirements(new ActiveSubscriptionRequirement()));
            });
            builder.Services.AddSingleton<IAuthorizationHandler, ActiveSubscriptionHandler>();
            builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
            builder.Services.Configure<VippsOptions>(builder.Configuration.GetSection("Vipps"));
            builder.Services.AddDataProtection()
                .PersistKeysToDbContext<ApplicationDbContext>()
                .SetApplicationName("garge-api");
            builder.Services.AddSingleton<IWebhookSecretProtector, WebhookSecretProtector>();
            builder.Services.AddSingleton<IAppSettingsCache, AppSettingsCache>();
            builder.Services.AddScoped<IInvoiceService, InvoiceService>();
            builder.Services.AddScoped<IOrderEmailService, OrderEmailService>();
            builder.Services.AddHttpClient<IVippsService, VippsService>();
            builder.Services.AddHostedService<VippsWebhookRegistrationService>();
            builder.Services.AddHostedService<ProcessedWebhookEventCleanupService>();

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
                              .AllowAnyHeader();
                    });
            });

            builder.Services.AddMemoryCache();
            builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
            builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
            builder.Services.AddInMemoryRateLimiting();

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
                var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var postgresNotificationService = scope.ServiceProvider.GetRequiredService<PostgresNotificationService>();
                logger.LogInformation("PostgresNotificationService started");

                context.EnsureTriggers();

                foreach (var roleName in RoleNames.AllRoles)
                {
                    var roleExist = await roleManager.RoleExistsAsync(roleName);
                    if (!roleExist)
                    {
                        await roleManager.CreateAsync(new IdentityRole(roleName));
                    }
                }

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

            var fwd = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 2
            };
            fwd.KnownNetworks.Clear();
            fwd.KnownProxies.Clear();
            app.UseForwardedHeaders(fwd);

            app.UseResponseCompression();
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Garge API V1");
            });

            app.UseRouting();
            app.UseMiddleware<RequestLoggingMiddleware>();
            app.UseCors("AllowAllOrigins");
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseIpRateLimiting();
            app.MapControllers();
            app.Run();
        }
    }


}
