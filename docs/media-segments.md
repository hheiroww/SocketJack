# Encode video and audio segments

`FfmpegSegmentEncoder` is available in the core SocketJack package. Give it the path to your application's FFmpeg executable with the libx264 and native AAC encoders enabled. The library passes raw media and encoded bytes through process pipes. The application supplies capture, media permissions, transport, and playback.

```csharp
using SocketJack.Media;

var encoder = new FfmpegSegmentEncoder(ffmpegExecutable);
byte[] video = await encoder.EncodeVideoAsync(
    bgraFrames, width, height, framesPerSecond, videoBitsPerSecond, cancellationToken);
byte[] audio = await encoder.EncodeAudioAsync(
    monoPcm16, sampleRate, audioBitsPerSecond, cancellationToken);
```

Video inputs contain tightly packed BGRA frames with even dimensions. Each result contains MP4 initialization boxes followed by fragmented MP4 media. Video uses H.264 constrained baseline level 3.1 (`avc1.42C01F`) in YUV 4:2:0. The first picture is a keyframe and subsequent pictures can use inter-frame prediction.

Audio inputs contain mono, signed little-endian 16-bit PCM. The result carries AAC-LC frames with ADTS headers. Retain the source sample count and account for encoder delay and final padding when joining decoded audio segments.

Choose the bitrate independently on each call. Each segment uses a fresh encoder process and independently starts its media timeline. Attach sequence numbers and timing information if your application plays segments continuously. The output limit is 512 KiB per call. Divide a longer segment or choose a lower bitrate if the encoder exceeds that limit. Cancellation stops the process and releases its pipes.
