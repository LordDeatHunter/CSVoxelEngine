using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The dedicated server's entry point: loads content headlessly, asks which saved world to host (or
/// creates a new one), asks for a port, then runs a headless <see cref="World"/> with a
/// <see cref="GameServer"/> until Ctrl+C or a typed <c>stop</c>. The world is saved on shutdown
/// exactly like a game world, so a server session persists its edits. <c>--allow-creative</c> lets
/// every player turn creative on (<see cref="GameServer.AllowCreative"/>). Without it only the
/// operators may (<see cref="GameServer.Operators"/>): the players named by each <c>--op &lt;name&gt;</c>
/// argument (repeatable) and by the hosted world folder's <see cref="OperatorList.FileName"/>, a
/// JSON array of names; everyone else stays in survival. The world's terrain comes from the
/// <see cref="IWorldSource"/> named by <c>--source &lt;name&gt;</c>, else by the world's
/// <c>world.json</c> <c>source</c> field (<see cref="WorldInfo.Source"/>), else worldgen
/// (<see cref="WorldSources"/>): <c>imported</c> reads the world folder's
/// <see cref="ImportedMapSource.DirectoryName"/> map and generates everything outside it. A source
/// that cannot be built is refused before the world folder is locked.
/// </summary>
public static class ServerProgram
{
    private const string SourceFlag = "--source";

    public static void Main(string[] args)
    {
        var allowCreative = WorldGenerationConfig.ServerAllowCreative
            || Array.Exists(args, a => a.Equals("--allow-creative", StringComparison.OrdinalIgnoreCase));
        var sourceArg = ArgValue(args, SourceFlag);
        if (sourceArg == null && Array.Exists(args, a => a.Equals(SourceFlag, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"{SourceFlag} needs a name ({string.Join(", ", WorldSources.Names)}).");
            return;
        }

        Console.WriteLine("CSVoxelEngine server");
        Console.WriteLine();

        var assets = AssetRoot.Find();
        // Content and models are opened by the relative path "assets/..." (FileUtils), exactly like
        // the game, so the server runs from the directory that holds assets/.
        Directory.SetCurrentDirectory(Path.GetDirectoryName(assets)!);
        ContentRegistry.Load(assets);
        TextureAtlas.RegisterTilesWithoutUpload(Path.Combine(assets, "textures", "blocks"));
        ContentRegistry.BakeModels();

        var store = new WorldStore(WorldStore.DefaultRoot);
        var world = PromptWorld(store);
        if (world == null)
        {
            Console.WriteLine("No world selected; exiting.");
            return;
        }

        var port = PromptPort();

        if (WorldSeed.WorldIsLive)
        {
            Console.WriteLine("A world is already live in this process; cannot start a second.");
            return;
        }

        // Resolved before the save store takes the folder's lock, so a refused source leaves the
        // folder untouched
        var sourceName = sourceArg ?? world.Source;
        IWorldSource source;
        try
        {
            source = WorldSources.Create(sourceName, world.Directory);
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            Console.WriteLine($"Cannot use world source '{sourceName}': {e.Message}");
            return;
        }

        ChunkSaveStore save;
        try
        {
            // The store takes the folder's session lock: a world the game or another server still
            // has open is refused here, before anything could race its writer.
            save = new ChunkSaveStore(world.Directory);
        }
        catch (IOException e)
        {
            Console.WriteLine("That world is open in another session (the game or another server); close it first.");
            Console.WriteLine($"  ({e.Message})");
            return;
        }

        // Read through the store, which owns the folder (and validates the file name): the command
        // line's operators plus the world's own list
        var operators = OperatorList.FromArgs(args);
        operators.AddAll(OperatorList.Parse(save.ReadFile(OperatorList.FileName)));

        WorldSeed.Apply(world.Seed);
        var gameWorld = new World(null!, null!, null!, null!, null!, null!, null, save, serverMode: true,
            worldSource: source);
        using var server = new GameServer(gameWorld, world.Name, world.Seed, port)
        {
            AllowCreative = allowCreative,
            Operators = operators,
        };
        try
        {
            server.Start();
        }
        catch (SocketException e)
        {
            // Most often the port is already in use. Nothing was served yet, but the world was
            // opened, so it is closed cleanly before exiting.
            Console.WriteLine($"Could not listen on port {port}: {e.Message}");
            gameWorld.SaveAndDispose();
            Log.Shutdown();
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Serving '{world.Name}' (seed {world.Seed}) on port {server.BoundPort} (IPv4 and IPv6).");
        Console.WriteLine(SourceLine(source));
        Console.WriteLine(CreativeLine(allowCreative, operators));
        Console.WriteLine("Type 'stop' or press Ctrl+C to shut down.");

        var stop = new ManualResetEventSlim(false);
        // Set by the main loop's finally once the world is saved, so a shutdown signal can wait
        // for the save before letting the process end.
        var stopped = new ManualResetEventSlim(false);
        var shutdownWait = TimeSpan.FromSeconds(WorldGenerationConfig.ServerShutdownWaitSeconds);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        // A process exit (a service stop, the last foreground thread ending) and the POSIX
        // terminate/hang-up signals (a closed terminal, `kill`, a container stop; on Windows the
        // console close and shutdown events) all save first.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            stop.Set();
            stopped.Wait(shutdownWait);
        };
        var signals = new List<PosixSignalRegistration>();
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            try
            {
                signals.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    ctx.Cancel = true;
                    stop.Set();
                    stopped.Wait(shutdownWait);
                }));
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        var input = new Thread(() => ReadStop(stop)) { IsBackground = true, Name = "server-stdin" };
        input.Start();

        try
        {
            var last = Stopwatch.GetTimestamp();
            while (!stop.IsSet)
            {
                var now = Stopwatch.GetTimestamp();
                var delta = (now - last) / (double)Stopwatch.Frequency;
                last = now;
                delta = Math.Clamp(delta, 0.0, 0.25);

                gameWorld.Update(delta, null);
                server.Pump(delta);
                Thread.Sleep(1);
            }
        }
        finally
        {
            // Runs on a clean stop and on an unexpected exception alike, so the world is saved
            // either way.
            Console.WriteLine("Shutting down...");
            server.Stop();
            gameWorld.SaveAndDispose();
            Log.Shutdown();
            Console.WriteLine("Server stopped.");
            stopped.Set();
            foreach (var registration in signals)
                registration.Dispose();
        }
    }

    private static void ReadStop(ManualResetEventSlim stop)
    {
        try
        {
            string? line;
            while (!stop.IsSet && (line = Console.ReadLine()) != null)
            {
                if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)
                    || line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    stop.Set();
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
    }

    private static WorldInfo? PromptWorld(WorldStore store)
    {
        while (true)
        {
            var worlds = store.List();
            Console.WriteLine(worlds.Count == 0 ? "No saved worlds yet." : "Saved worlds:");
            for (var i = 0; i < worlds.Count; i++)
                Console.WriteLine($"  {i + 1}. {worlds[i].Name} (seed {worlds[i].Seed})"
                    + (IsHostable(worlds[i]) ? "" : " (old format, cannot be hosted)"));
            Console.Write("Choose a world number, or N for a new world: ");

            var line = Console.ReadLine();
            if (line == null)
                return null;
            line = line.Trim();

            if (line.Equals("n", StringComparison.OrdinalIgnoreCase))
                return CreateWorld(store);
            if (int.TryParse(line, out var index) && index >= 1 && index <= worlds.Count)
            {
                var chosen = worlds[index - 1];
                if (IsHostable(chosen))
                    return chosen;
                // The same refusal the main menu makes: a world in another save format would be
                // half-loaded and its chunks regenerated over, so it is never opened.
                Console.WriteLine($"'{chosen.Name}' was saved by an older version (format {chosen.Version}, " +
                                  $"this build reads {WorldStore.CurrentVersion}); it cannot be hosted.");
                Console.WriteLine();
                continue;
            }
            if (line.Length == 0 && worlds.Count == 0)
                return CreateWorld(store);

            Console.WriteLine("Enter a listed number or N.");
            Console.WriteLine();
        }
    }

    // The value after a flag (matched case-insensitively, first occurrence), or null when the flag
    // is absent, is the last argument or is followed by another flag
    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            return null;
        return args[i + 1];
    }

    // The startup line saying where the world's terrain comes from
    private static string SourceLine(IWorldSource source) => source is ImportedMapSource imported
        ? $"World source: {WorldSources.Imported} ({imported.MapDirectory}; generated outside the imported columns)"
        : $"World source: {WorldSources.Generated}";

    // The startup line saying who may be creative: everyone, the listed operators, or nobody
    private static string CreativeLine(bool allowCreative, OperatorList operators)
    {
        if (allowCreative)
            return "Creative mode is allowed for everyone.";
        if (operators.Count > 0)
            return $"Creative mode is off; operators: {string.Join(", ", operators.Names)}";
        return "Creative mode is off; operators: none; start with --allow-creative or --op <name>, "
               + $"or list names in the world folder's {OperatorList.FileName}.";
    }

    // Whether this build reads the world's save format (MainMenuController's PLAY gate)
    private static bool IsHostable(WorldInfo world) => world.Version == WorldStore.CurrentVersion;

    private static WorldInfo CreateWorld(WorldStore store)
    {
        Console.Write($"World name [{WorldNames.DefaultName}]: ");
        var name = Console.ReadLine();

        Console.Write("Seed (empty for random): ");
        var seedText = Console.ReadLine();

        var seed = WorldSeedParser.Parse(seedText, Random.Shared.Next);
        return store.Create(name, seed);
    }

    private static int PromptPort()
    {
        while (true)
        {
            Console.Write($"Port [{ServerAddress.DefaultPort}]: ");
            var line = Console.ReadLine();
            if (line == null)
                return ServerAddress.DefaultPort;
            line = line.Trim();
            if (line.Length == 0)
                return ServerAddress.DefaultPort;
            if (int.TryParse(line, out var port) && port is >= ServerAddress.MinPort and <= ServerAddress.MaxPort)
                return port;

            Console.WriteLine($"Enter a port {ServerAddress.MinPort}-{ServerAddress.MaxPort}.");
        }
    }
}
