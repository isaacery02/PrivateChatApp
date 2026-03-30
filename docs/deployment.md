# ChatApp — Deployment Guide

## Prerequisites (one-time, on the host machine)

- **Docker Desktop** (Windows/Mac) or **Docker Engine + Docker Compose plugin** (Linux/Unraid)
- **Git** to clone/copy the project onto the machine

No .NET SDK, Node.js, or MongoDB installation needed — everything runs inside containers.

> **Docker Compose v1 vs v2:** Commands in this guide use `docker compose` (space, Compose v2 plugin).
> If your system has the older standalone binary, replace every `docker compose` with `docker-compose` (hyphen).
> To check: run `docker compose version` (v2) or `docker-compose --version` (v1).
> On **Unraid**, install the **Docker Compose Manager** community plugin to get v2.

---

## Step 1 — Create the `.env` file

This file must exist at `dashboard-app/.env` before you build. It is git-ignored and **never committed**.

```bash
cd dashboard-app
```

Create `dashboard-app/.env` with these exact keys:

```env
MONGO_ROOT_USERNAME=root
MONGO_ROOT_PASSWORD=your-mongo-root-password

# Password for the app_user MongoDB account created by mongo-init.js
MONGO_APP_PASSWORD=your-app-db-password

Jwt__Key=your-secret-key-minimum-32-characters-long!!

ADMIN_USERNAME=admin
ADMIN_PASSWORD=your-admin-password

# Optional: enables TOTP 2FA on the admin login (Base32-encoded secret)
# Leave blank (or omit) to skip 2FA. Generate a value from the admin panel.
# ADMIN_TOTP_SECRET=
```

Rules:

- `Jwt__Key` must be **32+ characters** (HMAC-SHA256 signing secret)
- `MONGO_ROOT_PASSWORD` can be anything — just keep it consistent
- `ADMIN_USERNAME` / `ADMIN_PASSWORD` are the credentials for the `/admin.html` panel
- `ADMIN_TOTP_SECRET` — if set, admin login requires a TOTP second factor. Generate the secret from the "Admin Two-Factor Authentication" card inside the admin panel, scan the QR code, then add the secret here and rebuild: `docker compose up --build -d backend`

---

## Step 2 — Build and start everything

```bash
docker compose up --build -d
```

This single command:

1. Pulls `mongo:8.2.5` and `nginx:1.27-alpine` from Docker Hub
2. Builds the .NET 8 backend image (restores NuGet packages, compiles Release build)
3. Builds the nginx frontend image (copies static files)
4. Starts all three containers in order: MongoDB → backend (waits for Mongo healthcheck) → nginx
5. Runs `mongo-init.js` on first start — creates `app_user` and seeds `#general` and `#random` rooms

> Takes **2–4 minutes** on first run (NuGet restore + .NET compile). Subsequent builds are faster due to layer caching.

---

## Step 3 — Verify containers are running

```bash
docker compose ps
```

Expected output — all three services should show `running` / `healthy`:

```text
NAME              IMAGE             STATUS
chat_mongodb      mongo:8.2.5       Up (healthy)
chat_backend      ...               Up
chat_frontend     ...               Up
```

Check the health endpoint:

```bash
curl http://localhost:8080/health
# → {"status":"healthy","timestamp":"..."}
```

Open `http://localhost:8080` (or `http://192.168.1.50:8080` from another device on the network).

---

## Step 4 — Verify MongoDB data

Confirm the database was initialised correctly and contains the seeded rooms.

**Open a shell inside the MongoDB container:**

```bash
docker exec -it chat_mongodb mongosh \
  --username root \
  --password your-mongo-root-password \
  --authenticationDatabase admin
```

**Switch to the app database and check collections:**

```js
use chat_db

// List all collections
show collections
// Expected: chat_rooms  messages  users  fs.files  fs.chunks

// Confirm seeded rooms exist
db.chat_rooms.find().pretty()
// Expected: two documents — #general and #random

// Check user count (should be 0 until you create accounts via admin.html)
db.users.countDocuments()

// Check message count
db.messages.countDocuments()
```

**Verify app_user was created:**

```js
use my_app_db
db.getUsers()
// Expected: app_user with readWrite on chat_db
```

**Exit the shell:**

```js
quit()
```

---

## Step 5 — Create your first user

Go to `http://localhost:8080/admin.html`, sign in with `ADMIN_USERNAME` / `ADMIN_PASSWORD` from `.env`, then create user accounts. Self-registration is disabled — all accounts are created here.

---

## Day-to-day commands

| Task | v2 (plugin) | v1 (standalone) |
| --- | --- | --- |
| Start (already built) | `docker compose up -d` | `docker-compose up -d` |
| Stop | `docker compose down` | `docker-compose down` |
| Rebuild after code change | `docker compose up --build -d` | `docker-compose up --build -d` |
| Rebuild backend only (faster) | `docker compose up --build -d backend` | `docker-compose up --build -d backend` |
| View live logs | `docker compose logs -f` | `docker-compose logs -f` |
| Backend logs only | `docker compose logs -f backend` | `docker-compose logs -f backend` |
| **Wipe all data** (destructive) | `docker compose down -v` | `docker-compose down -v` |

---

## Unraid-specific note

On Unraid, use the **Docker Compose Manager** plugin or SSH into the server and run the commands above from the project directory. The `mongo_data` Docker volume persists across container restarts automatically — it lives in Unraid's Docker appdata.

---

## PWA — Install on Android

The app is a Progressive Web App (PWA). Once deployed behind HTTPS (e.g. Cloudflare Tunnel):

1. Open the site URL in **Chrome for Android**
2. Tap the three-dot menu → **"Add to Home screen"**
3. Chrome installs it as a standalone app with the ChatApp icon

The service worker caches the app shell so the UI loads instantly on repeat visits. All API calls and SignalR connections always go to the live server — no offline messaging.
