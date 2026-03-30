# PrivateChatApp

A self-hosted, real-time chat application inspired by Rocket.Chat. Runs as three Docker containers — no cloud account or external service required.

## Features

- **Real-time messaging** via SignalR WebSockets
- **Public channels** and **private rooms** with invite-only access
- **Direct messages** between users
- **Message editing and deletion** (own messages only, enforced server-side)
- **Emoji reactions**
- **@mention** autocomplete
- **Message threading / replies**
- **Image and voice message uploads** (up to 10 MB, stored in MongoDB GridFS)
- **Pinned messages** per channel
- **Message search** within channels
- **Unread badges** per room
- **Push notifications** (Web Push / VAPID, optional)
- **Admin panel** (`/admin.html`) — create/delete users, manage channels, clear history, configure auto-purge
- **TOTP two-factor authentication** for the admin account
- **PWA** — installable on mobile and desktop
- **Dark theme** with CSS custom properties

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | .NET 8 Minimal API (C#) |
| Real-time | ASP.NET Core SignalR |
| Database | MongoDB 8.2.5 |
| File storage | MongoDB GridFS |
| Auth | JWT Bearer + BCrypt |
| Frontend | Vanilla HTML / JS / CSS (no framework, no build step) |
| Web server | Nginx 1.27-alpine |
| Containers | Docker Compose |

## Prerequisites

- Docker Engine + Docker Compose plugin (or Docker Desktop)
- Git

No .NET SDK, Node.js, or MongoDB installation needed on the host.

## Quick Start

### 1. Clone the repo

```bash
git clone https://github.com/isaacery02/PrivateChatApp.git
cd PrivateChatApp
```

### 2. Create your `.env` file

```bash
cp .env.example .env
```

Open `.env` and set real values for every key. **This file is git-ignored and must never be committed.** See [`.env.example`](.env.example) for all required keys and descriptions.

Minimum required keys:

```env
MONGO_ROOT_PASSWORD=<strong password>
MONGO_APP_PASSWORD=<strong password>
Jwt__Key=<32+ random characters>
ADMIN_USERNAME=admin
ADMIN_PASSWORD=<strong password>
```

### 3. Build and start

```bash
docker compose up --build -d
```

First run takes 2–4 minutes (NuGet restore + .NET compile). Subsequent builds are faster due to layer caching.

### 4. Open the app

| URL | Description |
|---|---|
| `http://localhost:8182` | Chat application |
| `http://localhost:8182/admin.html` | Admin panel |
| `http://localhost:8182/health` | Health check |

Log in to the admin panel with `ADMIN_USERNAME` / `ADMIN_PASSWORD` from your `.env`, then create user accounts from the Users tab.

## Configuration

All secrets and environment-specific values go in `.env`. Non-secret defaults are in [`backend/appsettings.json`](backend/appsettings.json).

| `.env` key | Description |
|---|---|
| `MONGO_ROOT_USERNAME` | MongoDB root user (used by Docker init) |
| `MONGO_ROOT_PASSWORD` | MongoDB root password |
| `MONGO_APP_PASSWORD` | Password for the app database user |
| `Jwt__Key` | JWT HMAC-SHA256 signing secret (32+ chars) |
| `ADMIN_USERNAME` | Admin panel username |
| `ADMIN_PASSWORD` | Admin panel password |
| `ADMIN_TOTP_SECRET` | Optional — Base32 TOTP secret for 2FA on admin login |
| `Push__VapidPublicKey` | Optional — VAPID public key for push notifications |
| `Push__VapidPrivateKey` | Optional — VAPID private key |
| `Push__VapidSubject` | Optional — VAPID contact email (`mailto:you@example.com`) |

### CORS

Set your server's address in `backend/appsettings.json` under `Cors:AllowedOrigins`, or override at runtime via Docker environment variables:

```yaml
# in docker-compose.yml environment section
Cors__AllowedOrigins__0: "http://192.168.1.50:8182"
Cors__AllowedOrigins__1: "https://chat.yourdomain.com"
```

### Admin TOTP (2FA)

1. Log in to `/admin.html` without TOTP
2. Open the **Two-Factor Authentication** card and click **Generate TOTP Secret**
3. Scan the QR code with an authenticator app (Google Authenticator, Authy, etc.)
4. Copy the Base32 secret into `.env` as `ADMIN_TOTP_SECRET=...`
5. Rebuild the backend: `docker compose up --build -d backend`

## Project Structure

```
PrivateChatApp/
├── .env.example            # Template for required secrets
├── docker-compose.yml      # Orchestrates mongodb / backend / frontend
├── mongo-init.js           # One-time DB init: creates app_user + seeds rooms
├── backend/
│   ├── Program.cs          # All API endpoints + DI wiring
│   ├── Hubs/ChatHub.cs     # SignalR real-time hub
│   ├── Models/             # Entity and DTO types
│   ├── Repositories/       # MongoDB repository interfaces + implementations
│   └── Services/           # AuthService, MessagePurgeService
├── frontend/
│   ├── index.html          # Chat SPA
│   ├── app.js              # All client-side logic
│   ├── styles.css          # Dark theme
│   ├── admin.html          # Admin panel (self-contained)
│   └── nginx.conf          # Reverse proxy config
└── docs/
    ├── deployment.md       # Detailed deployment guide
    └── llmInstructions.txt # Architecture reference for AI assistants
```

## Deployment

See [docs/deployment.md](docs/deployment.md) for full instructions including:

- Unraid-specific setup
- Cloudflare Tunnel / reverse proxy configuration
- Updating to a new version
- MongoDB data backup
- Troubleshooting

## Security Notes

- **Self-registration is permanently disabled** — only the admin can create accounts
- All user-supplied content is HTML-escaped before DOM insertion (no XSS vectors)
- JWT is stored in an `HttpOnly` cookie; CSRF protection uses the double-submit cookie pattern
- Edit/delete ownership and private room access are enforced server-side
- Rate limiting on login endpoints (brute-force protection) and file uploads
- Admin account is never stored in MongoDB — synthetic object used for JWT only

## License

MIT
