namespace TidalSharp.Exceptions;

public class RateLimitedException : APIException
{
    public RateLimitedException() { }
    public RateLimitedException(string message) : base(message) { }
    public RateLimitedException(string message, Exception inner) : base(message, inner) { }
}
