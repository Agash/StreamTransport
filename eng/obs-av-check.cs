#:package ObsWebSocket.Core@0.5.0-dev5
#:package Microsoft.Extensions.Logging.Console@10.0.0
#:property TargetFramework=net11.0
#:property Nullable=enable
#:property PublishAot=false
#:property ManagePackageVersionsCentrally=false

// Measures lip sync as OBS records it. A local relay carries the test signal from a publisher to a
// subscriber that plays it into this platform's video sharing (Spout, Syphon, PipeWire) and the default
// audio output; OBS captures both in a scene collection and profile of their own, records a clip, and
// the clip is measured with `streamtransport analyze`. The subscriber also measures where it hands the
// media over, so the two offsets give what the output path and OBS add.
//
//   dotnet run eng/obs-av-check.cs -- --cli <streamtransport> --relay <StreamTransport.Relay>
//       [--seconds 20] [--keep] [--obs-uri ws://127.0.0.1:4455]
//
// The OBS password is read from obs-websocket's own configuration on this machine. Whatever OBS had
// selected is selected again at the end, and the collection and profile made here are deleted.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

const string Collection = "StreamTransportAVCheck";
const string Profile = "StreamTransportAVCheck";
const string Scene = "Check";
const string SenderName = "StreamTransport";

int seconds = int.Parse(Argument("--seconds") ?? "20", CultureInfo.InvariantCulture);
bool keep = args.Contains("--keep");
Uri obsUri = new(Argument("--obs-uri") ?? "ws://127.0.0.1:4455");
string port = Argument("--port") ?? "8188";
string room = $"avcheck{Environment.ProcessId}";

(
    string videoSpec,
    string videoKind,
    JsonElement videoSettings,
    string audioKind,
    JsonElement audioSettings
) =
    OperatingSystem.IsWindows()
        ? (
            "spout:" + SenderName,
            "spout_capture",
            Json(new { spoutsenders = SenderName }),
            "wasapi_output_capture",
            Json(new { device_id = "default" })
        )
    : OperatingSystem.IsMacOS()
        ? (
            "syphon:" + SenderName,
            "syphon-input",
            Json(new { app_name = "streamtransport", name = SenderName }),
            "sck_audio_capture",
            Json(new { type = 0 })
        )
    : (
        "pipewire:" + SenderName,
        "pipewire-camera-source",
        Json(new { device_id = SenderName }),
        "pulse_output_capture",
        Json(new { device_id = "default" })
    );

using ILoggerFactory loggers = LoggerFactory.Create(b =>
    b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning)
);
if (args.Contains("--restore"))
{
    return await RestoreAsync(
        Argument("--collection") ?? throw new ArgumentException("--restore needs --collection."),
        Argument("--profile") ?? throw new ArgumentException("--restore needs --profile.")
    );
}

string cli =
    Argument("--cli") ?? throw new ArgumentException("--cli names the streamtransport executable.");
string relay =
    Argument("--relay")
    ?? throw new ArgumentException("--relay names the StreamTransport.Relay executable.");
string work = Directory.CreateTempSubdirectory("streamtransport-obs-").FullName;
List<Process> processes = [];
string? recording = null;
Lock logGate = new();
StreamWriter? subscriberLog = null;
TimeSpan? handOver = null;
string? outputOffset = null;
try
{
    processes.Add(
        Start(relay, [], new() { ["STREAMTRANSPORT_RELAY_URLS"] = $"http://127.0.0.1:{port}" })
    );
    await Task.Delay(TimeSpan.FromSeconds(3));
    string relayUrl = $"ws://127.0.0.1:{port}/ws";
    // The publisher's hello makes the room; a subscriber arriving first is told there is none.
    Process publisher = Start(
        cli,
        ["publish", "--relay", relayUrl, "--room", room, "--video", "test", "--audio", "test"]
    );
    processes.Add(publisher);
    TaskCompletionSource inRoom = new(TaskCreationOptions.RunContinuationsAsynchronously);
    publisher.OutputDataReceived += (_, e) =>
    {
        if (e.Data?.Contains("Publisher in room", StringComparison.Ordinal) == true)
        {
            inRoom.TrySetResult();
        }
    };
    publisher.BeginOutputReadLine();
    await inRoom.Task.WaitAsync(TimeSpan.FromSeconds(30));
    // No --measure: it sends the pictures to an in-memory analyzer instead of the output OBS reads. The
    // session's own lip sync, measured at the hand-over, is in its report either way.
    Process subscriber = Start(
        cli,
        [
            "subscribe",
            "--relay",
            relayUrl,
            "--room",
            room,
            "--video",
            videoSpec,
            "--audio",
            "default",
        ]
    );
    processes.Add(subscriber);
    Regex measured = new(@"lip sync (-?\d+(\.\d+)?) ms");
    Regex decodedVideo = new(@"decoded (\d+) video");
    TaskCompletionSource playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    subscriberLog = new(Path.Combine(work, "subscriber.log"));
    subscriber.OutputDataReceived += (_, e) =>
    {
        if (e.Data is { } line)
        {
            lock (logGate)
            {
                subscriberLog?.WriteLine(line);
            }
        }

        if (
            e.Data is { } decodedLine
            && decodedVideo.Match(decodedLine) is { Success: true } decoded
            && decoded.Groups[1].Value != "0"
        )
        {
            playing.TrySetResult();
        }

        if (
            e.Data is { } offsetLine
            && Regex.Match(offsetLine, "audio output offset (-?[0-9.]+) ms")
                is { Success: true } offsetMatch
        )
        {
            outputOffset = offsetMatch.Groups[1].Value;
        }

        if (e.Data is { } text && measured.Match(text) is { Success: true } match)
        {
            handOver = TimeSpan.FromMilliseconds(
                double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            );
        }
    };
    subscriber.BeginOutputReadLine();

    // The sender appears with the first decoded picture, which waits for a keyframe.
    await playing.Task.WaitAsync(TimeSpan.FromSeconds(60));

    Process? startedObs = await EnsureObsAsync();
    await using ObsWebSocketClient obs = new(
        loggers.CreateLogger<ObsWebSocketClient>(),
        _ => new JsonMessageSerializer(loggers.CreateLogger<JsonMessageSerializer>()),
        Options.Create(
            new ObsWebSocketClientOptions
            {
                ServerUri = obsUri,
                Password = ObsPassword(),
                RequestTimeoutMs = 60000,
                NotReadyRetry = new()
                {
                    Enabled = true,
                    MaxRetryAttempts = 60,
                    MaxDelayMs = 1000,
                },
            }
        )
    );
    await obs.ConnectAsync();

    // OBS answers NotReady while it loads; switching collections before it has finished can leave it
    // refusing every request afterwards.
    await UntilAsync(
        async () =>
        {
            try
            {
                _ = await obs.Config.GetSceneCollectionListAsync();
                return true;
            }
            catch (ObsWebSocketRequestException)
            {
                return false;
            }
        },
        TimeSpan.FromSeconds(1)
    );
    GetSceneCollectionListResponseData collections = (
        await obs.Config.GetSceneCollectionListAsync()
    )!;
    GetProfileListResponseData profiles = (await obs.Config.GetProfileListAsync())!;
    // What OBS had selected, kept in a file until it is selected again, so a run that is interrupted
    // part way is undone by the next one.
    string state = Path.Combine(Path.GetTempPath(), "streamtransport-obs-av-check.json");
    (string previousCollection, string previousProfile) = File.Exists(state)
        ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(state)) is [string c, string p]
            ? (c, p)
            : throw new InvalidDataException($"{state} is not a pair of names.")
        : (collections.CurrentSceneCollectionName, profiles.CurrentProfileName);
    File.WriteAllText(
        state,
        JsonSerializer.Serialize(new[] { previousCollection, previousProfile })
    );
    try
    {
        // Both switches take effect asynchronously; nothing is set until OBS reports them current.
        // An earlier run that stopped part way may have left either behind; it is reused.
        if (collections.SceneCollections.Contains(Collection))
        {
            await obs.Config.SetCurrentSceneCollectionAsync(
                new() { SceneCollectionName = Collection }
            );
        }
        else
        {
            await obs.Config.CreateSceneCollectionAsync(new() { SceneCollectionName = Collection });
        }

        await UntilAsync(async () =>
            (await obs.Config.GetSceneCollectionListAsync())!.CurrentSceneCollectionName
            == Collection
        );
        if (profiles.Profiles.Contains(Profile))
        {
            await obs.Config.SetCurrentProfileAsync(new() { ProfileName = Profile });
        }
        else
        {
            await obs.Config.CreateProfileAsync(new() { ProfileName = Profile });
        }

        await UntilAsync(async () =>
            (await obs.Config.GetProfileListAsync())!.CurrentProfileName == Profile
        );
        await obs.Config.SetProfileParameterAsync(new("Output", "Mode", "Simple"));
        await obs.Config.SetProfileParameterAsync(new("SimpleOutput", "RecFormat2", "mkv"));
        await obs.Config.SetRecordDirectoryAsync(new() { RecordDirectory = work });
        string directory = (await obs.Config.GetRecordDirectoryAsync())!.RecordDirectory;
        if (Path.GetFullPath(directory) != Path.GetFullPath(work))
        {
            throw new InvalidOperationException($"OBS records into {directory}, not {work}.");
        }
        foreach (string input in new[] { "Signal", "Sound" })
        {
            if ((await obs.Inputs.GetInputListAsync(new()))!.Inputs.Any(i => i.InputName == input))
            {
                await obs.Inputs.RemoveInputAsync(new() { InputName = input });
            }
        }

        if (!(await obs.Scenes.GetSceneListAsync(new()))!.Scenes.Any(sc => sc.SceneName == Scene))
        {
            await obs.Scenes.CreateSceneAsync(new(Scene));
        }
        await obs.Scenes.SetCurrentProgramSceneAsync(new() { SceneName = Scene });
        long signalItem = (
            await obs.Inputs.CreateInputAsync(
                new(
                    "Signal",
                    videoKind,
                    sceneName: Scene,
                    inputSettings: videoSettings,
                    sceneItemEnabled: true
                )
            )
        )!.SceneItemId;

        // Fill the canvas, so the picture's centre, where the flash is, is the recording's centre.
        var canvas = (await obs.Config.GetVideoSettingsAsync())!;
        await obs.SceneItems.SetSceneItemTransformAsync(
            new(
                signalItem,
                new()
                {
                    PositionX = 0,
                    PositionY = 0,
                    Alignment = 5,
                    BoundsType = "OBS_BOUNDS_STRETCH",
                    BoundsAlignment = 0,
                    BoundsWidth = canvas.BaseWidth,
                    BoundsHeight = canvas.BaseHeight,
                },
                sceneName: Scene
            )
        );
        await obs.Inputs.CreateInputAsync(
            new(
                "Sound",
                audioKind,
                sceneName: Scene,
                inputSettings: audioSettings,
                sceneItemEnabled: true
            )
        );
        await BindToLiveServerAsync(obs);
        await Task.Delay(TimeSpan.FromSeconds(2));

        await obs.Record.StartRecordAsync();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        string path = (await obs.Record.StopRecordAsync())!.OutputPath;

        // Stopped is not written: the muxer finishes the file after the output stops.
        await UntilAsync(async () => !(await obs.Record.GetRecordStatusAsync())!.OutputActive);
        long size = -1;
        await UntilAsync(
            () =>
            {
                long now = new FileInfo(path).Length;
                bool settled = now == size && now > 0;
                size = now;
                return Task.FromResult(settled);
            },
            TimeSpan.FromSeconds(1)
        );
        recording = path;
    }
    finally
    {
        if (previousCollection != Collection)
        {
            await obs.Config.SetCurrentSceneCollectionAsync(
                new() { SceneCollectionName = previousCollection }
            );
            await UntilAsync(async () =>
                (await obs.Config.GetSceneCollectionListAsync())!.CurrentSceneCollectionName
                == previousCollection
            );
        }

        if (previousProfile != Profile)
        {
            await obs.Config.SetCurrentProfileAsync(new() { ProfileName = previousProfile });
            await UntilAsync(async () =>
                (await obs.Config.GetProfileListAsync())!.CurrentProfileName == previousProfile
            );
        }

        if ((await obs.Config.GetProfileListAsync())!.Profiles.Contains(Profile))
        {
            await obs.Config.RemoveProfileAsync(new() { ProfileName = Profile });
        }

        DeleteCollection();
        File.Delete(state);
        _ = startedObs;
    }
}
finally
{
    foreach (Process process in Enumerable.Reverse(processes))
    {
        Stop(process);
    }

    // Lines still arriving from a stopped process are dropped.
    lock (logGate)
    {
        subscriberLog?.Dispose();
        subscriberLog = null;
    }

    if (recording is null && !keep)
    {
        Directory.Delete(work, recursive: true);
    }
}

if (recording is null)
{
    Console.Error.WriteLine("OBS recorded nothing.");
    return 1;
}

Console.WriteLine(
    FormattableString.Invariant(
        $"At the subscriber's hand-over: {(handOver is { } h ? $"{h.TotalMilliseconds:0.0} ms" : "not measured")}, audio played {outputOffset ?? "?"} ms from its slot for the outputs' latencies"
    )
);
Process analysis = Start(cli, ["analyze", recording], redirect: false);
await analysis.WaitForExitAsync();
if (!keep)
{
    File.Delete(recording);
    Directory.Delete(work, recursive: true);
}
else
{
    Console.WriteLine($"Kept {recording}");
}

return analysis.ExitCode;

// Starts OBS when nothing listens for obs-websocket, and returns it so it is closed again at the end.
async Task<Process?> EnsureObsAsync()
{
    if (await ListeningAsync())
    {
        return null;
    }

    ProcessStartInfo start =
        OperatingSystem.IsWindows()
            ? new(
                @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
                ["--disable-shutdown-check", "--disable-updater"]
            )
            {
                WorkingDirectory = @"C:\Program Files\obs-studio\bin\64bit",
            }
        : OperatingSystem.IsMacOS()
            ? new("open", ["-a", "OBS", "--args", "--disable-shutdown-check", "--disable-updater"])
        : new("obs", ["--disable-shutdown-check"]);
    // Through the shell, as a user's launch is: a child of this console gets no visible window, and an
    // OBS without one never finishes switching collections.
    start.UseShellExecute = true;
    Process obs = Process.Start(start) ?? throw new InvalidOperationException("OBS did not start.");
    await UntilAsync(ListeningAsync, TimeSpan.FromSeconds(1));
    return obs;
}

async Task<bool> ListeningAsync()
{
    using System.Net.Sockets.TcpClient probe = new();
    try
    {
        await probe.ConnectAsync(obsUri.Host, obsUri.Port);
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return false;
    }
}

// Puts the running OBS back on the given collection and profile: stops a recording an interrupted
// run left going, switches, and removes what the check made.
async Task<int> RestoreAsync(string collection, string profile)
{
    await using ObsWebSocketClient obs = new(
        loggers.CreateLogger<ObsWebSocketClient>(),
        _ => new JsonMessageSerializer(loggers.CreateLogger<JsonMessageSerializer>()),
        Options.Create(
            new ObsWebSocketClientOptions
            {
                ServerUri = obsUri,
                Password = ObsPassword(),
                RequestTimeoutMs = 60000,
                NotReadyRetry = new()
                {
                    Enabled = true,
                    MaxRetryAttempts = 60,
                    MaxDelayMs = 1000,
                },
            }
        )
    );
    await obs.ConnectAsync();
    if ((await obs.Record.GetRecordStatusAsync())!.OutputActive)
    {
        _ = await obs.Record.StopRecordAsync();
        await UntilAsync(async () => !(await obs.Record.GetRecordStatusAsync())!.OutputActive);
    }

    await obs.Config.SetCurrentSceneCollectionAsync(new() { SceneCollectionName = collection });
    await UntilAsync(async () =>
        (await obs.Config.GetSceneCollectionListAsync())!.CurrentSceneCollectionName == collection
    );
    await obs.Config.SetCurrentProfileAsync(new() { ProfileName = profile });
    await UntilAsync(async () =>
        (await obs.Config.GetProfileListAsync())!.CurrentProfileName == profile
    );
    if ((await obs.Config.GetProfileListAsync())!.Profiles.Contains(Profile))
    {
        await obs.Config.RemoveProfileAsync(new() { ProfileName = Profile });
    }

    DeleteCollection();
    File.Delete(Path.Combine(Path.GetTempPath(), "streamtransport-obs-av-check.json"));
    Console.WriteLine($"OBS is on {collection} and {profile}.");
    return 0;
}

// Polls until the condition holds, for at most 600 polls.
static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan? every = null)
{
    for (int i = 0; i < 600; i++)
    {
        if (await condition())
        {
            return;
        }

        await Task.Delay(every ?? TimeSpan.FromMilliseconds(100));
    }

    throw new TimeoutException("OBS did not reach the state the check waits for.");
}

// Syphon: the source binds by uuid, which a new server changes, and only rebinds by name once the old one
// has retired. Point it at the live server explicitly, as choosing it in OBS's list would.
async Task BindToLiveServerAsync(ObsWebSocketClient obs)
{
    if (videoKind == "spout_capture")
    {
        // Spout senders are found by name; OBS must list ours, or its Spout library cannot see it.
        string[] names = [];
        try
        {
            await UntilAsync(
                async () =>
                {
                    var senders = (
                        await obs.Inputs.GetInputPropertiesListPropertyItemsAsync(
                            new() { InputName = "Signal", PropertyName = "spoutsenders" }
                        )
                    )!;
                    names =
                    [
                        .. (senders.PropertyItems ?? []).Select(i => i.ItemName ?? string.Empty),
                    ];
                    return names.Contains(SenderName);
                },
                TimeSpan.FromMilliseconds(500)
            );
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"OBS lists the Spout senders [{string.Join(", ", names)}], not {SenderName}."
            );
        }

        return;
    }

    if (videoKind != "syphon-input")
    {
        return;
    }

    // OBS lists each server as "[app] name", with its uuid as the value.
    string? uuid = null;
    string[] listed = [];
    try
    {
        await UntilAsync(
            async () =>
            {
                var items = (
                    await obs.Inputs.GetInputPropertiesListPropertyItemsAsync(
                        new() { InputName = "Signal", PropertyName = "uuid" }
                    )
                )!;
                listed = [.. (items.PropertyItems ?? []).Select(i => i.ItemName ?? string.Empty)];
                uuid = (items.PropertyItems ?? [])
                    .Where(i =>
                        (i.ItemName ?? string.Empty).EndsWith(
                            "] " + SenderName,
                            StringComparison.Ordinal
                        )
                    )
                    .Select(i => i.ItemValue?.GetString())
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v));
                return uuid is not null;
            },
            TimeSpan.FromMilliseconds(500)
        );
    }
    catch (TimeoutException)
    {
        throw new InvalidOperationException(
            $"OBS lists the Syphon servers [{string.Join(", ", listed)}], not one named {SenderName}."
        );
    }

    await obs.Inputs.SetInputSettingsAsync(
        new()
        {
            InputName = "Signal",
            InputSettings = Json(new { uuid }),
            Overlay = true,
        }
    );
}

void DeleteCollection()
{
    string root =
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "obs-studio"
            )
        : OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "obs-studio"
            )
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "obs-studio"
        );
    foreach (
        string file in Directory.EnumerateFiles(
            Path.Combine(root, "basic", "scenes"),
            Collection + ".json*"
        )
    )
    {
        File.Delete(file);
    }
}

string? ObsPassword()
{
    string root =
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support"
            )
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    string config = Path.Combine(
        root,
        "obs-studio",
        "plugin_config",
        "obs-websocket",
        "config.json"
    );
    if (!File.Exists(config))
    {
        return null;
    }

    using var document = JsonDocument.Parse(File.ReadAllText(config));
    return
        document.RootElement.TryGetProperty("auth_required", out JsonElement required)
        && required.GetBoolean()
        ? document.RootElement.GetProperty("server_password").GetString()
        : null;
}

Process Start(
    string program,
    string[] arguments,
    Dictionary<string, string>? environment = null,
    bool redirect = true
)
{
    ProcessStartInfo info = new(program, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = redirect,
        RedirectStandardError = redirect,
    };
    foreach ((string key, string value) in environment ?? [])
    {
        info.Environment[key] = value;
    }

    return Process.Start(info) ?? throw new InvalidOperationException($"{program} did not start.");
}

// SIGTERM where there are signals, so the CLI's Syphon or PipeWire server retires; Windows has no signal
// for a console child, and Spout senders are found by name.
void Stop(Process process)
{
    if (process.HasExited)
    {
        return;
    }

    if (OperatingSystem.IsWindows())
    {
        process.Kill(entireProcessTree: true);
    }
    else
    {
        Process
            .Start("kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)])!
            .WaitForExit();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
        }
    }
}

string? Argument(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
