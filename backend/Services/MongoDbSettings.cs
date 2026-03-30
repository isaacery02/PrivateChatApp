namespace ChatApp.Services;

public class MongoDbSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 27017;
    public string Database { get; set; } = "chat_db";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string AuthSource { get; set; } = "admin";
}
