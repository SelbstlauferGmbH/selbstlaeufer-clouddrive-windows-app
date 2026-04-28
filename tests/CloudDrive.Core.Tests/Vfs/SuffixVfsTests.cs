using System.Text;
using CloudDrive.Core.Vfs;
using Shouldly;

namespace CloudDrive.Core.Tests.Vfs;

public class SuffixVfsTests
{
    [Fact]
    [Trait("Category", "Vfs")]
    public async Task CreateHydrateDehydrate_RoundTripsLogicalFile()
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs();
        await vfs.RegisterAsync(new VfsRegistration(tempDir.Path, "test"), CancellationToken.None);
        await vfs.ConnectAsync(CancellationToken.None);

        var localPath = Path.Combine(tempDir.Path, "docs", "report.txt");
        var metadata = new VfsMetadata(
            "file-1",
            "/docs/report.txt",
            "etag-1",
            LogicalSize: 11,
            MTimeUtc: DateTime.UtcNow,
            IsDirectory: false);

        (await vfs.CreatePlaceholderAsync(localPath, metadata, CancellationToken.None)).Succeeded.ShouldBeTrue();
        File.Exists(localPath).ShouldBeFalse();
        File.Exists(localPath + SuffixVfs.PlaceholderSuffix).ShouldBeTrue();

        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello world"));
        (await vfs.HydrateAsync(localPath, content, 11, null, CancellationToken.None)).Succeeded.ShouldBeTrue();
        File.Exists(localPath).ShouldBeTrue();
        File.Exists(localPath + SuffixVfs.PlaceholderSuffix).ShouldBeFalse();

        var hydrated = await vfs.GetPlaceholderInfoAsync(localPath, CancellationToken.None);
        hydrated.ShouldNotBeNull();
        hydrated.HydrationState.ShouldBe(VfsHydrationState.Hydrated);
        hydrated.FileId.ShouldBe("file-1");

        (await vfs.DehydrateAsync(localPath, CancellationToken.None)).Succeeded.ShouldBeTrue();
        File.Exists(localPath).ShouldBeFalse();
        File.Exists(localPath + SuffixVfs.PlaceholderSuffix).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Vfs")]
    public async Task EnumerateChildren_HidesSidecarFiles()
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs(tempDir.Path);

        var localPath = Path.Combine(tempDir.Path, "report.txt");
        await vfs.CreatePlaceholderAsync(
            localPath,
            new VfsMetadata("file-1", "/report.txt", "etag", 4, DateTime.UtcNow, IsDirectory: false),
            CancellationToken.None);

        var entries = await vfs.EnumerateChildrenAsync(tempDir.Path, depth: 1, CancellationToken.None);

        entries.Count.ShouldBe(1);
        entries[0].LocalPath.ShouldBe(localPath);
        entries[0].Placeholder.ShouldNotBeNull();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CloudDrive.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
