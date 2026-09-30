using System;
using System.Collections.Generic;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class LaunchModeTests
{
    private static Func<string, string?> Env(Dictionary<string, string> values) =>
        name => values.TryGetValue(name, out var value) ? value : null;

    [Fact]
    public void AnyDevOverrideBypassesTheMenuButTimeAloneDoesNot()
    {
        foreach (var variable in new[] { LaunchMode.ScriptVariable, LaunchMode.SpawnVariable, LaunchMode.LookVariable })
            Assert.True(LaunchMode.BypassesMenu(Env(new() { [variable] = "1,2,3" })));

        Assert.False(LaunchMode.BypassesMenu(Env(new() { ["VOXEL_TIME"] = "147" })));

        Assert.False(LaunchMode.BypassesMenu(Env(new()
        {
            [LaunchMode.ScriptVariable] = " ",
            [LaunchMode.SpawnVariable] = "\t",
            [LaunchMode.LookVariable] = "",
        })));

        Assert.False(LaunchMode.BypassesMenu(Env(new())));
    }
}
