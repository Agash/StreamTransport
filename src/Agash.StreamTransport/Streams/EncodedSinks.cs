using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Streams;

/// <summary>Takes an encoded video frame for the transport; the frame is borrowed for the call.</summary>
/// <param name="frame">The frame.</param>
internal delegate void EncodedVideoSink(in EncodedVideoFrame frame);

/// <summary>Takes an encoded audio packet for the transport; the packet is borrowed for the call.</summary>
/// <param name="frame">The packet.</param>
internal delegate void EncodedAudioSink(in EncodedAudioFrame frame);
