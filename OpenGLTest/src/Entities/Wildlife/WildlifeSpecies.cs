using System;
using System.Numerics;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>The animals of the wild, in the order their entity names are listed
/// (<see cref="WildlifeSpecies.Names"/>) and their network kinds follow.</summary>
public enum WildlifeKind : byte
{
    Deer = 0,
    Boar = 1,
    DireBoar = 2,
    Crow = 3,
    Dragonfly = 4,
    CorpseBloom = 5,
    Centipede = 6,
}

/// <summary>
/// The one table of the wild animals: each <see cref="WildlifeKind"/>'s entity name, how to build its
/// <see cref="Animal"/>, its content contract and its body height, so the manager, the saves, the network
/// mirror, the renderer and the item debugger all take a new species from here instead of naming it.
/// </summary>
public static class WildlifeSpecies
{
    /// <summary>The entity file names (<c>assets/data/entities/&lt;name&gt;.json</c>), indexed by <see cref="WildlifeKind"/>.</summary>
    public static readonly string[] Names =
        [Deer.EntityName, Boar.EntityName, Boar.DireEntityName, Crow.EntityName, Dragonfly.EntityName,
         CorpseBloom.EntityName, Centipede.EntityName];

    /// <summary>The species an entity name belongs to, or null for any other entity.</summary>
    public static WildlifeKind? KindOf(string name)
    {
        for (var i = 0; i < Names.Length; i++)
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
                return (WildlifeKind)i;
        return null;
    }

    /// <summary>The entity name of <paramref name="kind"/>.</summary>
    public static string NameOf(WildlifeKind kind) => Names[(int)kind];

    /// <summary>A new animal of <paramref name="type"/>'s species with its body center at
    /// <paramref name="position"/>, or null when the type is not a wild animal.</summary>
    public static Animal? Create(EntityType type, Vector3 position, bool spawnIn = true) => KindOf(type.Name) switch
    {
        WildlifeKind.Deer => new Deer(type, position, spawnIn),
        WildlifeKind.Boar => new Boar(type, position, dire: false, spawnIn),
        WildlifeKind.DireBoar => new Boar(type, position, dire: true, spawnIn),
        WildlifeKind.Crow => new Crow(type, position, spawnIn),
        WildlifeKind.Dragonfly => new Dragonfly(type, position, spawnIn),
        WildlifeKind.CorpseBloom => new CorpseBloom(type, position, spawnIn),
        WildlifeKind.Centipede => new Centipede(type, position, spawnIn),
        _ => null,
    };

    /// <summary>Throws, naming the model file, unless <paramref name="type"/>'s model has the groups, clips
    /// and events its species plays; does nothing for an entity that is not a wild animal.</summary>
    public static void ValidateContent(EntityType type)
    {
        switch (KindOf(type.Name))
        {
            case WildlifeKind.Deer: Deer.ValidateContent(type); break;
            case WildlifeKind.Boar or WildlifeKind.DireBoar: Boar.ValidateContent(type); break;
            case WildlifeKind.Crow: Crow.ValidateContent(type); break;
            case WildlifeKind.Dragonfly: Dragonfly.ValidateContent(type); break;
            case WildlifeKind.CorpseBloom: CorpseBloom.ValidateContent(type); break;
            case WildlifeKind.Centipede: Centipede.ValidateContent(type); break;
        }
    }

    /// <summary>The body height of the named species (where a spawn puts its center above its feet), or 0
    /// for an entity that is not a wild animal.</summary>
    public static float HeightOf(string name) => KindOf(name) switch
    {
        WildlifeKind.Deer => WildlifeConfig.DeerHeight,
        WildlifeKind.Boar => WildlifeConfig.BoarHeight,
        WildlifeKind.DireBoar => WildlifeConfig.DireBoarHeight,
        WildlifeKind.Crow => WildlifeConfig.CrowHeight,
        WildlifeKind.Dragonfly => WildlifeConfig.DragonflyHeight,
        WildlifeKind.CorpseBloom => WildlifeConfig.BloomHeight,
        WildlifeKind.Centipede => WildlifeConfig.CentipedeHeight,
        _ => 0f,
    };

    /// <summary>Whether <paramref name="kind"/> is a flyer, which spawns in the air over its column.</summary>
    public static bool Flies(WildlifeKind kind) => kind is WildlifeKind.Crow or WildlifeKind.Dragonfly;

    /// <summary>Half the horizontal edge of the named species' body, or 0 for an entity that is not a wild animal.</summary>
    public static float HalfExtentOf(string name) => KindOf(name) switch
    {
        WildlifeKind.Deer => WildlifeConfig.DeerHalfExtent,
        WildlifeKind.Boar => WildlifeConfig.BoarHalfExtent,
        WildlifeKind.DireBoar => WildlifeConfig.DireBoarHalfExtent,
        WildlifeKind.Crow => WildlifeConfig.CrowHalfExtent,
        WildlifeKind.Dragonfly => WildlifeConfig.DragonflyHalfExtent,
        WildlifeKind.CorpseBloom => WildlifeConfig.BloomHalfExtent,
        WildlifeKind.Centipede => WildlifeConfig.CentipedeHalfExtent,
        _ => 0f,
    };
}
