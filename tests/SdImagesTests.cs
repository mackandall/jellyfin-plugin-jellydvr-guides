using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyDvrGuides.SchedulesDirect;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyDvrGuides.Tests;

public sealed class SdImagesTests : IDisposable
{
    private readonly TestPaths _paths = new();
    private readonly SdImages _images;
    private readonly string _folder;
    private readonly Dictionary<string, string> _listed = new();
    private readonly List<string> _asked = new();
    private readonly List<string> _downloaded = new();

    public SdImagesTests()
    {
        var client = new SdClient(NullLogger<SdClient>.Instance);
        var updater = new SdUpdater(client, _paths.Paths, NullLogger<SdUpdater>.Instance);
        _images = new SdImages(client, updater, NullLogger<SdImages>.Instance);
        _folder = Path.Combine(updater.Root, "images");
        _images.Pictures = (_, id, _) =>
        {
            _asked.Add(id);
            return Task.FromResult(_listed.TryGetValue(id, out var uri)
                ? new JsonArray(new JsonObject { ["uri"] = uri, ["aspect"] = "4x3", ["width"] = "720" })
                : null);
        };
        _images.Download = (_, uri, _) =>
        {
            _downloaded.Add(uri);
            return Task.FromResult<(byte[] Bytes, string Type)?>((System.Text.Encoding.UTF8.GetBytes(uri), "image/jpeg"));
        };
    }

    public void Dispose() => _paths.Dispose();

    private string Jpg => Path.Combine(_folder, "SH00069019.jpg");

    private async Task<string?> Show()
    {
        var path = await _images.GetAsync("SH00069019", "EP000690193465", CancellationToken.None);
        await _images.RechecksDone();
        return path;
    }

    [Fact]
    public void IsDue_RespectsDaysAndNever()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(SdImages.IsDue(now.AddDays(-15), 14, now));
        Assert.False(SdImages.IsDue(now.AddDays(-13), 14, now));
        Assert.False(SdImages.IsDue(now.AddDays(-400), 0, now));
    }

    [Fact]
    public async Task FirstShow_AsksWithShowIdAndKeepsAddress()
    {
        _listed["SH00069019"] = "assets/a.jpg";
        Assert.Equal(Jpg, await Show());
        Assert.Equal(new[] { "SH00069019" }, _asked);
        Assert.Equal("assets/a.jpg", File.ReadAllText(Path.Combine(_folder, "SH00069019.uri")));
    }

    [Fact]
    public async Task FreshPicture_IsNotAskedAboutAgain()
    {
        _listed["SH00069019"] = "assets/a.jpg";
        await Show();
        _asked.Clear();
        await Show();
        Assert.Empty(_asked);
        Assert.Single(_downloaded);
    }

    [Fact]
    public async Task OldPicture_SameArt_IsNotDownloadedAgain()
    {
        _listed["SH00069019"] = "assets/a.jpg";
        await Show();
        File.SetLastWriteTimeUtc(Jpg, DateTime.UtcNow.AddDays(-20));
        Assert.Equal(Jpg, await Show());
        Assert.Single(_downloaded);
        Assert.True(File.GetLastWriteTimeUtc(Jpg) > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task OldPicture_NewArt_IsReplaced()
    {
        _listed["SH00069019"] = "assets/a.jpg";
        await Show();
        File.SetLastWriteTimeUtc(Jpg, DateTime.UtcNow.AddDays(-20));
        _listed["SH00069019"] = "assets/b.jpg";
        // The kept picture is served straight away; the new one replaces it in the background.
        Assert.Equal(Jpg, await Show());
        Assert.Equal(new[] { "assets/a.jpg", "assets/b.jpg" }, _downloaded);
        Assert.Equal("assets/b.jpg", File.ReadAllText(Jpg));
        Assert.Equal("assets/b.jpg", File.ReadAllText(Path.Combine(_folder, "SH00069019.uri")));
    }

    [Fact]
    public async Task OldPictureWithoutAddress_JustNotesIt()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Jpg, "kept before addresses were recorded");
        File.SetLastWriteTimeUtc(Jpg, DateTime.UtcNow.AddDays(-20));
        _listed["SH00069019"] = "assets/a.jpg";
        await Show();
        Assert.Empty(_downloaded);
        Assert.Equal("kept before addresses were recorded", File.ReadAllText(Jpg));
        Assert.Equal("assets/a.jpg", File.ReadAllText(Path.Combine(_folder, "SH00069019.uri")));
    }

    [Fact]
    public async Task OldPicture_NoneListedNow_IsKept()
    {
        _listed["SH00069019"] = "assets/a.jpg";
        await Show();
        File.SetLastWriteTimeUtc(Jpg, DateTime.UtcNow.AddDays(-20));
        _listed.Clear();
        Assert.Equal(Jpg, await Show());
        Assert.Equal("assets/a.jpg", File.ReadAllText(Jpg));
    }

    [Fact]
    public async Task PictureFound_RemovesOldNoPictureMarker()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "SH00069019.none"), string.Empty);
        _listed["SH00069019"] = "assets/a.jpg";
        Assert.Equal(Jpg, await Show());
        Assert.False(File.Exists(Path.Combine(_folder, "SH00069019.none")));
    }
}
