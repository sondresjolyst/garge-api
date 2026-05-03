# garge-api

ASP.NET Core 10 REST API for the Garge smart garage monitoring system. Handles authentication, sensor data, automation rules, switch control, electricity pricing, and Web Push notifications.

## What it does

- Stores sensor readings (battery voltage, temperature, humidity) from MQTT devices in PostgreSQL
- Evaluates automation rules and triggers switch actions based on sensor values and electricity prices
- Fetches hourly electricity prices from Nord Pool for demand-responsive automations
- Sends webhooks when switch state changes (PostgreSQL LISTEN/NOTIFY → HTTP delivery)
- Sends Web Push notifications when sensors go offline (RFC 8291 / VAPID)
- Manages user accounts, JWT authentication, email verification, and GDPR data export/deletion
- Device grouping, custom names, sensor photos, battery health tracking, and activity logs

## Tech stack

| Concern | Library |
|---|---|
| Framework | ASP.NET Core 10 |
| Database | PostgreSQL — EF Core 10 (Npgsql) |
| Auth | ASP.NET Identity + JWT Bearer + refresh tokens |
| Messaging | MQTT (EMQX) |
| Email | Brevo (transactional) |
| Rate limiting | AspNetCoreRateLimit |
| Mapping | AutoMapper |
| Logging | Serilog |
| API docs | Swagger at `/swagger` |

## Configuration

Secrets via environment variables or .NET User Secrets in development:

| Key | Description |
|-----|-------------|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string |
| `Jwt__Key` | Secret key for signing JWT tokens |
| `Jwt__Issuer` | JWT issuer/audience |
| `BrevoSettings__ApiKey` | Brevo transactional email API key |
| `BrevoSettings__SenderEmail` | Sender email address |
| `BrevoSettings__SenderName` | Sender display name |
| `Vapid__Subject` | Contact URL for VAPID (`https://garge.no`) |
| `Vapid__PublicKey` | VAPID public key (base64url, 65-byte P-256 uncompressed point) |
| `Vapid__PrivateKey` | VAPID private key (base64url, 32-byte P-256 scalar) |

## Generating VAPID keys

Required for Web Push (sensor offline alerts). Generate a new pair with Node.js:

```bash
node -e "
const crypto = require('crypto');
const { privateKey, publicKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
const pubDer = publicKey.export({ type: 'spki', format: 'der' });
const privDer = privateKey.export({ type: 'pkcs8', format: 'der' });
const b64u = (b) => b.toString('base64').replace(/\+/g,'-').replace(/\//g,'_').replace(/=/g,'');
console.log('PublicKey:', b64u(pubDer.slice(-65)));
console.log('PrivateKey:', b64u(privDer.slice(36, 68)));
"
```

## Running

```bash
dotnet ef database update   # apply migrations
dotnet run
```

API docs available at `http://localhost:5277/swagger`.
