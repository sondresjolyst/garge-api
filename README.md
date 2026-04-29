# garge-api

ASP.NET Core 8 REST API for the Garge garage monitoring system. Handles authentication, sensor data persistence, automation rule evaluation, and orchestrates switch commands to garge-operator via webhooks.

## What it does

- Stores sensor readings (battery voltage, temperature, humidity) in PostgreSQL
- Evaluates automation rules and triggers switch commands (e.g. start charger when battery drops below 12.5 V)
- Fetches hourly electricity prices from Nord Pool for demand-responsive automations
- Sends webhooks to garge-operator when a switch should toggle
- Manages user accounts and authentication (JWT + refresh tokens)

## Tech stack

- **ASP.NET Core 8** Web API
- **PostgreSQL** — Entity Framework Core 9 (Npgsql)
- **ASP.NET Identity** + JWT Bearer tokens
- **AutoMapper** — entity ↔ DTO mapping
- **Serilog** — structured logging
- **Swagger** — API docs at `/swagger`

## Configuration

Secrets are set via environment variables (or .NET User Secrets in development):

| Key | Description |
|-----|-------------|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |
| `Jwt__Key` | Secret key for signing JWT tokens |
| `Jwt__Issuer` / `Jwt__Audience` | JWT issuer and audience |
| `Email__ApiKey` | SendGrid or Brevo API key |

## Running

```bash
dotnet ef database update   # apply migrations
dotnet run
```

API docs available at `http://localhost:5277/swagger`.
