using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ZPlus.Shared.Dtos;

namespace ZPlus.Admin.Services;

public class ApiException(string message) : Exception(message);

/// <summary>Typed client for the ZPlus admin REST API.</summary>
public class AdminApiClient
{
    private static readonly HttpClient Http = new();

    public string ServerUrl { get; set; } = "http://localhost:5199";
    public string? Token { get; private set; }
    public UserDto? SignedInUser { get; private set; }

    public async Task SignInAsync(string email, string password)
    {
        var auth = await PostAsync<LoginRequest, AuthResponse>(
            "api/auth/login", new LoginRequest(email, password), authorized: false);
        if (auth.User.Role != Roles.Admin && auth.User.Role != Roles.SuperAdmin)
            throw new ApiException("This account does not have administrator rights.");
        Token = auth.Token;
        SignedInUser = auth.User;
    }

    public Task<List<AdminUserDto>> GetUsersAsync() => GetAsync<List<AdminUserDto>>("api/admin/users");

    public Task<AdminUserDto> CreateUserAsync(AdminCreateUserRequest request) =>
        PostAsync<AdminCreateUserRequest, AdminUserDto>("api/admin/users", request);

    public Task<AdminUserDto> UpdateUserAsync(Guid id, AdminUpdateUserRequest request) =>
        SendAsync<AdminUpdateUserRequest, AdminUserDto>(HttpMethod.Put, $"api/admin/users/{id}", request);

    public Task ResetPasswordAsync(Guid id, string newPassword) =>
        SendAsync<AdminResetPasswordRequest, object?>(
            HttpMethod.Post, $"api/admin/users/{id}/reset-password", new AdminResetPasswordRequest(newPassword),
            expectBody: false);

    public Task<ServerSettingsDto> GetSettingsAsync() => GetAsync<ServerSettingsDto>("api/admin/settings");

    public Task<ServerSettingsDto> SaveSettingsAsync(ServerSettingsDto settings) =>
        SendAsync<ServerSettingsDto, ServerSettingsDto>(HttpMethod.Put, "api/admin/settings", settings);

    public Task SendTestEmailAsync(ServerSettingsDto settings, string recipient) =>
        SendAsync<SmtpTestRequest, object?>(HttpMethod.Post, "api/admin/settings/test-email",
            new SmtpTestRequest(settings, recipient), expectBody: false);

    public Task<List<ActiveMeetingDto>> GetActiveMeetingsAsync() =>
        GetAsync<List<ActiveMeetingDto>>("api/admin/meetings/active");

    public Task ForceEndMeetingAsync(Guid id) =>
        SendAsync<object, object?>(HttpMethod.Post, $"api/admin/meetings/{id}/end", new { }, expectBody: false);

    // ---- plumbing ----------------------------------------------------------

    private async Task<T> GetAsync<T>(string path)
    {
        using var request = NewRequest(HttpMethod.Get, path, authorized: true);
        using var response = await Http.SendAsync(request);
        await ThrowIfFailedAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, bool authorized = true) =>
        SendAsync<TRequest, TResponse>(HttpMethod.Post, path, body, authorized: authorized);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method, string path, TRequest body, bool authorized = true, bool expectBody = true)
    {
        using var request = NewRequest(method, path, authorized);
        request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request);
        await ThrowIfFailedAsync(response);
        if (!expectBody) return default!;
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, bool authorized)
    {
        var request = new HttpRequestMessage(method, $"{ServerUrl.TrimEnd('/')}/{path}");
        if (authorized && Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync();
        throw new ApiException(string.IsNullOrWhiteSpace(detail)
            ? $"Request failed ({(int)response.StatusCode})."
            : detail.Trim('"'));
    }
}
