using garge_api.Models;
using garge_api.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using garge_api.Services;
using AspNetCoreRateLimit;
using garge_api.Models.Admin;
using Serilog;

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

            var jwtIssuer = builder.Configuration.GetSection("Jwt:Issuer").Get<string>();
            var jwtKey = builder.Configuration.GetSection("Jwt:Key").Get<string>() ?? string.Empty;
            builder.Configuration.AddEnvironmentVariables();

            //builder.Logging.ClearProviders();
            //builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
            //builder.Logging.AddSimpleConsole(options =>
            //{
            //    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            //    options.IncludeScopes = false; // disables the extra scope/tracing info
            //    options.SingleLine = true;
            //});
            //builder.Logging.AddFilter(DbLoggerCategory.Database.Command.Name, LogLevel.None);

            builder.Services.AddAutoMapper(typeof(MappingProfile));
            builder.Services.AddScoped<CustomDbCommandInterceptor>();

            builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
                       .EnableSensitiveDataLogging()
                       .AddInterceptors(serviceProvider.GetRequiredService<CustomDbCommandInterceptor>()));

            builder.Services.AddIdentity<User, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            builder.Services.AddControllers()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.Preserve;
                });
            builder.Services.AddHttpClient<NordPoolService>();
            builder.Services.AddHttpClient<WebhookNotificationService>();
            builder.Services.AddHostedService<PostgresNotificationService>();
            builder.Services.AddSingleton<PostgresNotificationService>();
            builder.Services.AddScoped<EmailService>();
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
            });

            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
            });

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAllOrigins",
                    builder =>
                    {
                        builder.AllowAnyOrigin()
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

                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        new string[] {}
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
            }

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Garge API V1");
            });

            app.UseStaticFiles();
            app.UseRouting();
            app.UseCors("AllowAllOrigins");
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseIpRateLimiting();
            app.MapControllers();
            app.Run();
        }
    }

    public class CustomDbCommandInterceptor : DbCommandInterceptor
    {
        private readonly ILogger<CustomDbCommandInterceptor> _logger;

        public CustomDbCommandInterceptor(ILogger<CustomDbCommandInterceptor> logger)
        {
            _logger = logger;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            LogCommand(command, eventData);
            return base.ReaderExecuting(command, eventData, result);
        }

        private void LogCommand(DbCommand command, CommandEventData eventData)
        {
            _logger.LogInformation("Executing DbCommand: {CommandText} with parameters: {Parameters} at {Timestamp}",
                command.CommandText,
                string.Join(", ", command.Parameters.Cast<DbParameter>().Select(p => $"{p.ParameterName}={p.Value}")),
                DateTime.UtcNow);
        }
    }
}
