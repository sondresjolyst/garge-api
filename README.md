# garge-api

API for Garge, serving [garge-app](https://github.com/sondresjolyst/garge-app)
and [garge-operator](https://github.com/sondresjolyst/garge-operator).

## Stack

ASP.NET Core 10, PostgreSQL through EF Core and Npgsql, ASP.NET Identity with
JWT, SignalR, Mapster, Serilog, AspNetCoreRateLimit, PuppeteerSharp, Brevo.

## Quick start

```bash
dotnet restore
dotnet ef database update   # needs a local Postgres, see appsettings.Development.json
dotnet run                  # Swagger at /swagger
```

Migrations do not run at startup. Apply them yourself before a deploy that adds
any.

## Environment

Production reads these from the cluster secret.

| Variable | Used for |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |
| `Jwt__Key`, `Jwt__Issuer` | JWT signing key and issuer. The key must match the app's `GARGE_API_JWT_SECRET` |
| `BrevoSettings__ApiKey`, `BrevoSettings__SenderEmail`, `BrevoSettings__SenderName` | Transactional email |
| `App__FrontendBaseUrl`, `App__ApiBaseUrl` | Used in links and callbacks |
| `Vapid__PublicKey`, `Vapid__PrivateKey` | Web push |
| `Vipps__*` | Payment, including the test merchant credentials |
| `PUPPETEER_NO_SANDBOX` | Set to `1` in the cluster. Chrome's own sandbox cannot start in the pod |

## What it serves

| Area | Holds |
| --- | --- |
| Sensors | Readings, history, battery health, activity |
| Switches and automations | Threshold rules that switch power sockets |
| Electricity | Spot prices and consumption |
| Shop | Products, orders and invoices, with the invoice rendered to PDF |
| Push | Web push subscriptions |
| Pairing and groups | Device ownership |
| Accounts | Sign-in, JWT and refresh tokens, password reset, roles |
| `/hubs/devices` | SignalR hub the app and operator connect to |

Browse `/swagger` on a running instance for the current surface.

## Health

| Path | Reports |
| --- | --- |
| `/health` | The process is up. No dependency checks, so a database outage does not restart the pod |
| `/health/ready` | The database connection. Fails while Postgres is unreachable, which takes the pod out of its Service |

Both are anonymous, and both are blocked at the ingress: only the kubelet
reaches them, over the pod address.

## PDF invoices

`Services/PuppeteerPdfRenderer.cs` launches the Chrome that the image installs
and prints the invoice HTML. Chrome writes its profile and crash handler state
under `$HOME`, so the pod mounts a volume at `/home/app`, and it needs
`PUPPETEER_NO_SANDBOX=1` because its own sandbox cannot start there. Without
either, rendering produces no output.

## Deployment

Image [`sondresjo/garge-api`](https://hub.docker.com/r/sondresjo/garge-api) on
Docker Hub, chart `garge-api` in
[garge](https://github.com/sondresjolyst/garge), applied by Flux from
[tumo-flux](https://github.com/sondresjolyst/tumo-flux) to `garge-dev` and
`garge-prod`.

The container runs as the non-root `app` user with a read-only root filesystem,
so anything written at runtime needs a volume: `/tmp` and `/home/app`. Data
protection keys are persisted to the database rather than the filesystem.

A push to `main` builds the `dev` tag. A release-please release builds `vX.Y.Z`,
tags it `latest` and opens a chart bump against
[garge](https://github.com/sondresjolyst/garge). Cluster secrets are created by
[`scripts/garge/bootstrap.sh`](https://github.com/sondresjolyst/tumo-platform/blob/main/scripts/garge/bootstrap.sh)
in [tumo-platform](https://github.com/sondresjolyst/tumo-platform).

## License

Proprietary. Copyright (c) 2026 Sondre Sjølyst.
