# GitHub Copilot — ChatApp Project Instructions

> **Before answering any question or making any code change in this repository,
> read `docs/llmInstructions.txt` in full.**  It is the single source of truth
> for this project's architecture, conventions, and hard constraints.

## Quick-reference summary (read the full file for details)

### What this project is
A self-hosted, Rocket.Chat-style real-time chat app running as three Docker
containers (MongoDB 8.2.5 · .NET 8 Minimal API · Nginx) on an Unraid server.

### Hard constraints — never violate these
| Constraint | Rule |
|---|---|
| MongoDB connection | ALWAYS `MongoClientSettings` + `MongoCredential` — **never** a connection string URL (password contains `@`) |
| Docker images | Linux-only — `mcr.microsoft.com/dotnet/*`, `mongo:8.2.5`, `nginx:1.27-alpine` |
| Frontend | Plain HTML / Vanilla JS / CSS — **no** framework, **no** build step |
| Self-registration | **Permanently disabled** — only admin creates accounts |
| Admin account | **Never** stored in MongoDB — synthetic `User` object for JWT only |
| File storage | GridFS (already integrated) — use it for all binary/file storage |
| DOM output | **All** user content must pass through `escapeHtml()` — no exceptions |
| Auth enforcement | Edit/delete ownership & private room access: **server-side only** |

### Key versions
- .NET SDK / Runtime: **8.0**
- MongoDB.Driver (NuGet): **3.2.0** (includes GridFS)
- JwtBearer (NuGet): **8.0.13**
- BCrypt.Net-Next (NuGet): **4.0.3**
- SignalR, Rate Limiter, Minimal API: built into .NET 8 (no extra packages)

### Backend patterns
- **All endpoints live in `Program.cs`** — no Controllers, no Startup.cs.
- New collection → Repository interface + impl → `AddSingleton` in Program.cs.
- New SignalR server event → method in `ChatHub.cs` + handler in `connectSignalR()` in `app.js`.
- Rate-limit sensitive endpoints with `.RequireRateLimiting(...)`.

### Important files
| File | Purpose |
|---|---|
| `backend/Program.cs` | All Minimal API endpoints + DI wiring |
| `backend/Hubs/ChatHub.cs` | All SignalR real-time methods |
| `backend/Models/Dtos.cs` | All request/response DTOs |
| `frontend/app.js` | All client-side logic (~784 lines) |
| `.env` | Secrets — **git-ignored, never commit** |
| `docs/llmInstructions.txt` | Full architecture reference |

### Secret management
- `appsettings.json` — non-secret defaults only.
- Production secrets — Docker env vars via `.env` file (`env_file: - .env`).
- `Jwt__Key`, `ADMIN_USERNAME`, `ADMIN_PASSWORD`, `MONGO_ROOT_PASSWORD` are all in `.env`.
