namespace EventIngestion.Api.Hosting;

/// <summary>
/// Drives /health/ready: false until the host has finished starting (and any recovery), false again as soon as
/// shutdown begins so load balancers stop sending traffic while in-flight requests drain.
/// </summary>
public sealed class ReadinessState
{
    private volatile bool _ready;

    public bool IsReady => _ready;

    public void MarkReady() => _ready = true;

    public void MarkNotReady() => _ready = false;
}
