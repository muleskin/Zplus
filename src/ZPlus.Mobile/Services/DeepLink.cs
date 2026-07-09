using ZPlus.Shared;

namespace ZPlus.Client.Services;

/// <summary>
/// Mobile stand-in for the desktop deep-link service. The desktop version handles
/// single-instance activation via named pipes and OS scheme registration; on mobile that
/// is done by the platform (Android intent filters / iOS URL types), so this only carries a
/// pending join between activation and the Home screen. HomeViewModel calls TakePendingJoin().
/// </summary>
public static class DeepLink
{
    private static ZplusJoinLink? _pending;

    public static ZplusJoinLink? PendingJoin => _pending;

    public static void SetPendingJoin(ZplusJoinLink link)
    {
        _pending = link;
        if (!string.IsNullOrWhiteSpace(link.Server))
            AppSession.Current.ServerUrl = link.Server;
    }

    /// <summary>Returns and clears any pending zplus:// join.</summary>
    public static ZplusJoinLink? TakePendingJoin()
    {
        var link = _pending;
        _pending = null;
        return link;
    }
}
