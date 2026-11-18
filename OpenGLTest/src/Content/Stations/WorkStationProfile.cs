namespace VoxelEngine.Content.Stations;

/// <summary>
/// How one kind of work station behaves, compiled from its block's <c>workstation</c> object
/// (<see cref="BlockType.WorkStation"/>): the anvil, the fleshing beam and the leather table are all one mechanic. A stack
/// of one input lies on it, a recipe from its folder is picked for the stack with the station's tool, and that tool's
/// hits work it (<see cref="Simulation.WorkStations.WorkStationManager"/>).
/// </summary>
/// <param name="ToolTag">The item tag whose members pick and hit on it (<c>tool</c>: <c>hammer</c>, <c>knife</c>).</param>
/// <param name="Recipes">The recipe folder under <c>recipes/</c> its work recipes come from (<c>recipes</c>:
/// <c>forge</c>, <c>scrape</c>, <c>cut</c>).</param>
/// <param name="SurfaceY">Height of its work surface in the cell, 0..1, where the stack is drawn (<c>surface</c>).</param>
/// <param name="Stack">The most of one input it stacks when that is more than its largest recipe takes (<c>stack</c>:
/// a fleshing beam holds several hides it scrapes one after another); 0 for no more than the largest recipe.</param>
public sealed record WorkStationProfile(string ToolTag, string Recipes, float SurfaceY, int Stack);
