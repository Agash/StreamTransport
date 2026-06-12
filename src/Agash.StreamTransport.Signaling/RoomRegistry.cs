using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Agash.StreamTransport.Signaling;

/// <summary>One connected peer in a room: its id, role, and the link the router pushes messages down.</summary>
internal sealed record Peer(PeerId Id, PeerRole Role, ISignalingPeerTransport Transport)
{
    public PeerInfo Info => new(Id, Role);
}

/// <summary>
/// An in-memory room. Holds the connected peers keyed by id. The router only routes signaling; media
/// flows peer-to-peer via WebRTC and never touches this process.
/// </summary>
internal sealed class Room(RoomCode code)
{
    private readonly ConcurrentDictionary<PeerId, Peer> _peers = new();

    public RoomCode Code { get; } = code;

    public bool IsEmpty => _peers.IsEmpty;

    public void Add(Peer peer) => _peers[peer.Id] = peer;

    public void Remove(PeerId id) => _peers.TryRemove(id, out _);

    public ISignalingPeerTransport? TransportFor(PeerId id) =>
        _peers.TryGetValue(id, out Peer? peer) ? peer.Transport : null;

    public IReadOnlyList<PeerInfo> Snapshot() => [.. _peers.Values.Select(static p => p.Info)];

    /// <summary>Send a message to every peer except <paramref name="except"/>.</summary>
    public async ValueTask BroadcastExceptAsync(PeerId except, SignalingMessage message, CancellationToken cancellationToken)
    {
        foreach (Peer peer in _peers.Values)
        {
            if (peer.Id == except)
            {
                continue;
            }

            try
            {
                await peer.Transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A broken peer link must not abort the broadcast; that peer's own session will clean up.
            }
        }
    }
}

/// <summary>
/// The room registry: creates rooms, mints peer ids, and garbage-collects rooms once their last peer
/// leaves. Thread-safe; a single registry backs the whole router.
/// </summary>
internal sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private long _nextPeerId;

    public PeerId MintPeerId() => new(Interlocked.Increment(ref _nextPeerId));

    /// <summary>Create a room with a freshly generated, unique code.</summary>
    public Room Create()
    {
        while (true)
        {
            RoomCode code = GenerateCode();
            var room = new Room(code);
            if (_rooms.TryAdd(code.Value, room))
            {
                return room;
            }
        }
    }

    public Room? Get(RoomCode code) => _rooms.TryGetValue(code.Value, out Room? room) ? room : null;

    /// <summary>
    /// Get the room with this code, creating it if absent. Used for a publisher joining: the publisher
    /// owns its room and may have minted the code itself, or be reconnecting after a transient drop that
    /// GC'd the room.
    /// </summary>
    public Room GetOrCreate(RoomCode code) =>
        _rooms.GetOrAdd(code.Value, static (_, c) => new Room(c), code);

    /// <summary>Remove the room if it has no peers left. Idempotent.</summary>
    public void RemoveIfEmpty(RoomCode code)
    {
        if (_rooms.TryGetValue(code.Value, out Room? room) && room.IsEmpty)
        {
            // Re-check emptiness under the removal to avoid evicting a room a peer just joined.
            _rooms.TryRemove(new KeyValuePair<string, Room>(code.Value, room));
        }
    }

    /// <summary>
    /// Generate a short, lowercase, pronounceable room code from a CSPRNG (alternating consonant/vowel).
    /// Six characters give enough entropy for a self-hosted relay; the registry collision-checks anyway.
    /// </summary>
    private static RoomCode GenerateCode()
    {
        ReadOnlySpan<char> consonants = "bcdfghjklmnpqrstvwxz";
        ReadOnlySpan<char> vowels = "aeiouy";
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);

        Span<char> chars = stackalloc char[6];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (i % 2 == 0)
                ? consonants[bytes[i] % consonants.Length]
                : vowels[bytes[i] % vowels.Length];
        }

        return new RoomCode(new string(chars));
    }
}
