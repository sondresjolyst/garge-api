# garge-api

ASP.NET Core 8 Web API backend for the Garge smart garage system. Handles all business logic, database access, authentication, and external integrations (MQTT, webhooks, electricity prices).

## Tech Stack

| Concern | Library |
|---|---|
| Framework | ASP.NET Core 8 Web API |
| Language | C# 12 (nullable reference types, implicit usings) |
| Database | PostgreSQL via Entity Framework Core 9 (Npgsql) |
| Auth | JWT Bearer + ASP.NET Identity |
| Mapping | AutoMapper v12 |
| Logging | Serilog |
| Email | SendGrid / Brevo |
| Rate Limiting | AspNetCoreRateLimit |
| API Docs | Swagger / Swashbuckle |
| Password | BCrypt.Net |

## Folder Structure

```
Controllers/          # HTTP endpoints — one controller per domain
Models/
├── <Domain>/         # EF Core entities, organized by domain subdirectory
└── ApplicationDbContext.cs
Dtos/                 # Request and response DTO classes
Profiles/             # AutoMapper mapping profiles
Services/             # Business logic and background hosted services
Constants/            # Shared constant values
Migrations/           # EF Core migrations (auto-generated, committed to repo)
```

## Architecture Rules

### Layering
- **Controllers** handle HTTP concerns only: routing, model binding, response shaping. Keep them thin.
- **Services** own business logic. Controllers call services; services call DbContext.
- **Models** are EF Core entities. Never expose them at API boundaries — always map to a DTO first.

### DTOs and Mapping
- Define all DTOs in `Dtos/`. Use separate request and response DTOs when their shapes differ.
- All entity ↔ DTO conversion happens in AutoMapper profiles in `Profiles/`. Do not map manually in controllers or services.
- Name profiles `<Domain>Profile.cs` (e.g., `SensorProfile.cs`).

### Database Access
- All DB access goes through `ApplicationDbContext` injected via DI. No raw ADO.NET or Dapper.
- Use EF Core LINQ queries. Raw SQL is a last resort and must be documented with a comment explaining why.
- Generate migrations with `dotnet ef migrations add <Name>` and commit them to the repo.

### Authentication and Authorization
- Protect endpoints with `[Authorize]`. Use policy-based auth where role/permission checks are needed.
- Password hashing via BCrypt — never store or log plaintext passwords.
- Refresh tokens are persisted in the DB and cleaned up by `RefreshTokenCleanupService`.

### Logging
- Serilog only. Inject `ILogger<T>` via DI — never use `Console.WriteLine`.
- Use structured logging: `_logger.LogInformation("Sensor {SensorId} updated", id)`, not string interpolation.
- Log levels: Information for normal flow, Warning for recoverable issues, Error for caught exceptions with context.

### XML Documentation
- Add `/// <summary>` on all public controller actions. Swagger picks these up automatically and keeps the API docs useful.

## Naming Conventions

| Thing | Convention | Example |
|---|---|---|
| Controllers | `<Domain>Controller.cs` | `SensorController.cs` |
| Entities | Singular PascalCase | `AutomationRule.cs` |
| DTOs | `<Action><Domain>Dto.cs` | `CreateSensorDto.cs`, `SensorResponseDto.cs` |
| Services | `<Domain>Service.cs` | `ElectricityPriceFetchService.cs` |
| AutoMapper profiles | `<Domain>Profile.cs` | `SensorProfile.cs` |

## What to Avoid
- Do not return EF entities from controllers — always use DTOs.
- Do not put business logic in controllers.
- Do not bypass AutoMapper with manual mapping in multiple places — keep mapping centralized in profiles.
- Do not use `Console.WriteLine` — use Serilog.
- Do not store secrets in `appsettings.json` — use environment variables in production and User Secrets in development.
- Do not disable nullable reference type warnings — handle nulls explicitly.
