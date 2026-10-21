using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Streams;

/// <summary>Takes an encoded video frame for the transport; the frame is borrowed for the call.</summary>
/// <param name="frame">The frame.</param>
/// <param name="timing">Its encode timing, when it is a timing frame.</param>
internal delegate void EncodedVideoSink(in EncodedVideoFrame frame, FrameSendTiming? timing);

/// <summary>Takes an encoded audio packet for the transport; the packet is borrowed for the call.</summary>
/// <param name="frame">The packet.</param>
internal delegate void EncodedAudioSink(in EncodedAudioFrame frame);
