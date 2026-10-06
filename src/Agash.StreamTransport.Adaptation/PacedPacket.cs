using System.Buffers;

namespace Agash.StreamTransport.Adaptation;

/// <summary>A packet waiting at the <see cref="Pacer"/>.</summary>
/// <param name="Buffer">
/// The packet, at the start of a buffer rented from <see cref="ArrayPool{T}.Shared"/>. The pacer owns it
/// from enqueue and returns it after the send callback. The buffer may be larger than the packet, so a
/// transport can protect it in place.
/// </param>
/// <param name="Length">The packet's length.</param>
/// <param name="Class">What it carries.</param>
public readonly record struct PacedPacket(byte[] Buffer, int Length, TrafficClass Class);
