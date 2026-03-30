using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using ChatApp.Models;
using ChatApp.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using WebPush;

namespace ChatApp.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly IMessageRepository _messages;
    private readonly IUserRepository _users;
    private readonly IPushSubscriptionRepository _pushRepo;
    private readonly IConfiguration _config;
    private readonly IChatRoomRepository _rooms;

    // connectionId → (userId, username)
    private static readonly ConcurrentDictionary<string, (string UserId, string Username)>
        _online = new();

    // connectionId → currently-viewed roomId (null if no room open)
    private static readonly ConcurrentDictionary<string, string>
        _activeRoom = new();

    // ── Per-user rate limiting for hub methods ──────────────────────────────
    // userId → (windowStart, count)
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)>
        _messageRateLimit = new();
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)>
        _editRateLimit = new();
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)>
        _reactionRateLimit = new();
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)>
        _typingRateLimit = new();

    private const int MaxMessagesPerWindow   = 5;   // 5 messages per second
    private const int MaxEditsPerWindow      = 3;   // 3 edits per second
    private const int MaxReactionsPerWindow  = 5;   // 5 reactions per second
    private const int MaxTypingPerWindow     = 2;   // 2 typing signals per second
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Check if a user exceeds the rate limit for a given action bucket.
    /// Returns true if the action is allowed, false if it should be rejected.
    /// </summary>
    private static bool CheckRateLimit(
        ConcurrentDictionary<string, (DateTime WindowStart, int Count)> bucket,
        string userId, int maxPerWindow)
    {
        var now = DateTime.UtcNow;
        var entry = bucket.GetOrAdd(userId, _ => (now, 0));

        // If the window has expired, start a new one
        if (now - entry.WindowStart > RateWindow)
            entry = (now, 0);

        if (entry.Count >= maxPerWindow)
            return false; // rate limit exceeded

        bucket[userId] = (entry.WindowStart, entry.Count + 1);
        return true;
    }

    public ChatHub(IMessageRepository messages, IUserRepository users, IPushSubscriptionRepository pushRepo, IConfiguration config, IChatRoomRepository rooms)
    {
        _messages = messages;
        _users     = users;
        _pushRepo  = pushRepo;
        _config    = config;
        _rooms     = rooms;
    }

    // ── Periodic cleanup of stale entries ───────────────────────────────────
    // Removes expired rate-limit entries and orphaned connection tracking entries
    // every 5 minutes to prevent unbounded memory growth.
    // Track connection timestamps for orphan detection
    private static readonly ConcurrentDictionary<string, DateTime> _connectionTime = new();

    private static readonly Timer _cleanupTimer = new(_ =>
    {
        var rateCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(1);

        // Purge rate-limit entries older than 1 minute (windows are 1 second,
        // so anything older is guaranteed stale)
        PurgeStaleEntries(_messageRateLimit, rateCutoff);
        PurgeStaleEntries(_editRateLimit, rateCutoff);
        PurgeStaleEntries(_reactionRateLimit, rateCutoff);
        PurgeStaleEntries(_typingRateLimit, rateCutoff);

        // Purge orphaned connection entries — if a connection has been tracked
        // for over 24 hours without a disconnect, it's almost certainly stale.
        // (Normal connections are cleaned up in OnDisconnectedAsync.)
        var connCutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        foreach (var key in _connectionTime.Keys)
        {
            if (_connectionTime.TryGetValue(key, out var connectedAt) && connectedAt < connCutoff)
            {
                _online.TryRemove(key, out (string, string) _discard1);
                _activeRoom.TryRemove(key, out string? _discard2);
                _connectionTime.TryRemove(key, out DateTime _discard3);
            }
        }
    }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

    private static void PurgeStaleEntries(
        ConcurrentDictionary<string, (DateTime WindowStart, int Count)> dict,
        DateTime cutoff)
    {
        foreach (var key in dict.Keys)
        {
            if (dict.TryGetValue(key, out var entry) && entry.WindowStart < cutoff)
                dict.TryRemove(key, out _);
        }
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        var username = GetUsername();
        _online[Context.ConnectionId] = (userId, username);
        _connectionTime[Context.ConnectionId] = DateTime.UtcNow;

        // Tell everyone this user is online
        await Clients.All.SendAsync("UserOnline", userId, username);
        // Send the new connection the full online-user list
        await Clients.Caller.SendAsync("OnlineUsers",
            _online.Values
                   .GroupBy(v => v.UserId)
                   .Select(g => new { userId = g.Key, username = g.First().Username })
                   .ToList());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connectionTime.TryRemove(Context.ConnectionId, out _);
        _activeRoom.TryRemove(Context.ConnectionId, out _);
        if (_online.TryRemove(Context.ConnectionId, out var info))
        {
            // Only broadcast offline if the user has no other active connections
            bool stillOnline = _online.Values.Any(v => v.UserId == info.UserId);
            if (!stillOnline)
                await Clients.All.SendAsync("UserOffline", info.UserId, info.Username);
        }
        await base.OnDisconnectedAsync(exception);
    }

    // ── Track which room the client is currently viewing ──────────────────
    public Task SetActiveRoom(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            _activeRoom.TryRemove(Context.ConnectionId, out _);
        else
            _activeRoom[Context.ConnectionId] = roomId;
        return Task.CompletedTask;
    }

    // ── Join a chat room (SignalR group) ──────────────────────────────────────
    public async Task JoinRoom(string roomId)
    {
        var userId = GetUserId();

        // DM rooms: caller must be one of the two participants.
        if (roomId.StartsWith("dm_"))
        {
            var parts = roomId[3..].Split('_');
            if (parts.Length != 2 || !parts.Contains(userId))
                return;
        }
        else
        {
            // Channel rooms: reject if private and caller is not a member.
            var room = await _rooms.GetByIdAsync(roomId);
            if (room is null) return;
            if (room.IsPrivate)
            {
                var role = Context.User?.FindFirstValue(ClaimTypes.Role);
                if (role != "admin" && !room.MemberIds.Contains(userId))
                    return;
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("UserJoined", GetUsername(), roomId);
    }
    // ── Start or re-join a DM thread ─────────────────────────────────────────
    // Both participants are added to the shared SignalR group so messages
    // are delivered in real-time even if the recipient hasn’t opened the DM yet.
    public async Task JoinDm(string otherUserId)
    {
        var myUserId = GetUserId();
        var sorted   = new[] { myUserId, otherUserId }.OrderBy(x => x).ToArray();
        var dmRoomId = "dm_" + sorted[0] + "_" + sorted[1];

        // Add the caller to the group
        await Groups.AddToGroupAsync(Context.ConnectionId, dmRoomId);

        // Add all active connections of the other user and notify them
        var otherConns = _online
            .Where(kvp => kvp.Value.UserId == otherUserId)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var connId in otherConns)
        {
            await Groups.AddToGroupAsync(connId, dmRoomId);
            // Tell the recipient so their client joins the group too
            await Clients.Client(connId).SendAsync("DmInvite", dmRoomId);
        }
    }
    // ── Leave a chat room ─────────────────────────────────────────────────────
    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("UserLeft", GetUsername(), roomId);
    }

    // ── Typing indicator (fire-and-forget, no persistence) ───────────────────
    public Task SendTyping(string roomId)
    {
        if (!CheckRateLimit(_typingRateLimit, GetUserId(), MaxTypingPerWindow))
            return Task.CompletedTask; // silently drop excessive typing signals
        return Clients.OthersInGroup(roomId).SendAsync("UserTyping", GetUsername(), roomId);
    }

    // ── Persist + broadcast a message to the room ─────────────────────────────
    public async Task SendMessage(string roomId, string content, string? clientMsgId = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        if (content.Length > 2000) return;  // server-side length guard

        var userId = GetUserId();

        // Per-user rate limit: max 5 messages per second
        if (!CheckRateLimit(_messageRateLimit, userId, MaxMessagesPerWindow))
        {
            await Clients.Caller.SendAsync("RateLimited", "You are sending messages too quickly.");
            return;
        }

        // Access control: verify caller may post to this room.
        if (roomId.StartsWith("dm_"))
        {
            var parts = roomId[3..].Split('_');
            if (parts.Length != 2 || !parts.Contains(userId)) return;
        }
        else
        {
            var room = await _rooms.GetByIdAsync(roomId);
            if (room is null) return;
            if (room.IsPrivate)
            {
                var role = Context.User?.FindFirstValue(ClaimTypes.Role);
                if (role != "admin" && !room.MemberIds.Contains(userId)) return;
            }
        }

        // Fetch full user record so the message includes display name, avatar colour & file
        var senderUser = await _users.GetByIdAsync(userId);

        var msg = new Message
        {
            RoomId = roomId,
            SenderId = userId,
            SenderUsername = GetUsername(),
            SenderDisplayName = senderUser?.DisplayName ?? string.Empty,
            SenderAvatarColor = senderUser?.AvatarColor ?? string.Empty,
            SenderAvatarFileId = senderUser?.AvatarFileId,
            Content = content,
            SentAt = DateTime.UtcNow
        };

        await _messages.CreateAsync(msg);

        await Clients.Group(roomId).SendAsync("ReceiveMessage", new
        {
            id = msg.Id,
            roomId = msg.RoomId,
            senderId = msg.SenderId,
            senderUsername = msg.SenderUsername,
            senderDisplayName = msg.SenderDisplayName,
            senderAvatarColor = msg.SenderAvatarColor,
            senderAvatarFileId = msg.SenderAvatarFileId,
            content = msg.Content,
            deleted = msg.Deleted,
            editedAt = msg.EditedAt,
            reactions = msg.Reactions,
            attachmentId = msg.AttachmentId,
            attachmentName = msg.AttachmentName,
            attachmentType = msg.AttachmentType,
            sentAt = msg.SentAt,
            clientMsgId  // echoed back so sender can confirm optimistic UI
        });

        // Fire push notifications to users who are offline or not in this room
        _ = Task.Run(() => SendPushNotificationsAsync(msg));
    }

    private async Task SendPushNotificationsAsync(Message msg)
    {
        try
        {
            var vapidPublicKey  = _config["Push:VapidPublicKey"];
            var vapidPrivateKey = _config["Push:VapidPrivateKey"];
            var vapidSubject    = _config["Push:VapidSubject"] ?? "mailto:admin@example.com";

            if (string.IsNullOrWhiteSpace(vapidPublicKey) ||
                vapidPublicKey.StartsWith("CHANGE_ME") ||
                string.IsNullOrWhiteSpace(vapidPrivateKey) ||
                vapidPrivateKey.StartsWith("CHANGE_ME"))
                return; // VAPID not configured yet

            // Users currently viewing the same room — they already see the message live
            var usersViewingRoom = _activeRoom
                .Where(kvp => kvp.Value == msg.RoomId)
                .Select(kvp => _online.TryGetValue(kvp.Key, out var info) ? info.UserId : null)
                .Where(uid => uid != null)
                .ToHashSet();

            var allSubs  = await _pushRepo.GetAllAsync();
            var toNotify = allSubs
                .Where(s => s.UserId != msg.SenderId && !usersViewingRoom.Contains(s.UserId))
                .ToList();

            if (toNotify.Count == 0) return;

            var payload = JsonSerializer.Serialize(new
            {
                title  = msg.SenderDisplayName ?? msg.SenderUsername,
                body   = msg.Content.Length > 100 ? msg.Content[..100] + "…" : msg.Content,
                roomId = msg.RoomId,
                tag    = $"room-{msg.RoomId}"
            });

            var vapidDetails = new VapidDetails(vapidSubject, vapidPublicKey, vapidPrivateKey);
            var client       = new WebPushClient();

            var tasks = toNotify.Select(async sub =>
            {
                try
                {
                    var pushSub = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                    await client.SendNotificationAsync(pushSub, payload, vapidDetails);
                }
                catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone ||
                                                   ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Subscription expired — clean it up
                    await _pushRepo.DeleteAsync(sub.Endpoint);
                }
                catch { /* ignore transient errors */ }
            });
            await Task.WhenAll(tasks);
        }
        catch { /* never crash the hub */ }
    }

    // ── Edit a message (only the original sender) ────────────────────────────
    public async Task EditMessage(string roomId, string messageId, string newContent)
    {
        if (string.IsNullOrWhiteSpace(newContent) || newContent.Length > 2000) return;
        var userId = GetUserId();

        if (!CheckRateLimit(_editRateLimit, userId, MaxEditsPerWindow))
        {
            await Clients.Caller.SendAsync("RateLimited", "You are editing messages too quickly.");
            return;
        }
        // Verify the message belongs to the claimed room to prevent spurious broadcasts.
        var existing = await _messages.GetByIdAsync(messageId);
        if (existing is null || existing.RoomId != roomId) return;
        var ok = await _messages.EditAsync(messageId, userId, newContent);
        if (ok)
            await Clients.Group(roomId).SendAsync("MessageEdited",
                new { messageId, roomId, content = newContent, editedAt = DateTime.UtcNow });
    }

    // ── Soft-delete a message (only the original sender) ─────────────────────
    public async Task DeleteMessage(string roomId, string messageId)
    {
        var userId = GetUserId();
        var existing = await _messages.GetByIdAsync(messageId);
        if (existing is null || existing.RoomId != roomId) return;
        var ok = await _messages.SoftDeleteAsync(messageId, userId);
        if (ok)
            await Clients.Group(roomId).SendAsync("MessageDeleted",
                new { messageId, roomId });
    }

    // ── Toggle an emoji reaction ──────────────────────────────────────────────
    public async Task ToggleReaction(string roomId, string messageId, string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 10) return;
        var userId = GetUserId();

        if (!CheckRateLimit(_reactionRateLimit, userId, MaxReactionsPerWindow))
        {
            await Clients.Caller.SendAsync("RateLimited", "You are reacting too quickly.");
            return;
        }
        var ok = await _messages.ToggleReactionAsync(messageId, emoji, userId);
        if (ok)
        {
            var msg = await _messages.GetByIdAsync(messageId);
            await Clients.Group(roomId).SendAsync("ReactionsUpdated",
                new { messageId, roomId, reactions = msg?.Reactions ?? [] });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private string GetUserId() =>
        Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private string GetUsername() =>
        Context.User?.FindFirstValue(ClaimTypes.Name) ?? "Anonymous";
}
