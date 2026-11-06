using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MetroHub.Tests;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class MetroSyncSecurityTests
{
    [Fact]
    public void SaveAndLoad_RoundTrip_Succeeds()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        const string botToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
        const long userId = 987654321;

        security.SaveCredentials(botToken, userId);

        bool success = security.TryLoadCredentials(out var creds);

        Assert.True(success);
        Assert.NotNull(creds);
        Assert.Equal(botToken, creds.BotToken);
        Assert.Equal(userId, creds.AllowedUserId);
    }

    [Fact]
    public void SaveCredentials_PlaintextTokenNeverOnDisk()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        const string botToken = "SECRET_TELEGRAM_TOKEN_XYZ_789";
        security.SaveCredentials(botToken, 12345);

        Assert.True(File.Exists(security.CredentialsPath));
        byte[] rawBytes = File.ReadAllBytes(security.CredentialsPath);
        string fileContent = Encoding.UTF8.GetString(rawBytes);

        Assert.DoesNotContain(botToken, fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TryLoadCredentials_MissingFile_ReturnsFalse()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        bool success = security.TryLoadCredentials(out var creds);

        Assert.False(success);
        Assert.Null(creds);
    }

    [Fact]
    public void TryLoadCredentials_EmptyFile_ReturnsFalse()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        Directory.CreateDirectory(sandbox.Root);
        File.WriteAllBytes(security.CredentialsPath, Array.Empty<byte>());

        bool success = security.TryLoadCredentials(out var creds);

        Assert.False(success);
        Assert.Null(creds);
    }

    [Fact]
    public void TryLoadCredentials_CorruptOrTamperedBytes_ReturnsFalseWithoutThrowing()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        Directory.CreateDirectory(sandbox.Root);
        // Write random garbage bytes that cannot be un-protected with DPAPI
        byte[] garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(security.CredentialsPath, garbage);

        bool success = security.TryLoadCredentials(out var creds);

        Assert.False(success);
        Assert.Null(creds);
    }

    [Fact]
    public void Delete_RemovesCredentialsFile()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        security.SaveCredentials("dummy-token", 100);
        Assert.True(File.Exists(security.CredentialsPath));

        security.Delete();

        Assert.False(File.Exists(security.CredentialsPath));
        Assert.False(security.TryLoadCredentials(out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveCredentials_InvalidToken_ThrowsArgumentException(string? token)
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        Assert.ThrowsAny<ArgumentException>(() => security.SaveCredentials(token!, 123));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-99999)]
    public void SaveCredentials_InvalidUserId_ThrowsArgumentOutOfRangeException(long userId)
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        Assert.Throws<ArgumentOutOfRangeException>(() => security.SaveCredentials("token", userId));
    }

    [Fact]
    public void Delete_CleansUpOrphanedTmpFiles()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        security.SaveCredentials("token", 100);
        string orphanTmp = Path.Combine(sandbox.Root, "credentials.dat.orphan.tmp");
        File.WriteAllText(orphanTmp, "junk");

        security.Delete();

        Assert.False(File.Exists(security.CredentialsPath));
        Assert.False(File.Exists(orphanTmp));
    }

    [Fact]
    public async Task SaveAndLoad_ConcurrentAccess_ThreadSafe()
    {
        using var sandbox = new WidgetTestSandbox();
        var security = new MetroSyncSecurity(sandbox.Root);

        var tasks = new List<Task>();
        for (int i = 1; i <= 20; i++)
        {
            int id = i;
            tasks.Add(Task.Run(() =>
            {
                security.SaveCredentials($"token_{id}", id);
                security.TryLoadCredentials(out _);
            }));
        }

        await Task.WhenAll(tasks);

        bool success = security.TryLoadCredentials(out var finalCreds);
        Assert.True(success);
        Assert.NotNull(finalCreds);
        Assert.True(finalCreds.AllowedUserId >= 1 && finalCreds.AllowedUserId <= 20);
    }
}
