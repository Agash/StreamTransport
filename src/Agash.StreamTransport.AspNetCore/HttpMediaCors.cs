namespace Agash.StreamTransport.AspNetCore;

/// <summary>
/// What a CORS policy for WHIP and WHEP endpoints lets a browser client see. The endpoints answer the
/// preflight themselves (RFC 9725 section 4.2); the policy the host applies with <c>RequireCors</c>
/// decides which origins get in, and should expose these headers.
/// </summary>
public static class HttpMediaCors
{
    /// <summary>
    /// The response headers a browser client reads: <c>Location</c> names the session (RFC 9725 section
    /// 4.2), <c>Link</c> carries the STUN and TURN servers (section 4.6) and the WHEP profile, and
    /// <c>ETag</c> tags the ICE session for later <c>PATCH</c> requests (section 4.3.1), and
    /// <c>Accept-Post</c> and <c>Accept-Patch</c> name what the endpoint and the session take. Pass them to
    /// <c>CorsPolicyBuilder.WithExposedHeaders</c>.
    /// </summary>
    public static IReadOnlyList<string> ExposedHeaders { get; } =
    ["Location", "Link", "ETag", "Accept-Post", "Accept-Patch"];

    /// <summary>
    /// The request headers a browser client sends: <c>Authorization</c> for the bearer token (section
    /// 4.7.1), <c>Content-Type</c> for SDP and trickle fragments, and <c>If-Match</c> for the ICE session.
    /// Pass them to <c>CorsPolicyBuilder.WithHeaders</c>.
    /// </summary>
    public static IReadOnlyList<string> AllowedHeaders { get; } =
    ["Authorization", "Content-Type", "If-Match"];

    /// <summary>The methods the endpoint and its sessions take. Pass them to <c>WithMethods</c>.</summary>
    public static IReadOnlyList<string> AllowedMethods { get; } =
    ["GET", "HEAD", "OPTIONS", "POST", "PATCH", "DELETE"];
}
