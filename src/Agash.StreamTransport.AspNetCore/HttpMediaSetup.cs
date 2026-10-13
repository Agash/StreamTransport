using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Agash.StreamTransport.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using WebRtcSdp = Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.AspNetCore;


/// <summary>What a session made for an HTTP offer sends and receives, and how it is set up.</summary>
/// <param name="Endpoints">
/// For WHIP ingest, the sinks the publisher's media goes to; for WHEP playback, the sources the player
/// receives.
/// </param>
/// <param name="Options">How the session is set up.</param>
public sealed record HttpMediaSetup(MediaEndpoints Endpoints, MediaSessionOptions Options)
{
    /// <summary>Called once when the session ends, by the client's DELETE, a failure or shutdown.</summary>
    public Func<ValueTask>? Ended { get; init; }
}

/// <summary>
/// Decides what a WHIP or WHEP offer gets: its endpoints and options, or null to refuse it with
/// <c>404 Not Found</c>. Authentication and authorization are the host's, applied to the mapped group.
/// </summary>
/// <param name="context">The request, after the host's middleware.</param>
/// <param name="cancellationToken">The request's cancellation.</param>
/// <returns>The setup, or null.</returns>
public delegate ValueTask<HttpMediaSetup?> HttpMediaHandler(
    HttpContext context,
    CancellationToken cancellationToken
);

