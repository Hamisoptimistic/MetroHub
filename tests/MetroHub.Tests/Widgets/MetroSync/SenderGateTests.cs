using MetroHub.Tests.Widgets.MetroSync.TestKit;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

public sealed class SenderGateTests
{
    private const long AllowedUserId = 4242;
    private const long StrangerUserId = 9999;

    private readonly SenderGate _gate = new(AllowedUserId);

    [Fact]
    public void LegitPrivateMessage_Passes()
    {
        var update = UpdateBuilder.ToRaw(1, UpdateBuilder.Text(AllowedUserId, "hello"));
        Assert.True(_gate.IsAllowed(update));
    }

    [Fact]
    public void StrangerSender_IsDropped()
    {
        var update = UpdateBuilder.ToRaw(1, UpdateBuilder.Text(StrangerUserId, "hello"));
        Assert.False(_gate.IsAllowed(update));
    }

    [Fact]
    public void NonPrivateChat_IsDropped()
    {
        var groupUpdate = UpdateBuilder.ToRaw(1, UpdateBuilder.Text(AllowedUserId, "hello", chatType: "group", chatId: -100123));
        Assert.False(_gate.IsAllowed(groupUpdate));

        var supergroupUpdate = UpdateBuilder.ToRaw(2, UpdateBuilder.Text(AllowedUserId, "hello", chatType: "supergroup", chatId: -100456));
        Assert.False(_gate.IsAllowed(supergroupUpdate));
    }

    [Fact]
    public void ChatIdAndFromIdMismatch_IsDropped()
    {
        var update1 = UpdateBuilder.ToRaw(1, UpdateBuilder.Text(StrangerUserId, "pose", chatType: "private", chatId: AllowedUserId));
        Assert.False(_gate.IsAllowed(update1));

        var update2 = UpdateBuilder.ToRaw(2, UpdateBuilder.Text(AllowedUserId, "pose", chatType: "private", chatId: StrangerUserId));
        Assert.False(_gate.IsAllowed(update2));
    }

    [Fact]
    public void ChannelPostAndEditedMessages_AreDropped()
    {
        var channelUpdate = UpdateBuilder.ToRaw(1, UpdateBuilder.ChannelPost(-100999));
        Assert.False(_gate.IsAllowed(channelUpdate));

        var editedUpdate = UpdateBuilder.ToRaw(2, UpdateBuilder.EditedText(AllowedUserId, "edited"));
        Assert.False(_gate.IsAllowed(editedUpdate));
    }

    [Fact]
    public void HostileCorpus_AllDropped()
    {
        var corpus = UpdateBuilder.HostileCorpus(AllowedUserId, StrangerUserId);
        for (var i = 0; i < corpus.Count; i++)
        {
            var update = UpdateBuilder.ToRaw(i + 1, corpus[i]);
            Assert.False(_gate.IsAllowed(update));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-4242)]
    public void Construct_WithNegativeOrZeroUserId_ThrowsArgumentOutOfRangeException(long invalidId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SenderGate(invalidId));
    }
}
