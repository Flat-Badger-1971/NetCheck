using System;
using NetCheck.Tools;
using Xunit;

namespace NetCheck.Tests.Tools;

public class ParseToolsTests
{
    [Fact]
    public void ParseJSON_ReturnsVersion_WhenValid()
    {
        string json = "{ \"sdk\": { \"version\": \"8.0.100\" } }";
        string version = ParseTools.ParseJSON(json);
        Assert.Equal("8.0.100", version);
    }

    [Fact]
    public void ParseJSON_ReturnsNull_WhenMissingVersion()
    {
        string json = "{ \"sdk\": { \"allowPrerelease\": true } }";
        string version = ParseTools.ParseJSON(json);
        Assert.Null(version);
    }

    [Fact]
    public void ParseJSON_Throws_OnInvalidJson()
    {
        Assert.Throws<ArgumentException>(() => ParseTools.ParseJSON("{ invalid"));
    }
}
