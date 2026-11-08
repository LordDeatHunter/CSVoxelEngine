namespace VoxelEngine.WorldGen.Structures;

/// <summary>Which concentric ring of a battle tower a column belongs to, from outside in (<see cref="TowerShell.RingOf"/>).</summary>
internal enum TowerRing
{
    /// <summary>Beyond the tower: nothing is written.</summary>
    Outside,

    /// <summary>The short entrance walkway outside the wall, only in front of the entrance.</summary>
    Apron,

    /// <summary>The thick outer wall, crenellated over the roof.</summary>
    OuterWall,

    /// <summary>The solid ring the spiral stair's tube winds through.</summary>
    Corridor,

    /// <summary>The thin wall between the stair and the rooms, with one doorway per floor.</summary>
    InnerWall,

    /// <summary>The floor rooms, slab over slab.</summary>
    Room
}
