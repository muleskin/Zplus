using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZPlus.Server.Data;
using ZPlus.Server.Models;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Controllers;

/// <summary>
/// Storage for files shared into meetings. Upload returns a file id the client then
/// announces to the meeting over the hub (<c>ShareFile</c>); download streams the bytes.
/// </summary>
[ApiController]
[Route("api/files")]
[Authorize]
public class FilesController(AppDbContext db) : ControllerBase
{
    /// <summary>Maximum accepted upload size (25 MB) — files live in the SQLite database.</summary>
    private const long MaxBytes = 25 * 1024 * 1024;

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentDisplayName => User.FindFirstValue(ClaimTypes.Name) ?? "Unknown";

    [HttpPost]
    [RequestSizeLimit(MaxBytes + 4096)]
    public async Task<ActionResult<FileUploadResponse>> Upload([FromForm] Guid meetingId, IFormFile? file)
    {
        if (file is null || file.Length == 0) return BadRequest("No file was uploaded.");
        if (file.Length > MaxBytes) return BadRequest($"File too large (limit {MaxBytes / (1024 * 1024)} MB).");

        var meeting = await db.Meetings.FindAsync(meetingId);
        if (meeting is null || meeting.EndedAtUtc is not null)
            return NotFound("Meeting not found or already ended.");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);

        var record = new MeetingFile
        {
            MeetingId = meetingId,
            FileName = Path.GetFileName(file.FileName),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            Size = file.Length,
            SenderUserId = CurrentUserId,
            SenderDisplayName = CurrentDisplayName,
            Content = ms.ToArray(),
        };
        db.MeetingFiles.Add(record);
        await db.SaveChangesAsync();

        return Ok(new FileUploadResponse(record.Id, record.FileName, record.Size, record.ContentType, "/api/files/" + record.Id));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Download(Guid id)
    {
        var file = await db.MeetingFiles.FindAsync(id);
        if (file is null) return NotFound();
        return File(file.Content, file.ContentType, file.FileName);
    }
}
