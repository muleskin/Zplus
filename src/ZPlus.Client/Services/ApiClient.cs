using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.Services;

public class ApiException(string message) : Exception(message);

/// <summary>Typed wrapper over the ZPlus REST API.</summary>
public class ApiClient
{
    private static readonly HttpClient Http = new();

    private readonly AppSession _session = AppSession.Current;

    public Task<AuthResponse> RegisterAsync(RegisterRequest request) =>
        PostAsync<RegisterRequest, AuthResponse>("api/auth/register", request, authorized: false);

    public Task<AuthResponse> LoginAsync(LoginRequest request) =>
        PostAsync<LoginRequest, AuthResponse>("api/auth/login", request, authorized: false);

    public Task<CreateMeetingResponse> CreateMeetingAsync(CreateMeetingRequest request) =>
        PostAsync<CreateMeetingRequest, CreateMeetingResponse>("api/meetings", request);

    public Task<MeetingDto> LookupMeetingAsync(JoinLookupRequest request) =>
        PostAsync<JoinLookupRequest, MeetingDto>("api/meetings/lookup", request);

    public async Task<List<MeetingDto>> GetMyMeetingsAsync()
    {
        using var request = NewRequest(HttpMethod.Get, "api/meetings/mine", authorized: true);
        using var response = await Http.SendAsync(request);
        await ThrowIfFailedAsync(response);
        return (await response.Content.ReadFromJsonAsync<List<MeetingDto>>())!;
    }

    /// <summary>Uploads a file for a meeting and returns its stored id + metadata.</summary>
    public async Task<FileUploadResponse> UploadFileAsync(Guid meetingId, string filePath)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(meetingId.ToString()), "meetingId");
        var bytes = await File.ReadAllBytesAsync(filePath);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var request = NewRequest(HttpMethod.Post, "api/files", authorized: true);
        request.Content = content;
        using var response = await Http.SendAsync(request);
        await ThrowIfFailedAsync(response);
        return (await response.Content.ReadFromJsonAsync<FileUploadResponse>())!;
    }

    /// <summary>Downloads a shared file (server-relative path) to the given local path.</summary>
    public async Task DownloadFileAsync(string downloadPath, string destinationPath)
    {
        using var request = NewRequest(HttpMethod.Get, downloadPath.TrimStart('/'), authorized: true);
        using var response = await Http.SendAsync(request);
        await ThrowIfFailedAsync(response);
        await using var fs = File.Create(destinationPath);
        await response.Content.CopyToAsync(fs);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path, TRequest body, bool authorized = true)
    {
        using var request = NewRequest(HttpMethod.Post, path, authorized);
        request.Content = JsonContent.Create(body);
        using var response = await Http.SendAsync(request);
        await ThrowIfFailedAsync(response);
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, bool authorized)
    {
        var request = new HttpRequestMessage(method, $"{_session.ServerUrl.TrimEnd('/')}/{path}");
        if (authorized && _session.Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token);
        return request;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new ApiException(await FriendlyErrorAsync(response));
    }

    /// <summary>
    /// Turns a failed HTTP response into a short message. Z+ returns brief text; anything else
    /// (an HTML error page, a proxy/portal, the wrong service) means the Server address isn't a
    /// Z+ server — so we say that instead of dumping the whole page into the UI.
    /// </summary>
    internal static async Task<string> FriendlyErrorAsync(HttpResponseMessage response)
    {
        var detail = (await response.Content.ReadAsStringAsync()).Trim();
        var code = (int)response.StatusCode;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        bool looksHtml = mediaType == "text/html"
            || detail.StartsWith("<", StringComparison.Ordinal)
            || detail.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("<!doctype", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(detail))
            return $"Request failed (HTTP {code}).";
        if (looksHtml || detail.Length > 300)
            return $"The server returned an unexpected response (HTTP {code}). Check that the " +
                   "Server address points directly at your Z+ server, e.g. http://your-host:5199.";
        return detail.Trim('"');
    }
}
