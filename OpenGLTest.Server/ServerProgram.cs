using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The dedicated server's entry point: loads content headlessly, asks which saved world to host (or
/// creates a new one), asks for a port, then runs a headless <see cref="World"/> with a
/// <see cref="GameServer"/> until Ctrl+C or a typed <c>stop</c>. The world is saved on shutdown
/// exactly like a game world, so a server session persists its edits.
/// </summary>
public static class ServerProgram
{
    public static void Main()
    {
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

        WorldSeed.Apply(world.Seed);
        var save = new ChunkSaveStore(world.Directory);
        var gameWorld = new World(null!, null!, null!, null!, null!, null!, null, save, serverMode: true);
        using var server = new GameServer(gameWorld, world.Name, world.Seed, port);
        server.Start();
        Console.WriteLine();
        Console.WriteLine($"Serving '{world.Name}' (seed {world.Seed}) on 0.0.0.0:{port}.");
        Console.WriteLine("Type 'stop' or press Ctrl+C to shut down.");

        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        var input = new Thread(() => ReadStop(stop)) { IsBackground = true, Name = "server-stdin" };
        input.Start();

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

        Console.WriteLine("Shutting down...");
        server.Stop();
        gameWorld.SaveAndDispose();
        Log.Shutdown();
        Console.WriteLine("Server stopped.");
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
                Console.WriteLine($"  {i + 1}. {worlds[i].Name} (seed {worlds[i].Seed})");
            Console.Write("Choose a world number, or N for a new world: ");

            var line = Console.ReadLine();
            if (line == null)
                return null;
            line = line.Trim();

            if (line.Equals("n", StringComparison.OrdinalIgnoreCase))
                return CreateWorld(store);
            if (int.TryParse(line, out var index) && index >= 1 && index <= worlds.Count)
                return worlds[index - 1];
            if (line.Length == 0 && worlds.Count == 0)
                return CreateWorld(store);

            Console.WriteLine("Enter a listed number or N.");
            Console.WriteLine();
        }
    }

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
