using Microsoft.EntityFrameworkCore;
using ZPlus.Server.Models;

namespace ZPlus.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingParticipantRecord> MeetingParticipants => Set<MeetingParticipantRecord>();
    public DbSet<ChatMessageRecord> ChatMessages => Set<ChatMessageRecord>();
    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();
    public DbSet<MeetingInvitation> MeetingInvitations => Set<MeetingInvitation>();
    public DbSet<Poll> Polls => Set<Poll>();
    public DbSet<PollVote> PollVotes => Set<PollVote>();
    public DbSet<MeetingFile> MeetingFiles => Set<MeetingFile>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<ServerSetting>().HasKey(s => s.Key);
        modelBuilder.Entity<Meeting>().HasIndex(m => m.MeetingCode).IsUnique();
        modelBuilder.Entity<Meeting>()
            .HasOne(m => m.Host)
            .WithMany(u => u.HostedMeetings)
            .HasForeignKey(m => m.HostUserId);
        modelBuilder.Entity<MeetingParticipantRecord>().HasIndex(p => p.MeetingId);
        modelBuilder.Entity<MeetingInvitation>().HasIndex(i => i.MeetingId);
        modelBuilder.Entity<ChatMessageRecord>().HasIndex(c => c.MeetingId);
        modelBuilder.Entity<Poll>().HasIndex(p => p.MeetingId);
        modelBuilder.Entity<PollVote>().HasIndex(v => new { v.PollId, v.UserId }).IsUnique();
        modelBuilder.Entity<MeetingFile>().HasIndex(f => f.MeetingId);
        modelBuilder.Entity<AuditLog>().HasIndex(a => a.WhenUtc);
    }
}
