﻿using System;
using System.Threading.Tasks;
using GitHubSync;
using VerifyXunit;
using Xunit;
using Xunit.Abstractions;

public class PartsTests :
    VerifyBase
{
    [Fact]
    public Task Tree()
    {
        var parts = new Parts("SimonCropp/Fake", TreeEntryTargetType.Tree, "develop", "buildSupport");
        return Verify(parts);
    }

    [Fact]
    public Task Blob()
    {
        var parts = new Parts("SimonCropp/Fake", TreeEntryTargetType.Blob, "develop", "src/settings");

        return Verify(parts);
    }

    [Fact]
    public async Task CannotEscapeOutOfARootTree()
    {
        var parts = new Parts("SimonCropp/Fake", TreeEntryTargetType.Tree, "develop", null);

        await Verify(parts);
// ReSharper disable once UnusedVariable
        Assert.Throws<Exception>(() =>
        {
            var parent = parts.ParentTreePart;
        });
    }

    public PartsTests(ITestOutputHelper output) :
        base(output)
    {
    }
}