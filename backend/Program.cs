using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using ChatApp.Hubs;
using ChatApp.Models;
using ChatApp.Repositories;
using ChatApp.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using OtpNet;

var builder = WebApplication.CreateBuilder(args);

// ── Admin credentials (injected via .env → Docker Compose env_file) ──────────
var adminUsername     = Environment.GetEnvironmentVariable("ADMIN_USERNAME") ?? "admin";
var adminRawPassword  = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "changeme";
var adminPasswordHash = BCrypt.Net.BCrypt.HashPassword(adminRawPassword);
// If set, admin login requires a TOTP second factor (Base32-encoded secret)
var adminTotpSecret   = Environment.GetEnvironmentVariable("ADMIN_TOTP_SECRET");

// ── MongoDB ───────────────────────────────────────────────────────────────────
var mongo = builder.Configuration.GetSection("MongoDB").Get<MongoDbSettings>()!;
var mongoClientSettings = new MongoClientSettings
{
    Server = new MongoServerAddress(mongo.Host, mongo.Port),
    Credential = MongoCredential.CreateCredential(mongo.AuthSource, mongo.Username, mongo.Password)
};
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoClientSettings));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(mongo.Database));

// ── Repositories ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IMessageRepository, MessageRepository>();
builder.Services.AddSingleton<IChatRoomRepository, ChatRoomRepository>();builder.Services.AddSingleton<IChatSettingsRepository, ChatSettingsRepository>();
builder.Services.AddSingleton<IPushSubscriptionRepository, PushSubscriptionRepository>();

// ── Background Services ──────────────────────────────────────────────────
builder.Services.AddHostedService<MessagePurgeService>();
// ── Auth Service ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<AuthService>();

// ── JWT Authentication ─────────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };

        // Read JWT from HttpOnly cookie (primary) or query string (SignalR fallback)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // 1. Prefer the HttpOnly cookie for all requests
                if (ctx.Request.Cookies.TryGetValue("chatToken", out var cookieToken)
                    && !string.IsNullOrEmpty(cookieToken))
                {
                    ctx.Token = cookieToken;
                    return Task.CompletedTask;
                }
                // 2. Fallback: query-string token for SignalR WebSocket negotiation
                var qsToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(qsToken) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/chathub"))
                    ctx.Token = qsToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("AdminPolicy", p => p.RequireRole("admin"));
});

// ── Rate Limiting (.NET 8 built-in) ───────────────────────────────────────────
// "messages" policy: max 5 requests/second per IP (covers send + upload hot paths)
builder.Services.AddRateLimiter(opts =>
{
    opts.AddFixedWindowLimiter("messages", o =>
    {
        o.Window = TimeSpan.FromSeconds(1);
        o.PermitLimit = 5;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0; // reject immediately when limit hit
    });

    // Stricter limiter for login endpoints to throttle brute-force attempts
    opts.AddFixedWindowLimiter("login", o =>
    {
        o.Window = TimeSpan.FromMinutes(1);
        o.PermitLimit = 10;
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });

    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── CORS ──────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(opts =>
    opts.AddPolicy("ChatPolicy", p =>
        p.WithOrigins(allowedOrigins)
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials()));

// ── SignalR ────────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();
builder.Services.AddHttpClient("og", c =>
{
    c.Timeout = TimeSpan.FromSeconds(5);
    c.DefaultRequestHeaders.Add("User-Agent", "ChatApp-Preview/1.0");
});

// ── Auth cookie helper ───────────────────────────────────────────────────────
// Sets an HttpOnly JWT cookie and a readable CSRF-token cookie on the response.
static void SetAuthCookies(HttpContext ctx, string jwt)
{
    var isProduction = !ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
    ctx.Response.Cookies.Append("chatToken", jwt, new CookieOptions
    {
        HttpOnly  = true,
        Secure    = isProduction,
        SameSite  = SameSiteMode.Strict,
        Path      = "/",
        MaxAge    = TimeSpan.FromHours(24)
    });

    // CSRF double-submit token — readable by JS so it can be sent as a header
    var csrfToken = Convert.ToBase64String(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    ctx.Response.Cookies.Append("csrfToken", csrfToken, new CookieOptions
    {
        HttpOnly  = false,       // JS must read this
        Secure    = isProduction,
        SameSite  = SameSiteMode.Strict,
        Path      = "/",
        MaxAge    = TimeSpan.FromHours(24)
    });
}

// ── File-content validation (magic bytes) ────────────────────────────────────
// Verifies that the first bytes of a file match a known image/audio signature,
// preventing attackers from uploading HTML/executables with a spoofed Content-Type.
static bool HasValidMagicBytes(Stream stream, string claimedMimeType)
{
    const int maxHeader = 12; // longest signature we check
    Span<byte> buf = stackalloc byte[maxHeader];
    var originalPos = stream.Position;
    int read = stream.Read(buf);
    stream.Position = originalPos; // rewind for subsequent consumers

    if (read < 4) return false;

    return claimedMimeType switch
    {
        "image/jpeg" => buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF,
        "image/png"  => read >= 8 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47
                                  && buf[4] == 0x0D && buf[5] == 0x0A && buf[6] == 0x1A && buf[7] == 0x0A,
        "image/gif"  => read >= 6 && buf[0] == 0x47 && buf[1] == 0x49 && buf[2] == 0x46
                                  && buf[3] == 0x38 && (buf[4] == 0x37 || buf[4] == 0x39) && buf[5] == 0x61,
        "image/webp" => read >= 12 && buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46
                                   && buf[8] == 0x57 && buf[9] == 0x45 && buf[10] == 0x42 && buf[11] == 0x50,
        // Audio formats: OGG container (audio/ogg), RIFF/WAV, MP3 (ID3 or sync word), ftyp (MP4/M4A)
        "audio/ogg"  => read >= 4 && buf[0] == 0x4F && buf[1] == 0x67 && buf[2] == 0x67 && buf[3] == 0x53,
        "audio/wav"  => read >= 12 && buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46
                                   && buf[8] == 0x57 && buf[9] == 0x41 && buf[10] == 0x56 && buf[11] == 0x45,
        "audio/mpeg" => (buf[0] == 0x49 && buf[1] == 0x44 && buf[2] == 0x33) // ID3 tag
                     || (buf[0] == 0xFF && (buf[1] & 0xE0) == 0xE0),         // MPEG sync word
        "audio/mp4"  => read >= 8 && buf[4] == 0x66 && buf[5] == 0x74 && buf[6] == 0x79 && buf[7] == 0x70, // ftyp box
        // WebM/Matroska container (audio/webm) — EBML header
        "audio/webm" => read >= 4 && buf[0] == 0x1A && buf[1] == 0x45 && buf[2] == 0xDF && buf[3] == 0xA3,
        _ => false
    };
}

var app = builder.Build();

// ── Security Headers Middleware ──────────────────────────────────────────────
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    // Prevent clickjacking — only allow this site to frame itself
    h["X-Frame-Options"] = "SAMEORIGIN";
    // Stop browsers from MIME-sniffing the Content-Type
    h["X-Content-Type-Options"] = "nosniff";
    // Enforce HTTPS for 1 year (includeSubDomains)
    h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    // Limit Referer leakage
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // Disable browser features the app doesn't need
    h["Permissions-Policy"] = "camera=(), geolocation=(), payment=()";
    // Content-Security-Policy — allow own origin + Google Fonts + inline styles (needed for avatars)
    h["Content-Security-Policy"] = string.Join("; ",
        "default-src 'self'",
        "script-src 'self' https://cdnjs.cloudflare.com",
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
        "font-src 'self' https://fonts.gstatic.com",
        "img-src 'self' blob: data:",
        "media-src 'self' blob:",
        "connect-src 'self' wss: ws: https://cdnjs.cloudflare.com",
        "object-src 'none'",
        "base-uri 'self'",
        "frame-ancestors 'self'"
    );
    await next();
});

// ── CSRF Protection (double-submit cookie pattern) ──────────────────────────
// For state-changing methods, verify that the X-CSRF-Token header matches
// the csrfToken cookie.  GET/HEAD/OPTIONS and unauthenticated endpoints
// (login, register) are excluded.  SignalR is excluded because WebSocket
// upgrades don't carry custom headers after the initial handshake.
app.Use(async (context, next) =>
{
    var method = context.Request.Method;
    var path   = context.Request.Path.Value ?? "";

    var isStateChanging = method is "POST" or "PUT" or "PATCH" or "DELETE";
    var isExempt = path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/admin/login", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/admin/verify-totp", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/chathub", StringComparison.OrdinalIgnoreCase);

    if (isStateChanging && !isExempt)
    {
        var cookieCsrf = context.Request.Cookies["csrfToken"];
        var headerCsrf = context.Request.Headers["X-CSRF-Token"].FirstOrDefault();

        if (string.IsNullOrEmpty(cookieCsrf)
            || string.IsNullOrEmpty(headerCsrf)
            || !string.Equals(cookieCsrf, headerCsrf, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "CSRF token mismatch." });
            return;
        }
    }
    await next();
});

app.UseCors("ChatPolicy");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ── SignalR Hub ────────────────────────────────────────────────────────────────
app.MapHub<ChatHub>("/chathub");

// ═══════════════════════════════════════════════════════════════════════════════
// AUTH
// ═══════════════════════════════════════════════════════════════════════════════

// Logout — clear auth cookies
app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete("chatToken", new CookieOptions { Path = "/" });
    ctx.Response.Cookies.Delete("csrfToken", new CookieOptions { Path = "/" });
    return Results.NoContent();
});

// ── Session restore: return a fresh token + profile from the existing cookie ──
// Called on page load when localStorage has profile data but token is not in memory.
// Returns a refreshed JWT so SignalR accessTokenFactory has a valid in-memory token.
app.MapGet("/api/auth/me", async (
    ClaimsPrincipal principal, IUserRepository users, AuthService auth, HttpContext ctx) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var user   = await users.GetByIdAsync(userId);
    if (user is null) return Results.Unauthorized();
    if (user.Disabled) return Results.Json(new { error = "Account is disabled." }, statusCode: 403);

    // Issue a fresh JWT + refresh the cookies so the session clock resets
    var jwt = auth.GenerateToken(user, "user");
    SetAuthCookies(ctx, jwt);
    return Results.Ok(new
    {
        token       = jwt,
        userId      = user.Id,
        username    = user.Username,
        displayName = user.DisplayName ?? user.Username,
        avatarColor = user.AvatarColor ?? "#5865f2",
        avatarFileId = user.AvatarFileId,
        totpEnabled = user.TotpEnabled
    });
}).RequireAuthorization();

app.MapPost("/api/auth/login", async (
    LoginRequest req, IUserRepository users, AuthService auth, IConfiguration config) =>
{
    var user = await users.GetByUsernameAsync(req.Username);
    if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        return Results.Unauthorized();

    if (user.Disabled)
        return Results.Json(new { error = "Account is disabled." }, statusCode: 403);

    var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
    var jwtHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();

    // 2FA not yet configured — force first-time setup before granting access
    if (!user.TotpEnabled)
    {
        var setupToken = jwtHandler.WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer:             config["Jwt:Issuer"],
            audience:           config["Jwt:Audience"],
            claims:             [new Claim(ClaimTypes.NameIdentifier, user.Id!),
                                 new Claim(ClaimTypes.Role, "totp_setup"),
                                 new Claim("jti", Guid.NewGuid().ToString())],
            expires:            DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(jwtKey, SecurityAlgorithms.HmacSha256)));
        return Results.Ok(new { requiresTotpSetup = true, tempToken = setupToken });
    }

    // 2FA configured — require the authenticator code
    var pendingToken = jwtHandler.WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        issuer:             config["Jwt:Issuer"],
        audience:           config["Jwt:Audience"],
        claims:             [new Claim(ClaimTypes.NameIdentifier, user.Id!),
                             new Claim(ClaimTypes.Role, "totp_pending"),
                             new Claim("jti", Guid.NewGuid().ToString())],
        expires:            DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new SigningCredentials(jwtKey, SecurityAlgorithms.HmacSha256)));
    return Results.Ok(new { requiresTOTP = true, tempToken = pendingToken });
}).RequireRateLimiting("login");

// TOTP step-2 verification — accepts the pending token + 6-digit code
app.MapPost("/api/auth/verify-totp", async (
    VerifyTotpRequest req, IUserRepository users, AuthService auth, IConfiguration config, HttpContext ctx) =>
{
    var principal = ValidateTempToken(req.TempToken, "totp_pending", config);
    if (principal is null) return Results.Unauthorized();

    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var user = await users.GetByIdAsync(userId);
    if (user is null || !user.TotpEnabled || user.TotpSecret is null)
        return Results.Unauthorized();

    var totp = new Totp(Base32Encoding.ToBytes(user.TotpSecret));
    if (!totp.VerifyTotp(req.Code.Trim(), out _, VerificationWindow.RfcSpecifiedNetworkDelay))
        return Results.Json(new { error = "Invalid code." }, statusCode: 401);

    var jwt = auth.GenerateToken(user, "user");
    SetAuthCookies(ctx, jwt);
    return Results.Ok(new
    {
        token = jwt,
        username = user.Username,
        displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
        userId = user.Id,
        avatarColor = user.AvatarColor,
        avatarFileId = user.AvatarFileId,
        totpEnabled = user.TotpEnabled
    });
}).RequireRateLimiting("login");

// First-time 2FA setup step 1 — generate secret from totp_setup temp token
app.MapPost("/api/auth/setup-totp-first", async (
    SetupFirstTotpRequest req, IUserRepository users, IConfiguration config) =>
{
    var principal = ValidateTempToken(req.TempToken, "totp_setup", config);
    if (principal is null) return Results.Unauthorized();

    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var user   = await users.GetByIdAsync(userId);
    if (user is null) return Results.Unauthorized();

    var secretBytes = KeyGeneration.GenerateRandomKey(20);
    var secret      = Base32Encoding.ToString(secretBytes);
    await users.UpdateTotpAsync(userId, secret, false);

    var label  = Uri.EscapeDataString($"ChatApp:{user.Username}");
    var issuer = Uri.EscapeDataString("ChatApp");
    var uri    = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    return Results.Ok(new { secret, uri });
}).RequireRateLimiting("login");

// First-time 2FA setup step 2 — confirm code, enable TOTP, return full JWT
app.MapPost("/api/auth/confirm-totp-first", async (
    ConfirmFirstTotpRequest req, IUserRepository users, AuthService auth, IConfiguration config, HttpContext ctx) =>
{
    var principal = ValidateTempToken(req.TempToken, "totp_setup", config);
    if (principal is null) return Results.Unauthorized();

    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var user   = await users.GetByIdAsync(userId);
    if (user is null || user.TotpSecret is null) return Results.Unauthorized();

    var totp = new Totp(Base32Encoding.ToBytes(user.TotpSecret));
    if (!totp.VerifyTotp(req.Code.Trim(), out _, VerificationWindow.RfcSpecifiedNetworkDelay))
        return Results.Json(new { error = "Invalid code — check your authenticator app." }, statusCode: 400);

    await users.UpdateTotpAsync(userId, user.TotpSecret, true);
    var jwt = auth.GenerateToken(user, "user");
    SetAuthCookies(ctx, jwt);
    return Results.Ok(new
    {
        token        = jwt,
        username     = user.Username,
        displayName  = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
        userId       = user.Id,
        avatarColor  = user.AvatarColor,
        avatarFileId = user.AvatarFileId,
        totpEnabled  = true
    });
}).RequireRateLimiting("login");

// ── Admin Login ───────────────────────────────────────────────────────────────
app.MapPost("/api/admin/login", (LoginRequest req, AuthService auth, IConfiguration config, HttpContext ctx) =>
{
    if (!string.Equals(req.Username, adminUsername, StringComparison.Ordinal)
        || !BCrypt.Net.BCrypt.Verify(req.Password, adminPasswordHash))
        return Results.Unauthorized();

    // TOTP configured — issue a 5-min pending token instead of the full admin JWT
    if (!string.IsNullOrEmpty(adminTotpSecret))
    {
        var jwtKey     = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var jwtHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var pending    = jwtHandler.WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer:             config["Jwt:Issuer"],
            audience:           config["Jwt:Audience"],
            claims:             [new Claim(ClaimTypes.NameIdentifier, "admin"),
                                 new Claim(ClaimTypes.Role, "totp_pending"),
                                 new Claim("jti", Guid.NewGuid().ToString())],
            expires:            DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(jwtKey, SecurityAlgorithms.HmacSha256)));
        return Results.Ok(new { requiresTOTP = true, tempToken = pending });
    }

    var adminUser = new User { Id = "admin", Username = adminUsername };
    var jwt = auth.GenerateToken(adminUser, "admin");
    SetAuthCookies(ctx, jwt);
    return Results.Ok(new
    {
        token = jwt,
        username = adminUsername
    });
}).RequireRateLimiting("login");

// Admin TOTP step-2: verify authenticator code and exchange for full admin JWT
app.MapPost("/api/admin/verify-totp", (VerifyTotpRequest req, AuthService auth, IConfiguration config, HttpContext ctx) =>
{
    if (string.IsNullOrEmpty(adminTotpSecret)) return Results.NotFound();

    var principal = ValidateTempToken(req.TempToken, "totp_pending", config);
    if (principal is null) return Results.Unauthorized();

    // Ensure the pending token was issued for the admin account specifically
    if (principal.FindFirstValue(ClaimTypes.NameIdentifier) != "admin")
        return Results.Unauthorized();

    var totp = new Totp(Base32Encoding.ToBytes(adminTotpSecret));
    if (!totp.VerifyTotp(req.Code.Trim(), out _, VerificationWindow.RfcSpecifiedNetworkDelay))
        return Results.Json(new { error = "Invalid code." }, statusCode: 401);

    var adminUser = new User { Id = "admin", Username = adminUsername };
    var jwt = auth.GenerateToken(adminUser, "admin");
    SetAuthCookies(ctx, jwt);
    return Results.Ok(new
    {
        token    = jwt,
        username = adminUsername
    });
}).RequireRateLimiting("login");

// Admin TOTP setup: generate a new Base32 secret + QR URI for first-time or reset setup
// Requires an existing admin JWT (admin logs in with password, visits the setup card)
app.MapGet("/api/admin/setup-totp", () =>
{
    var secretBytes = KeyGeneration.GenerateRandomKey(20);
    var secret      = Base32Encoding.ToString(secretBytes);
    var label  = Uri.EscapeDataString($"ChatApp:{adminUsername}");
    var issuer = Uri.EscapeDataString("ChatApp");
    var uri    = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    return Results.Ok(new { secret, uri });
}).RequireAuthorization("AdminPolicy");

// ═══════════════════════════════════════════════════════════════════════════════
// ADMIN — User Management
// ═══════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/admin/users", async (IUserRepository users) =>
{
    var all = await users.GetAllAsync();
    return Results.Ok(all.Select(u => new
    {
        u.Id, u.Username, u.DisplayName, u.AvatarColor, u.AvatarFileId, u.CreatedAt, u.Disabled
    }));
}).RequireAuthorization("AdminPolicy");

app.MapPost("/api/admin/users", async (CreateUserRequest req, IUserRepository users) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Username and password are required." });

    if (req.Username.Length < 3)
        return Results.BadRequest(new { error = "Username must be at least 3 characters." });

    if (req.Password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters." });

    if (await users.GetByUsernameAsync(req.Username) is not null)
        return Results.Conflict(new { error = "Username already taken." });

    var user = new User
    {
        Username     = req.Username,
        DisplayName  = req.Username,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
        CreatedAt    = DateTime.UtcNow
    };
    await users.CreateAsync(user);
    return Results.Created($"/api/admin/users/{user.Id}",
        new { user.Id, user.Username, user.DisplayName, user.CreatedAt });
}).RequireAuthorization("AdminPolicy");

app.MapDelete("/api/admin/users/{id}", async (string id, IUserRepository users) =>
{
    if (await users.GetByIdAsync(id) is null) return Results.NotFound();
    await users.DeleteAsync(id);
    return Results.NoContent();
}).RequireAuthorization("AdminPolicy");

app.MapDelete("/api/admin/rooms/{id}", async (
    string id,
    IChatRoomRepository rooms,
    IMessageRepository msgs,
    IMongoClient mongoClient) =>
{
    if (await rooms.GetByIdAsync(id) is null) return Results.NotFound();

    // Use a MongoDB transaction so message deletion and room deletion are atomic.
    // If either fails, both are rolled back — prevents orphaned messages or missing rooms.
    using var session = await mongoClient.StartSessionAsync();
    session.StartTransaction();
    try
    {
        await msgs.DeleteByRoomAsync(id);
        await rooms.DeleteAsync(id);
        await session.CommitTransactionAsync();
    }
    catch
    {
        await session.AbortTransactionAsync();
        throw;
    }
    return Results.NoContent();
}).RequireAuthorization("AdminPolicy");

// ADMIN — Clear all messages in a room without deleting the room itself
app.MapDelete("/api/admin/rooms/{id}/messages", async (
    string id,
    IChatRoomRepository rooms,
    IMessageRepository msgs) =>
{
    if (await rooms.GetByIdAsync(id) is null) return Results.NotFound();
    await msgs.DeleteByRoomAsync(id);
    return Results.NoContent();
}).RequireAuthorization("AdminPolicy");

// List all rooms with message counts (admin panel)
app.MapGet("/api/admin/rooms", async (
    IChatRoomRepository rooms,
    IMessageRepository msgs) =>
{
    var allRooms = await rooms.GetAllAsync();
    var results = await Task.WhenAll(allRooms.Select(async r => new
    {
        r.Id, r.Name, r.Description, r.IsPrivate,
        messageCount = await msgs.CountByRoomAsync(r.Id!)
    }));
    return Results.Ok(results);
}).RequireAuthorization("AdminPolicy");

// ═══════════════════════════════════════════════════════════════════════════════
// ADMIN — Settings (auto-purge)
// ═══════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/admin/settings", async (IChatSettingsRepository settings) =>
{
    var cfg = await settings.GetAsync();
    return Results.Ok(new { purgeEnabled = cfg.PurgeEnabled, purgeAfterDays = cfg.PurgeAfterDays, historyLimit = cfg.HistoryLimit });
}).RequireAuthorization("AdminPolicy");

app.MapPut("/api/admin/settings", async (
    UpdateSettingsRequest req,
    IChatSettingsRepository settings) =>
{
    if (req.PurgeAfterDays < 1 || req.PurgeAfterDays > 3650)
        return Results.BadRequest(new { error = "PurgeAfterDays must be between 1 and 3650." });
    if (req.HistoryLimit < 10 || req.HistoryLimit > 200)
        return Results.BadRequest(new { error = "HistoryLimit must be between 10 and 200." });

    var cfg = await settings.GetAsync();
    cfg.PurgeEnabled   = req.PurgeEnabled;
    cfg.PurgeAfterDays = req.PurgeAfterDays;
    cfg.HistoryLimit   = req.HistoryLimit;
    await settings.SaveAsync(cfg);
    return Results.Ok(new { purgeEnabled = cfg.PurgeEnabled, purgeAfterDays = cfg.PurgeAfterDays, historyLimit = cfg.HistoryLimit });
}).RequireAuthorization("AdminPolicy");

// Enable / disable a user account (admin)
app.MapPut("/api/admin/users/{id}/disabled", async (
    string id,
    SetUserDisabledRequest req,
    IUserRepository users,
    IHubContext<ChatHub> hubCtx) =>
{
    if (await users.GetByIdAsync(id) is null) return Results.NotFound();
    await users.SetDisabledAsync(id, req.Disabled);

    // If disabling, force-logout all of the user's active SignalR connections
    // so their session is immediately invalidated (not just on next API call).
    if (req.Disabled)
    {
        await hubCtx.Clients.User(id).SendAsync("ForceLogout",
            "Your account has been disabled by an administrator.");
    }

    return Results.NoContent();
}).RequireAuthorization("AdminPolicy");

// ═══════════════════════════════════════════════════════════════════════════════
// USER PROFILE  (authenticated user updating their own profile / avatar)
// ═══════════════════════════════════════════════════════════════════════════════

// 2FA — generate a new TOTP secret and store it (pending confirmation)
app.MapPost("/api/users/2fa/setup", async (
    IUserRepository users, ClaimsPrincipal principal) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var user = await users.GetByIdAsync(userId);
    if (user is null) return Results.Unauthorized();

    var secretBytes = KeyGeneration.GenerateRandomKey(20);
    var secret = Base32Encoding.ToString(secretBytes);
    await users.UpdateTotpAsync(userId, secret, false); // not yet enabled until confirmed

    var label  = Uri.EscapeDataString($"ChatApp:{user.Username}");
    var issuer = Uri.EscapeDataString("ChatApp");
    var uri    = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    return Results.Ok(new { secret, uri });
}).RequireAuthorization();

// 2FA — confirm the pending secret with a valid TOTP code → activates 2FA
app.MapPost("/api/users/2fa/confirm", async (
    ConfirmTotpRequest req, IUserRepository users, ClaimsPrincipal principal) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var user = await users.GetByIdAsync(userId);
    if (user is null || user.TotpSecret is null)
        return Results.BadRequest(new { error = "Run /api/users/2fa/setup first." });

    var totp = new Totp(Base32Encoding.ToBytes(user.TotpSecret));
    if (!totp.VerifyTotp(req.Code.Trim(), out _, VerificationWindow.RfcSpecifiedNetworkDelay))
        return Results.Json(new { error = "Invalid code — check your authenticator app." }, statusCode: 400);

    await users.UpdateTotpAsync(userId, user.TotpSecret, true);
    return Results.Ok(new { message = "2FA enabled." });
}).RequireAuthorization();

// 2FA — disable (requires password confirmation)
app.MapPost("/api/users/2fa/disable", async (
    DisableTotpRequest req, IUserRepository users, ClaimsPrincipal principal) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var user = await users.GetByIdAsync(userId);
    if (user is null) return Results.Unauthorized();

    if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        return Results.Json(new { error = "Incorrect password." }, statusCode: 401);

    await users.UpdateTotpAsync(userId, null, false);
    return Results.Ok(new { message = "2FA disabled." });
}).RequireAuthorization();

// Update display name or avatar colour
app.MapPatch("/api/users/me", async (
    UpdateProfileRequest req,
    IUserRepository users,
    ClaimsPrincipal principal) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // Validate avatarColor to prevent CSS injection (must be a hex colour like #a1b2c3)
    if (req.AvatarColor is not null &&
        !System.Text.RegularExpressions.Regex.IsMatch(req.AvatarColor, @"^#[0-9a-fA-F]{6}$"))
        return Results.BadRequest(new { error = "avatarColor must be a valid hex colour (e.g. #ff5500)." });

    await users.UpdateProfileAsync(userId, req.DisplayName, req.AvatarColor);
    var updated = await users.GetByIdAsync(userId);
    return Results.Ok(new
    {
        updated?.DisplayName,
        updated?.AvatarColor,
        updated?.AvatarFileId
    });
}).RequireAuthorization();

// Return list of all users (non-sensitive fields only) — for DM search etc.
app.MapGet("/api/users", async (IUserRepository users) =>
{
    var all = await users.GetAllAsync();
    return Results.Ok(all.Where(u => !u.Disabled).Select(u => new
    {
        id          = u.Id,
        username    = u.Username,
        displayName = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName,
        avatarColor = u.AvatarColor,
        avatarFileId= u.AvatarFileId
    }));
}).RequireAuthorization();

// Return all DM conversations for the calling user (roomId + partner info)
app.MapGet("/api/users/dm-conversations", async (
    IMessageRepository messages,
    IUserRepository users,
    ClaimsPrincipal principal) =>
{
    var callerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var dmRooms  = await messages.GetDmRoomsForUserAsync(callerId);
    var result   = new List<object>();
    foreach (var roomId in dmRooms)
    {
        var parts = roomId[3..].Split('_'); // "dm_uid1_uid2" → ["uid1","uid2"]
        if (parts.Length != 2) continue;
        var otherId = parts[0] == callerId ? parts[1] : parts[0];
        var other   = await users.GetByIdAsync(otherId);
        if (other is null) continue;
        result.Add(new { uid = otherId, username = other.Username, roomId });
    }
    return Results.Ok(result);
}).RequireAuthorization();

// Upload a profile avatar (GridFS)
app.MapPost("/api/users/avatar", async (
    HttpRequest request,
    IMongoDatabase db,
    IUserRepository users,
    ClaimsPrincipal principal) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Multipart form data required." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No file provided." });

    if (file.Length > 2 * 1024 * 1024)
        return Results.BadRequest(new { error = "Avatar must be under 2 MB." });

    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/webp" };
    if (!allowed.Contains(file.ContentType))
        return Results.BadRequest(new { error = "Only JPEG, PNG, or WebP avatars are allowed." });

    // Verify the file content actually matches the claimed MIME type (magic bytes)
    using var avatarStream = file.OpenReadStream();
    if (!HasValidMagicBytes(avatarStream, file.ContentType))
        return Results.BadRequest(new { error = "File content does not match its declared type." });

    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var bucket = new GridFSBucket(db);
    var opts = new GridFSUploadOptions
    {
        Metadata = new BsonDocument { { "contentType", file.ContentType }, { "userId", userId } }
    };
    ObjectId fileObjId;
    fileObjId = await bucket.UploadFromStreamAsync(file.FileName, avatarStream, opts);

    await users.UpdateAvatarAsync(userId, fileObjId.ToString());
    return Results.Ok(new { avatarFileId = fileObjId.ToString() });
}).RequireAuthorization();

// ═══════════════════════════════════════════════════════════════════════════════
// ROOMS
// ═══════════════════════════════════════════════════════════════════════════════

// List rooms accessible to the calling user (public + private rooms they belong to)
app.MapGet("/api/rooms", async (
    IChatRoomRepository rooms,
    ClaimsPrincipal principal) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    var role   = principal.FindFirstValue(ClaimTypes.Role);
    // Admins see all rooms; regular users see accessible rooms
    var list = (role == "admin")
        ? await rooms.GetAllAsync()
        : await rooms.GetAccessibleAsync(userId!);
    return Results.Ok(list);
}).RequireAuthorization();

app.MapPost("/api/rooms", async (
    CreateRoomRequest req,
    IChatRoomRepository rooms,
    ClaimsPrincipal principal) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "Room name is required." });

    if (await rooms.GetByNameAsync(req.Name) is not null)
        return Results.Conflict(new { error = "Channel name already exists." });

    var creatorId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var room = new ChatRoom
    {
        Name      = req.Name.ToLower().Replace(" ", "-"),
        Description = req.Description,
        IsPrivate = req.IsPrivate,
        // Creator is automatically a member of private channels
        MemberIds = req.IsPrivate ? [creatorId] : [],
        CreatedAt = DateTime.UtcNow
    };
    await rooms.CreateAsync(room);
    return Results.Created($"/api/rooms/{room.Id}", room);
}).RequireAuthorization();

// Invite a user to a private channel (only members or admin may invite)
app.MapPost("/api/rooms/{roomId}/invite", async (
    string roomId,
    InviteUserRequest req,
    IChatRoomRepository rooms,
    IUserRepository users,
    ClaimsPrincipal principal) =>
{
    var room = await rooms.GetByIdAsync(roomId);
    if (room is null) return Results.NotFound(new { error = "Room not found." });

    if (!room.IsPrivate)
        return Results.BadRequest(new { error = "Room is public; no invite needed." });

    var callerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var role     = principal.FindFirstValue(ClaimTypes.Role);
    if (role != "admin" && !room.MemberIds.Contains(callerId))
        return Results.Forbid();

    if (await users.GetByIdAsync(req.UserId) is null)
        return Results.NotFound(new { error = "User not found." });

    await rooms.AddMemberAsync(roomId, req.UserId);
    return Results.Ok(new { message = "User invited." });
}).RequireAuthorization();

// Edit a room's name and/or description (admin or private-room member)
app.MapPut("/api/rooms/{id}", async (
    string id,
    UpdateRoomRequest req,
    IChatRoomRepository rooms,
    IHubContext<ChatHub> hubCtx,
    ClaimsPrincipal principal) =>
{
    var room = await rooms.GetByIdAsync(id);
    if (room is null) return Results.NotFound(new { error = "Room not found." });

    var callerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var role     = principal.FindFirstValue(ClaimTypes.Role);
    if (role != "admin" && !(room.IsPrivate && room.MemberIds.Contains(callerId)))
        return Results.Forbid();

    string? normalizedName = null;
    if (!string.IsNullOrWhiteSpace(req.Name))
    {
        normalizedName = System.Text.RegularExpressions.Regex.Replace(
            req.Name.ToLower().Replace(" ", "-"), @"[^a-z0-9\-]", "");
        if (string.IsNullOrEmpty(normalizedName))
            return Results.BadRequest(new { error = "Invalid room name." });
        var existing = await rooms.GetByNameAsync(normalizedName);
        if (existing is not null && existing.Id != id)
            return Results.Conflict(new { error = "Channel name already exists." });
    }

    await rooms.UpdateAsync(id, normalizedName, req.Description);
    var updated = await rooms.GetByIdAsync(id);
    await hubCtx.Clients.Group(id).SendAsync("RoomUpdated",
        new { roomId = id, name = updated?.Name, description = updated?.Description });
    return Results.Ok(updated);
}).RequireAuthorization();

// Leave a private channel (caller is removed from MemberIds)
app.MapDelete("/api/rooms/{id}/members/me", async (
    string id,
    IChatRoomRepository rooms,
    ClaimsPrincipal principal) =>
{
    var room = await rooms.GetByIdAsync(id);
    if (room is null) return Results.NotFound(new { error = "Room not found." });
    if (!room.IsPrivate)
        return Results.BadRequest(new { error = "Cannot leave a public channel." });
    var callerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    if (!room.MemberIds.Contains(callerId))
        return Results.BadRequest(new { error = "You are not a member of this room." });
    await rooms.RemoveMemberAsync(id, callerId);
    return Results.NoContent();
}).RequireAuthorization();

// Get all members of a room (admin sees any; others need to be a member of private rooms)
app.MapGet("/api/rooms/{id}/members", async (
    string id,
    IChatRoomRepository rooms,
    IUserRepository users,
    ClaimsPrincipal principal) =>
{
    var room = await rooms.GetByIdAsync(id);
    if (room is null) return Results.NotFound(new { error = "Room not found." });
    var callerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var role     = principal.FindFirstValue(ClaimTypes.Role);
    if (room.IsPrivate && role != "admin" && !room.MemberIds.Contains(callerId))
        return Results.Forbid();
    var members = new List<object>();
    foreach (var uid in room.MemberIds)
    {
        var u = await users.GetByIdAsync(uid);
        if (u is not null)
            members.Add(new { id = u.Id, username = u.Username,
                displayName = u.DisplayName, avatarColor = u.AvatarColor,
                avatarFileId = u.AvatarFileId });
    }
    return Results.Ok(members);
}).RequireAuthorization();

// ═══════════════════════════════════════════════════════════════════════════════
// MESSAGES
// ═══════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/rooms/{roomId}/messages", async (
    string roomId,
    string? before,
    IChatRoomRepository rooms,
    IMessageRepository messages,
    IUserRepository users,
    IChatSettingsRepository settings,
    ClaimsPrincipal principal) =>
{
    var cfg   = await settings.GetAsync();
    var limit = Math.Clamp(cfg.HistoryLimit, 10, 200);

    // DM rooms: no Room document. Verify the caller is one of the two participants.
    if (roomId.StartsWith("dm_"))
    {
        var callerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var dmParts  = roomId[3..].Split('_');
        if (dmParts.Length != 2 || !dmParts.Contains(callerId))
            return Results.Forbid();
        var dmMsgs = await messages.GetByRoomAsync(roomId, limit, before);
        return Results.Ok(await EnrichMessages(dmMsgs, users));
    }

    var room = await rooms.GetByIdAsync(roomId);
    if (room is null) return Results.NotFound();

    // Guard private-channel history
    if (room.IsPrivate)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var role   = principal.FindFirstValue(ClaimTypes.Role);
        if (role != "admin" && !room.MemberIds.Contains(userId))
            return Results.Forbid();
    }

    var roomMsgs = await messages.GetByRoomAsync(roomId, limit, before);
    return Results.Ok(await EnrichMessages(roomMsgs, users));
}).RequireAuthorization();

// Edit a message (REST fallback — SignalR preferred path)
app.MapPut("/api/rooms/{roomId}/messages/{msgId}", async (
    string roomId,
    string msgId,
    EditMessageRequest req,
    IMessageRepository messages,
    IHubContext<ChatHub> hubCtx,
    ClaimsPrincipal principal) =>
{
    if (string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest(new { error = "Content cannot be empty." });
    if (req.Content.Length > 2000)
        return Results.BadRequest(new { error = "Message must be 2000 characters or fewer." });

    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var ok = await messages.EditAsync(msgId, userId, req.Content);
    if (!ok) return Results.Forbid();

    await hubCtx.Clients.Group(roomId).SendAsync("MessageEdited",
        new { messageId = msgId, roomId, content = req.Content, editedAt = DateTime.UtcNow });

    return Results.NoContent();
}).RequireAuthorization();

// Soft-delete a message (REST fallback)
app.MapDelete("/api/rooms/{roomId}/messages/{msgId}", async (
    string roomId,
    string msgId,
    IMessageRepository messages,
    IHubContext<ChatHub> hubCtx,
    ClaimsPrincipal principal) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var ok = await messages.SoftDeleteAsync(msgId, userId);
    if (!ok) return Results.Forbid();

    await hubCtx.Clients.Group(roomId).SendAsync("MessageDeleted",
        new { messageId = msgId, roomId });

    return Results.NoContent();
}).RequireAuthorization();

// Toggle emoji reaction
app.MapPost("/api/rooms/{roomId}/messages/{msgId}/react", async (
    string roomId,
    string msgId,
    ReactRequest req,
    IMessageRepository messages,
    IHubContext<ChatHub> hubCtx,
    ClaimsPrincipal principal) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var ok = await messages.ToggleReactionAsync(msgId, req.Emoji, userId);
    if (!ok) return Results.NotFound();

    var msg = await messages.GetByIdAsync(msgId);
    await hubCtx.Clients.Group(roomId).SendAsync("ReactionsUpdated",
        new { messageId = msgId, roomId, reactions = msg?.Reactions ?? [] });

    return Results.Ok();
}).RequireAuthorization();

// Search messages in a room by keyword (full-text)
app.MapGet("/api/rooms/{roomId}/messages/search", async (
    string roomId,
    string? q,
    int? limit,
    IChatRoomRepository rooms,
    IMessageRepository messages,
    IUserRepository users,
    ClaimsPrincipal principal) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        return Results.BadRequest(new { error = "Query must be at least 2 characters." });
    if (q.Length > 100)
        return Results.BadRequest(new { error = "Query must be 100 characters or fewer." });
    var count = Math.Clamp(limit ?? 20, 1, 50);

    if (roomId.StartsWith("dm_"))
    {
        var callerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var dmParts  = roomId[3..].Split('_');
        if (dmParts.Length != 2 || !dmParts.Contains(callerId)) return Results.Forbid();
        return Results.Ok(await EnrichMessages(await messages.SearchAsync(roomId, q, count), users));
    }
    var room = await rooms.GetByIdAsync(roomId);
    if (room is null) return Results.NotFound();
    if (room.IsPrivate)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var role   = principal.FindFirstValue(ClaimTypes.Role);
        if (role != "admin" && !room.MemberIds.Contains(userId)) return Results.Forbid();
    }
    return Results.Ok(await EnrichMessages(await messages.SearchAsync(roomId, q, count), users));
}).RequireAuthorization();

// Get all pinned messages for a room
app.MapGet("/api/rooms/{roomId}/pinned", async (
    string roomId,
    IChatRoomRepository rooms,
    IMessageRepository messages,
    IUserRepository users,
    ClaimsPrincipal principal) =>
{
    if (roomId.StartsWith("dm_")) return Results.Ok(Array.Empty<object>());
    var room = await rooms.GetByIdAsync(roomId);
    if (room is null) return Results.NotFound();
    if (room.IsPrivate)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var role   = principal.FindFirstValue(ClaimTypes.Role);
        if (role != "admin" && !room.MemberIds.Contains(userId)) return Results.Forbid();
    }
    return Results.Ok(await EnrichMessages(await messages.GetPinnedByRoomAsync(roomId), users));
}).RequireAuthorization();

// Pin or unpin a message (admin for public rooms; admin or member for private rooms)
app.MapPut("/api/rooms/{roomId}/messages/{msgId}/pin", async (
    string roomId,
    string msgId,
    PinMessageRequest req,
    IChatRoomRepository rooms,
    IMessageRepository messages,
    IHubContext<ChatHub> hubCtx,
    ClaimsPrincipal principal) =>
{
    if (roomId.StartsWith("dm_"))
        return Results.BadRequest(new { error = "Cannot pin messages in DMs." });
    var room = await rooms.GetByIdAsync(roomId);
    if (room is null) return Results.NotFound(new { error = "Room not found." });
    var callerId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var role     = principal.FindFirstValue(ClaimTypes.Role);
    if (role != "admin" && !(room.IsPrivate && room.MemberIds.Contains(callerId)))
        return Results.Forbid();
    var ok = await messages.SetPinnedAsync(msgId, req.IsPinned);
    if (!ok) return Results.NotFound(new { error = "Message not found." });
    await hubCtx.Clients.Group(roomId).SendAsync("MessagePinned",
        new { messageId = msgId, roomId, isPinned = req.IsPinned });
    return Results.NoContent();
}).RequireAuthorization();

// ═══════════════════════════════════════════════════════════════════════════════
// FILE UPLOAD / DOWNLOAD  (GridFS — messages with attachments)
// ═══════════════════════════════════════════════════════════════════════════════

app.MapPost("/api/rooms/{roomId}/upload", async (
    string roomId,
    HttpRequest request,
    IMongoDatabase db,
    IMessageRepository msgRepo,
    IUserRepository userRepo,
    IHubContext<ChatHub> hubCtx,
    ClaimsPrincipal user) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Multipart form data required." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");

    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No file provided." });

    if (file.Length > 10 * 1024 * 1024)
        return Results.BadRequest(new { error = "File must be under 10 MB." });

    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/gif", "image/webp",
          "audio/webm", "audio/ogg", "audio/mp4", "audio/wav", "audio/mpeg" };
    // Strip codec parameters (e.g. "audio/webm;codecs=opus" → "audio/webm")
    var baseContentType = file.ContentType.Split(';')[0].Trim();
    if (!allowed.Contains(baseContentType))
        return Results.BadRequest(new { error = "Only images (JPEG/PNG/GIF/WebP) and audio files are allowed." });

    // Verify the file content actually matches the claimed MIME type (magic bytes)
    using var stream = file.OpenReadStream();
    if (!HasValidMagicBytes(stream, baseContentType))
        return Results.BadRequest(new { error = "File content does not match its declared type." });

    var bucket = new GridFSBucket(db);
    var uploadOpts = new GridFSUploadOptions
    {
        Metadata = new BsonDocument { { "contentType", file.ContentType }, { "roomId", roomId } }
    };

    ObjectId fileObjId;
    fileObjId = await bucket.UploadFromStreamAsync(file.FileName, stream, uploadOpts);

    var senderId   = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    var senderName = user.FindFirstValue(ClaimTypes.Name) ?? "";

    // Fetch full user record so the broadcast includes display name, avatar colour and file
    var senderUser = await userRepo.GetByIdAsync(senderId);

    var msg = new Message
    {
        RoomId            = roomId,
        SenderId          = senderId,
        SenderUsername    = senderName,
        SenderDisplayName = senderUser?.DisplayName ?? string.Empty,
        SenderAvatarColor = senderUser?.AvatarColor ?? string.Empty,
        SenderAvatarFileId= senderUser?.AvatarFileId,
        Content           = "",
        AttachmentId   = fileObjId.ToString(),
        AttachmentName = file.FileName,
        AttachmentType = file.ContentType,
        SentAt         = DateTime.UtcNow
    };
    await msgRepo.CreateAsync(msg);

    await hubCtx.Clients.Group(roomId).SendAsync("ReceiveMessage", new
    {
        id             = msg.Id,
        roomId         = msg.RoomId,
        senderId       = msg.SenderId,
        senderUsername = msg.SenderUsername,
        senderDisplayName = msg.SenderDisplayName,
        senderAvatarColor = msg.SenderAvatarColor,
        senderAvatarFileId = msg.SenderAvatarFileId,
        content        = msg.Content,
        deleted        = msg.Deleted,
        editedAt       = msg.EditedAt,
        reactions      = msg.Reactions,
        attachmentId   = msg.AttachmentId,
        attachmentName = msg.AttachmentName,
        attachmentType = msg.AttachmentType,
        sentAt         = msg.SentAt
    });

    return Results.Ok(new { fileId = fileObjId.ToString(), messageId = msg.Id });
}).RequireAuthorization().RequireRateLimiting("messages");

app.MapGet("/api/files/{fileId}", async (string fileId, IMongoDatabase db) =>
{
    if (!ObjectId.TryParse(fileId, out var objId))
        return Results.BadRequest(new { error = "Invalid file ID." });

    var bucket = new GridFSBucket(db);
    try
    {
        var cursor = await bucket.FindAsync(
            Builders<GridFSFileInfo>.Filter.Eq(x => x.Id, objId));
        var info = await cursor.FirstOrDefaultAsync();
        if (info is null) return Results.NotFound();

        var contentType = "application/octet-stream";
        if (info.Metadata != null && info.Metadata.Contains("contentType"))
            contentType = info.Metadata["contentType"].AsString;

        var downloadStream = await bucket.OpenDownloadStreamAsync(objId);

        // Sanitise the filename for the Content-Disposition header
        var rawName = info.Filename ?? "download";
        var safeName = System.Text.RegularExpressions.Regex.Replace(rawName, @"[^\w.\-]", "_");
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "download";

        // Only allow known-safe types to render inline; everything else must download,
        // preventing uploaded HTML/SVG from executing in the app's origin context.
        var inlineTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/png", "image/gif", "image/webp",
            "audio/webm", "audio/ogg", "audio/mp4", "audio/wav", "audio/mpeg"
        };
        var baseType = contentType.Split(';')[0].Trim();
        if (inlineTypes.Contains(baseType))
        {
            // Let the browser display images/audio inline (needed for chat rendering)
            return Results.Stream(downloadStream, contentType);
        }
        // Force download for any unrecognised or non-media type
        return Results.Stream(downloadStream, contentType, safeName,
            enableRangeProcessing: false);
    }
    catch (GridFSFileNotFoundException)
    {
        return Results.NotFound();
    }
}).RequireAuthorization();

// ── Push Notification Endpoints ───────────────────────────────────────────────

// Return the VAPID public key so the browser can subscribe
app.MapGet("/api/push/vapid-public-key", (IConfiguration cfg) =>
    Results.Ok(new { publicKey = cfg["Push:VapidPublicKey"] ?? "" }));

// Save a push subscription for the calling user
app.MapPost("/api/push/subscribe", async (
    PushSubscribeRequest req,
    IPushSubscriptionRepository pushRepo,
    ClaimsPrincipal principal) =>
{
    if (string.IsNullOrWhiteSpace(req.Endpoint) ||
        string.IsNullOrWhiteSpace(req.P256dh)   ||
        string.IsNullOrWhiteSpace(req.Auth))
        return Results.BadRequest();

    // Validate that the push endpoint is a legitimate HTTPS push-service URL
    if (!Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var pushUri)
        || pushUri.Scheme != "https"
        || !(pushUri.Host.EndsWith(".push.services.mozilla.com")
          || pushUri.Host.EndsWith(".notify.windows.com")
          || pushUri.Host.EndsWith(".googleapis.com")
          || pushUri.Host.EndsWith(".push.apple.com")))
        return Results.BadRequest(new { error = "Push endpoint must be a known HTTPS push service." });

    var uid = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    await pushRepo.UpsertAsync(new ChatApp.Models.UserPushSubscription
    {
        UserId  = uid,
        Endpoint = req.Endpoint,
        P256dh   = req.P256dh,
        Auth     = req.Auth,
        CreatedAt = DateTime.UtcNow
    });
    return Results.NoContent();
}).RequireAuthorization();

// Remove a push subscription (user unsubscribed)
app.MapDelete("/api/push/subscribe", async (
    string endpoint,
    IPushSubscriptionRepository pushRepo,
    ClaimsPrincipal principal) =>
{
    var uid = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    await pushRepo.DeleteByUserAsync(uid, endpoint);   // scoped to calling user
    return Results.NoContent();
}).RequireAuthorization();

// ── Health ─────────────────────────────────────────────────────────────────────
app.MapGet("/health", () =>
    Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// ── OG / Link preview (server-side fetch to avoid CORS) ───────────────────────
app.MapGet("/api/og", async (string url, IHttpClientFactory http) =>
{
    // Only allow http/https URLs
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "http" && uri.Scheme != "https"))
        return Results.BadRequest();

    // Resolve DNS once, validate all addresses, then pin one for the actual request.
    // This prevents DNS-rebinding attacks where the hostname resolves to a private
    // IP between our check and the HttpClient's own resolution.
    System.Net.IPAddress? pinnedAddress = null;
    try
    {
        var host = uri.DnsSafeHost;
        var addresses = await System.Net.Dns.GetHostAddressesAsync(host);
        foreach (var addr in addresses)
        {
            var bytes = addr.GetAddressBytes();
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (bytes[0] == 0   ||   // 0.0.0.0/8 — non-routable / INADDR_ANY
                    bytes[0] == 127 ||   // 127.0.0.0/8 — loopback
                    bytes[0] == 10  ||   // 10.0.0.0/8  — private
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || // 172.16/12
                    (bytes[0] == 192 && bytes[1] == 168) ||  // 192.168.0.0/16
                    bytes[0] == 169)     // 169.254.0.0/16 — link-local
                    return Results.BadRequest();
            }
            else if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (System.Net.IPAddress.IsLoopback(addr)) return Results.BadRequest();
                var b6 = addr.GetAddressBytes();
                // fe80::/10 — IPv6 link-local
                if ((b6[0] == 0xfe && (b6[1] & 0xc0) == 0x80)) return Results.BadRequest();
                // fc00::/7 — IPv6 unique local (ULA)
                if (b6[0] == 0xfc || b6[0] == 0xfd) return Results.BadRequest();
            }
        }
        // All addresses passed — pick the first one to pin the connection to
        pinnedAddress = addresses[0];
    }
    catch { return Results.BadRequest(); }

    try
    {
        // Build an HttpClient that connects only to the validated IP, preventing DNS rebinding
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new System.Net.Sockets.Socket(
                    pinnedAddress!.AddressFamily,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new System.Net.IPEndPoint(pinnedAddress, uri.Port), cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("User-Agent", "ChatApp-Preview/1.0");
        // Preserve the original Host header so the target server routes correctly
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var ct = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!ct.StartsWith("text/html")) return Results.Ok(new { });

        // Limit HTML to first 64 KB to prevent ReDoS on giant pages
        var html = await response.Content.ReadAsStringAsync();
        if (html.Length > 65_536) html = html[..65_536];

        // Use bounded quantifiers ({0,400}) instead of unbounded [^>]* to prevent
        // catastrophic backtracking (ReDoS) on malicious / deeply-nested HTML.
        // Also apply a 2-second timeout per regex match as a safety net.
        static string? Meta(string h, string prop, string attr) {
            var opts = System.Text.RegularExpressions.RegexOptions.IgnoreCase
                     | System.Text.RegularExpressions.RegexOptions.Singleline;
            var timeout = TimeSpan.FromSeconds(2);
            try {
                var pat = $@"<meta\s[^>]{{0,400}}{attr}=[""']{prop}[""'][^>]{{0,400}}content=[""']([^""']{{1,500}})[""']";
                var m = System.Text.RegularExpressions.Regex.Match(h, pat, opts, timeout);
                if (m.Success) return m.Groups[1].Value;
                // reversed attribute order
                var pat2 = $@"<meta\s[^>]{{0,400}}content=[""']([^""']{{1,500}})[""'][^>]{{0,400}}{attr}=[""']{prop}[""']";
                m = System.Text.RegularExpressions.Regex.Match(h, pat2, opts, timeout);
                return m.Success ? m.Groups[1].Value : null;
            } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) {
                return null; // abort gracefully if regex takes too long
            }
        }

        var title = Meta(html, "og:title", "property")
                 ?? Meta(html, "title", "name")
                 ?? (System.Text.RegularExpressions.Regex.Match(html,
                        @"<title[^>]{0,100}>([^<]{1,200})</title>",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(2)) is var tm && tm.Success
                            ? tm.Groups[1].Value.Trim() : null);

        var description = Meta(html, "og:description", "property")
                       ?? Meta(html, "description", "name");

        var image = Meta(html, "og:image", "property");

        if (string.IsNullOrWhiteSpace(title)) return Results.Ok(new { });

        return Results.Ok(new
        {
            title  = title?.Length > 120 ? title[..120] : title,
            description = description?.Length > 200 ? description[..200] : description,
            image,
            siteName = uri.Host
        });
    }
    catch { return Results.Ok(new { }); }
}).RequireAuthorization();

app.Run();

// ── Local helper: enrich messages with current sender avatar/display info ────────
// Fills in SenderAvatarFileId, SenderAvatarColor, and SenderDisplayName from the
// User collection so that old messages (stored before avatar upload) render correctly.
static async Task<List<object>> EnrichMessages(List<Message> msgs, IUserRepository users)
{
    // Build a lookup of unique sender IDs to avoid repeated DB calls
    var senderIds = msgs.Select(m => m.SenderId).Where(id => id != null).Distinct().ToList();
    var userLookup = new Dictionary<string, User>();
    foreach (var id in senderIds)
    {
        var u = await users.GetByIdAsync(id);
        if (u is not null) userLookup[id] = u;
    }

    return msgs.Select(m =>
    {
        var sender = m.SenderId is not null && userLookup.TryGetValue(m.SenderId, out var u) ? u : null;
        return (object)new
        {
            id                 = m.Id,
            roomId             = m.RoomId,
            senderId           = m.SenderId,
            senderUsername     = m.SenderUsername,
            senderDisplayName  = !string.IsNullOrEmpty(m.SenderDisplayName)
                                    ? m.SenderDisplayName
                                    : sender?.DisplayName ?? m.SenderUsername,
            senderAvatarColor  = !string.IsNullOrEmpty(m.SenderAvatarColor)
                                    ? m.SenderAvatarColor
                                    : sender?.AvatarColor ?? "",
            senderAvatarFileId = m.SenderAvatarFileId ?? sender?.AvatarFileId,
            content            = m.Content,
            deleted            = m.Deleted,
            editedAt           = m.EditedAt,
            reactions          = m.Reactions,
            attachmentId       = m.AttachmentId,
            attachmentName     = m.AttachmentName,
            attachmentType     = m.AttachmentType,
            sentAt             = m.SentAt,
            isPinned           = m.IsPinned
        };
    }).ToList();
}

// ── Local helper: validate a short-lived temp token (totp_pending / totp_setup) ─
ClaimsPrincipal? ValidateTempToken(string token, string requiredRole, IConfiguration cfg)
{
    var handler   = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
    var valParams = new TokenValidationParameters
    {
        ValidateIssuer           = true,
        ValidateAudience         = true,
        ValidateLifetime         = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer              = cfg["Jwt:Issuer"],
        ValidAudience            = cfg["Jwt:Audience"],
        IssuerSigningKey         = new SymmetricSecurityKey(
                                       Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!))
    };
    try
    {
        var principal = handler.ValidateToken(token, valParams, out _);
        return principal.FindFirstValue(ClaimTypes.Role) == requiredRole ? principal : null;
    }
    catch { return null; }
}
