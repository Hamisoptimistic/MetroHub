using System.Collections.Generic;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public sealed class HubStateTests
{
    [Fact]
    public void HubState_DefaultsToVisible()
    {
        HubState.SetVisibility(true);
        Assert.True(HubState.IsVisible);
        Assert.False(HubState.IsHidden);
    }

    [Fact]
    public void HubState_SetVisibility_TogglesStateAndFiresEvent()
    {
        var transitions = new List<bool>();
        HubState.VisibilityChanged += isVisible => transitions.Add(isVisible);

        try
        {
            HubState.SetVisibility(true); // already true, no event
            Assert.Empty(transitions);

            HubState.SetVisibility(false);
            Assert.False(HubState.IsVisible);
            Assert.True(HubState.IsHidden);
            Assert.Single(transitions);
            Assert.False(transitions[0]);

            HubState.SetVisibility(true);
            Assert.True(HubState.IsVisible);
            Assert.False(HubState.IsHidden);
            Assert.Equal(2, transitions.Count);
            Assert.True(transitions[1]);
        }
        finally
        {
            // Restore default
            HubState.SetVisibility(true);
        }
    }
}
