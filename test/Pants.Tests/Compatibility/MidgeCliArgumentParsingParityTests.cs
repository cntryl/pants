using Cntryl.Pants;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeCliArgumentParsingParityTests
{
    [Fact]
    public void ShouldPreserveJsonFormatGivenUsageErrorWhenFlagFollowsError()
    {
        var command = PantsCliCommandLineParser.Parse(["verify", "--bad", "--json"]);

        Assert.False(command.IsHelp);
        Assert.Equal(PantsCliOutputFormat.Json, command.Format);
        Assert.Equal(PantsCliOutputFormat.Json, command.Failure?.Format);
        Assert.Contains("unknown flag '--bad'", command.Failure?.Message, StringComparison.Ordinal);
    }
}
