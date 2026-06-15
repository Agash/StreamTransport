#if HAS_PIPEWIRE
using System.Runtime.Versioning;
using Agash.StreamTransport.Codecs;
using PipeWire.NET;
using Spectre.Console;
using PwPixelFormat = PipeWire.NET.PixelFormat;

namespace StreamTransport.Agent;

/// <summary>
/// In-process verification of the Linux GPU zero-copy publish path that does NOT depend on GStreamer's dmabuf
/// support (which is broken on some PipeWire/driver stacks): a <see cref="VaapiPresentationPool"/> backs a
/// <see cref="PipeWireVideoOutput"/> dmabuf producer, and a <see cref="PipeWireVideoCapture"/> consumer (which
/// does the consumer-side DRM-modifier fixation) connects to it and counts the dmabuf frames it receives. If
/// frames flow, the producer (pool export + dmabuf negotiation) is correct end to end; the same surfaces a
/// real GL consumer (OBS) would import. Run: <c>selftest pwdmabuf</c>.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class PwDmaBufSelfTest
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            AnsiConsole.MarkupLine("[yellow]pwdmabuf selftest is Linux-only.[/]");
            return 1;
        }

        try
        {
            FFmpegLibrary.EnsureLoaded();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]FFmpeg natives not found: {ex.Message}[/]");
            return 1;
        }

        const int width = 320;
        const int height = 240;
        const int poolCount = 4;

        VaapiPresentationPool pool;
        try
        {
            pool = new VaapiPresentationPool(width, height, poolCount);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]VAAPI presentation pool unavailable: {ex.Message}[/]");
            return 1;
        }

        var ctx = new PipeWireContext();
        await ctx.StartAsync().ConfigureAwait(false);

        long modifier = (long)pool.Modifier;
        bool streaming = false;
        int framesProduced = 0;
        int framesConsumed = 0;
        int dmaBufConsumed = 0;

        var output = new PipeWireVideoOutput(ctx, "stx-dmabuf-selftest", width, height, PwPixelFormat.Nv12, 30);
        output.AllocateDmaBuf += (_, bufferIndex, _, _, _, planes) =>
        {
            if (bufferIndex >= poolCount)
            {
                return 0;
            }

            var surface = pool.Planes(bufferIndex);
            int n = Math.Min(surface.PlaneCount, planes.Length);
            for (int p = 0; p < n; p++)
            {
                var pl = surface[p];
                uint size = (uint)((int)pl.Stride * (p == 0 ? height : height / 2));
                planes[p] = new VideoPlane(pl.Fd, pl.Offset, (int)pl.Stride, size);
            }

            return n;
        };
        output.FillDmaBuf += (_, _) => { Interlocked.Increment(ref framesProduced); return true; };
        output.StateChanged += (_, _, newS) => streaming = newS == PipeWireStreamState.Streaming;
        output.ConnectDmaBuf([modifier]);

        // Wait for the producer node to be assigned an id, then point the consumer straight at it.
        uint nodeId = PipeWireVideoCapture.AnyNode;
        for (int i = 0; i < 50 && nodeId == PipeWireVideoCapture.AnyNode; i++)
        {
            nodeId = output.NodeId;
            if (nodeId == PipeWireVideoCapture.AnyNode)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        AnsiConsole.MarkupLineInterpolated($"[grey]producer node id = {nodeId}, modifier = 0x{pool.Modifier:x}[/]");

        var capture = new PipeWireVideoCapture(ctx);
        capture.FrameReady += (_, frame) =>
        {
            Interlocked.Increment(ref framesConsumed);
            if (frame.BufferType == PipeWireBufferType.DmaBuf)
            {
                Interlocked.Increment(ref dmaBufConsumed);
            }
        };
        capture.Connect(nodeId, [PwPixelFormat.Nv12], modifiers: [modifier]);

        // Drive the DRIVER producer at ~30 fps once it is streaming (no real frames here - the pool surfaces
        // are published as-is; this verifies negotiation + dmabuf delivery, not pixel content).
        using var driver = new Timer(_ => { if (streaming) { output.TriggerFrame(); } }, null, 100, 33);

        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);

        await capture.DisposeAsync().ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);
        pool.Dispose();
        await ctx.DisposeAsync().ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated(
            $"produced={framesProduced} consumed={framesConsumed} (dmabuf={dmaBufConsumed})");
        bool pass = dmaBufConsumed >= 10;
        AnsiConsole.MarkupLine(pass
            ? "[green]PWDMABUF-PASS[/] (GPU zero-copy dmabuf producer->consumer verified in-process)"
            : "[red]PWDMABUF-FAIL[/] (no dmabuf frames delivered - see whether the daemon negotiated the format)");
        return pass ? 0 : 1;
    }
}
#endif
