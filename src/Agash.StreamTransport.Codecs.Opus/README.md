# Agash.StreamTransport.Codecs.Opus

Opus audio for [Agash.StreamTransport](https://github.com/Agash/StreamTransport) on Concentus, a managed Opus implementation, behind the audio codec contracts of `Agash.StreamTransport.Media`. `OpusAudioEncoderFactory` and `OpusAudioDecoderFactory` register it with a pipeline like any other audio codec.

- `OpusAudioEncoder` takes PCM frames of any length at 8 to 48 kHz, mono or stereo, and hands out packets of 10, 20, 40 or 60 ms, each stamped with the capture time of its first sample. It carries in-band forward error correction and takes bit rate changes while running.
- `OpusAudioDecoder` produces 48 kHz stereo float PCM from any Opus stream. It conceals a lost packet, or recovers it from the correction data in the packet after it through `IAudioLossRecovery`.

The package has no native dependency.
