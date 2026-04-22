using CloudDrive.Core.SyncRoot;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncRoot;

public class ExplorerVisualStateInfoTests
{
    [Theory]
    [InlineData(ExplorerVisualState.Connected, null)]
    [InlineData(ExplorerVisualState.Disconnected, "CloudDrive is offline \u2014 cannot reach server")]
    public void GetStatusMessage_ReturnsCorrectMessage(ExplorerVisualState state, string? expected)
    {
        ExplorerVisualStateInfo.GetStatusMessage(state).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ExplorerVisualState.Connected, @"%SystemRoot%\system32\imageres.dll,-1043")]
    [InlineData(ExplorerVisualState.Disconnected, @"%SystemRoot%\system32\imageres.dll,-1405")]
    public void GetIconResource_ReturnsCorrectResource(ExplorerVisualState state, string expected)
    {
        ExplorerVisualStateInfo.GetIconResource(state).ShouldBe(expected);
    }

    [Fact]
    public void AllStates_HaveDistinctIconResources()
    {
        var states = Enum.GetValues<ExplorerVisualState>();
        var icons = states.Select(ExplorerVisualStateInfo.GetIconResource).ToList();

        icons.Distinct().Count().ShouldBe(states.Length,
            "Each visual state should map to a distinct icon resource");
    }

    [Fact]
    public void AllNonConnectedStates_HaveDistinctMessages()
    {
        var statesWithMessages = Enum.GetValues<ExplorerVisualState>()
            .Where(s => s != ExplorerVisualState.Connected)
            .ToList();

        var messages = statesWithMessages
            .Select(ExplorerVisualStateInfo.GetStatusMessage)
            .ToList();

        messages.ShouldAllBe(m => m != null, "Non-Connected states should have a message");
        messages.Distinct().Count().ShouldBe(statesWithMessages.Count,
            "Each non-Connected state should have a distinct message");
    }
}
