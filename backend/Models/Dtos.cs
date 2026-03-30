namespace ChatApp.Models;

// Admin-only: create a new user account
public record CreateUserRequest(string Username, string Password);
public record LoginRequest(string Username, string Password);
public record CreateRoomRequest(string Name, string Description, bool IsPrivate = false);
public record EditMessageRequest(string Content);
public record InviteUserRequest(string UserId);
public record ReactRequest(string Emoji);
public record UpdateProfileRequest(string? DisplayName, string? AvatarColor);
public record UpdateSettingsRequest(bool PurgeEnabled, int PurgeAfterDays, int HistoryLimit = 50);
public record SetUserDisabledRequest(bool Disabled);
public record PushSubscribeRequest(string Endpoint, string P256dh, string Auth);
public record VerifyTotpRequest(string TempToken, string Code);
public record ConfirmTotpRequest(string Code);
public record DisableTotpRequest(string Password);
public record SetupFirstTotpRequest(string TempToken);
public record ConfirmFirstTotpRequest(string TempToken, string Code);
public record PinMessageRequest(bool IsPinned);
public record UpdateRoomRequest(string? Name, string? Description);
