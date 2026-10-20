namespace EventIngestion.Core.Models;

public enum AddResult
{
    Accepted,
    /// <summary>Older than the window; would never contribute.</summary>
    Stale,
    /// <summary>Further in the future than the allowed clock skew.</summary>
    Future,
    /// <summary>The window was removed by the sweeper; caller must re-resolve the device.</summary>
    Retired,
}
