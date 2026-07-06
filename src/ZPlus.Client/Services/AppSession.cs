using ZPlus.Shared.Dtos;

namespace ZPlus.Client.Services;

/// <summary>Holds the signed-in user's session for the lifetime of the process.</summary>
public class AppSession
{
    public static AppSession Current { get; } = new();

    public string ServerUrl { get; set; } = "http://localhost:5199";
    public string? Token { get; set; }
    public UserDto? User { get; set; }

    public bool IsSignedIn => Token is not null && User is not null;
}
