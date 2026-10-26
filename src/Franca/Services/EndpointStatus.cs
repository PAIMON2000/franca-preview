namespace Franca.Services;

public enum EndpointStatus
{
    Untested,
    Testing,
    Ok,
    Slow,
    Limited,
    Timeout,
    NoKey,
    Failed
}
