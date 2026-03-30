// MongoDB initialisation script
// Runs once on first container start as the root admin user.
// Creates app_user in my_app_db (authSource) with readWrite on chat_db,
// then seeds a default #general channel.

// ── Create application user ──────────────────────────────────────────────────
db = db.getSiblingDB("my_app_db");

// app_user password is injected at runtime via MONGO_APP_PASSWORD env var
db.createUser({
  user: "app_user",
  pwd:  process.env.MONGO_APP_PASSWORD || "CHANGE_ME",
  roles: [
    { role: "readWrite", db: "chat_db"   },
    { role: "dbOwner",   db: "my_app_db" }
  ]
});

// ── Seed default chat room ────────────────────────────────────────────────────
db = db.getSiblingDB("chat_db");

db.chat_rooms.insertOne({
  name:        "general",
  description: "General discussion for everyone",
  createdAt:   new Date()
});

db.chat_rooms.insertOne({
  name:        "random",
  description: "Off-topic conversations",
  createdAt:   new Date()
});

print("MongoDB initialisation complete.");
