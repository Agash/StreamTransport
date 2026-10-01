using Agash.StreamTransport;
using Agash.StreamTransport.AspNetCore;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StreamTransport.Cli;

/// <summary>
/// <c>streamtransport serve</c>: WHEP at <c>/whep</c> playing an input, WHIP at <c>/whip/{name}</c>
/// taking a publisher into an output (or measuring it), and a page at <c>/</c> that plays and publishes
/// from a browser, all in one ASP.NET Core host.
/// </summary>
internal static class Serve
{
    public static async Task<int> RunAsync(
        CommandLine command,
        Action<IServiceCollection> addMedia,
        ILoggerFactory loggers,
        CancellationToken stop
    )
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(command.Relay.ToString());
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggers);
        addMedia(builder.Services);
        builder.Services.AddHttpMediaEndpoints();
        await using WebApplication app = builder.Build();
        ILogger log = loggers.CreateLogger("streamtransport");
        MediaDevices devices = app.Services.GetRequiredService<MediaDevices>();

        using IVideoInput? video = command.Video is { } v
            ? await devices.OpenVideoInputAsync(v, command.Capture, stop)
            : null;
        using IAudioInput? audio = command.Audio is { } a
            ? await devices.OpenAudioInputAsync(a, stop)
            : null;
        TestSignalAnalyzer? analyzer = command.Measure ? new TestSignalAnalyzer() : null;

        app.MapGet("/", () => Results.Content(Page, "text/html"));
        app.MapWhep(
            "/whep",
            (_, _) =>
                ValueTask.FromResult<HttpMediaSetup?>(
                    new HttpMediaSetup(
                        new MediaEndpoints { VideoSource = video, AudioSource = audio },
                        command.Session
                    )
                )
        );
        app.MapWhip(
            "/whip/{name}",
            async (context, cancellationToken) =>
            {
                string name = (string)context.Request.RouteValues["name"]!;
                if (analyzer is not null)
                {
                    return new HttpMediaSetup(
                        new MediaEndpoints
                        {
                            VideoSink = analyzer.WrapVideo(),
                            AudioSink = analyzer.WrapAudio(),
                        },
                        command.Session
                    );
                }

                IVideoOutput output = await devices.CreateVideoOutputAsync(
                    null,
                    name,
                    cancellationToken
                );
                IAudioOutput speaker = await devices.CreateAudioOutputAsync(
                    null,
                    null,
                    cancellationToken
                );
                return new HttpMediaSetup(
                    new MediaEndpoints { VideoSink = output, AudioSink = speaker },
                    command.Session
                )
                {
                    Ended = () =>
                    {
                        output.Dispose();
                        speaker.Dispose();
                        return ValueTask.CompletedTask;
                    },
                };
            }
        );

        await app.StartAsync(stop);
        log.LogInformation(
            "Serving WHEP at /whep, WHIP at /whip/{{name}} and a test page at / on {Urls}; Ctrl+C stops.",
            command.Relay
        );
        using PeriodicTimer every = new(TimeSpan.FromSeconds(5));
        try
        {
            while (await every.WaitForNextTickAsync(stop))
            {
                if (analyzer is not null)
                {
                    log.LogInformation("WHIP test signal: {Measurement}.", analyzer.Measure());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: Ctrl+C is how serving ends.
        }

        await app.StopAsync(CancellationToken.None);
        return 0;
    }

    // Plays WHEP into a video element, and publishes WHIP from a canvas and WebAudio drawing the test
    // signal on whole wall-clock seconds; window.report() reads the connections' statistics.
    private const string Page = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>StreamTransport WHIP and WHEP</title></head>
        <body>
        <video id="play" autoplay muted playsinline width="640"></video>
        <canvas id="draw" width="640" height="360"></canvas>
        <script>
        const gathered = pc => new Promise(resolve => {
          if (pc.iceGatheringState === 'complete') { resolve(); return; }
          pc.addEventListener('icegatheringstatechange', () => { if (pc.iceGatheringState === 'complete') resolve(); });
          setTimeout(resolve, 2000);
        });
        async function exchange(pc, url) {
          await pc.setLocalDescription(await pc.createOffer());
          await gathered(pc);
          const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: pc.localDescription.sdp });
          if (response.status !== 201) throw new Error('offer answered ' + response.status);
          await pc.setRemoteDescription({ type: 'answer', sdp: await response.text() });
          return response.headers.get('Location');
        }
        window.whep = async () => {
          const pc = new RTCPeerConnection();
          pc.addTransceiver('video', { direction: 'recvonly' });
          pc.addTransceiver('audio', { direction: 'recvonly' });
          const stream = new MediaStream();
          pc.ontrack = e => { stream.addTrack(e.track); document.getElementById('play').srcObject = stream; };
          window.whepPc = pc;
          return exchange(pc, '/whep');
        };
        window.whip = async name => {
          const canvas = document.getElementById('draw');
          const g = canvas.getContext('2d');
          const audio = new AudioContext();
          const tone = audio.createOscillator(); tone.frequency.value = 1000;
          const gate = audio.createGain(); gate.gain.value = 0;
          const out = audio.createMediaStreamDestination();
          tone.connect(gate).connect(out); tone.start();
          for (let s = Math.ceil(Date.now() / 1000) + 1; s < Math.ceil(Date.now() / 1000) + 600; s++) {
            const at = audio.currentTime + (s * 1000 - Date.now()) / 1000;
            gate.gain.setValueAtTime(0.5, at); gate.gain.setValueAtTime(0, at + 0.04);
          }
          const draw = () => {
            const now = Date.now(), into = now % 1000;
            g.fillStyle = '#222'; g.fillRect(0, 0, 640, 360);
            if (into < 17 || into >= 983) { g.fillStyle = '#fff'; g.fillRect(160, 90, 320, 180); }
            const cell = Math.floor(640 / 40), rows = Math.max(1, Math.floor(360 / 48));
            for (let bit = 0; bit < 40; bit++) {
              g.fillStyle = Math.floor(now / 2 ** (39 - bit)) % 2 ? '#fff' : '#000';
              g.fillRect(bit * cell, 0, cell, rows);
            }
            requestAnimationFrame(draw);
          };
          draw();
          const pc = new RTCPeerConnection();
          for (const track of [...canvas.captureStream(30).getTracks(), ...out.stream.getTracks()]) {
            pc.addTransceiver(track, { direction: 'sendonly' });
          }
          window.whipPc = pc;
          return exchange(pc, '/whip/' + name);
        };
        window.report = async () => {
          const result = {};
          for (const [key, pc] of [['whep', window.whepPc], ['whip', window.whipPc]]) {
            if (!pc) continue;
            const stats = {};
            (await pc.getStats()).forEach(s => {
              if (s.type === 'inbound-rtp') stats['in-' + s.kind] = { packets: s.packetsReceived, frames: s.framesDecoded, codec: s.codecId, lost: s.packetsLost };
              if (s.type === 'outbound-rtp') stats['out-' + s.kind] = { packets: s.packetsSent, frames: s.framesEncoded };
              if (s.type === 'transport') stats.transport = { dtls: s.dtlsState, srtp: s.srtpCipher, tls: s.tlsVersion };
            });
            stats.state = pc.connectionState;
            result[key] = stats;
          }
          const v = document.getElementById('play');
          result.video = { width: v.videoWidth, height: v.videoHeight, time: v.currentTime };
          return result;
        };
        </script>
        </body></html>
        """;
}
