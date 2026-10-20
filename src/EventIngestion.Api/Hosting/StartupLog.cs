namespace EventIngestion.Api.Hosting;

/// <summary>Start-up log messages (source-generated).</summary>
public static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "EventIngestion.Api {Version} starting in {Environment}")]
    public static partial void Starting(ILogger logger, string version, string environment);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authentication is DISABLED (Auth:Enabled=false). Do not expose this instance beyond a trusted network.")]
    public static partial void AuthDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limiting is DISABLED (RateLimiting:Enabled=false).")]
    public static partial void RateLimitDisabled(ILogger logger);
}

