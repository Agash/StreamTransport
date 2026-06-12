using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agash.StreamTransport;

/// <summary>
/// The canonical JSON serializer for <see cref="SignalingMessage"/>. Every transport - the relay's
/// WebSocket endpoint, a SignalR hub, the room-aware client - serializes with this so the wire format is
/// identical everywhere. Backed by a source-generated <see cref="JsonSerializerContext"/> so it is
/// reflection-free and NativeAOT-compatible; enum values go on the wire as camelCase strings to match the
/// W3C WebRTC vocabulary that browser clients expect.
/// </summary>
public static class SignalingJson
{
    /// <summary>Serialize a signaling message to its canonical UTF-8 JSON form.</summary>
    public static byte[] SerializeToUtf8Bytes(SignalingMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, SignalingJsonContext.Default.SignalingMessage);

    /// <summary>Serialize a signaling message to its canonical JSON string form.</summary>
    public static string Serialize(SignalingMessage message) =>
        JsonSerializer.Serialize(message, SignalingJsonContext.Default.SignalingMessage);

    /// <summary>Parse a signaling message from UTF-8 JSON, or null when the payload is not a valid message.</summary>
    public static SignalingMessage? Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, SignalingJsonContext.Default.SignalingMessage);

    /// <summary>Parse a signaling message from a JSON string, or null when the payload is not a valid message.</summary>
    public static SignalingMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, SignalingJsonContext.Default.SignalingMessage);
}

/// <summary>Source-generated (reflection-free, AOT-safe) serializer metadata for the signaling protocol.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SignalingMessage))]
internal sealed partial class SignalingJsonContext : JsonSerializerContext;
