using CloudDrive.Core.Helpers;

namespace CloudDrive.Core.Tests.Helpers;

public class PathMapperTests
{
    [Fact]
    public void NormalizedPathToRemotePath_Root_ReturnsBasePathWithoutTrailingSlash()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        
        // Act
        var result = mapper.NormalizedPathToRemotePath("\\CloudDrive");
        
        // Assert
        Assert.Equal("/instance_399", result);
        Assert.False(result.EndsWith("/"), "Root path should not have trailing slash");
    }

    [Fact]
    public void NormalizedPathToRemotePath_Subfolder_ReturnsCorrectPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        
        // Act
        var result = mapper.NormalizedPathToRemotePath("\\CloudDrive\\Documents");
        
        // Assert
        Assert.Equal("/instance_399/Documents", result);
    }

    [Fact]
    public void NormalizedPathToRemotePath_DeepSubfolder_ReturnsCorrectPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        
        // Act
        var result = mapper.NormalizedPathToRemotePath("\\CloudDrive\\Documents\\Work\\Project");
        
        // Assert
        Assert.Equal("/instance_399/Documents/Work/Project", result);
    }

    [Fact]
    public void ToRemotePath_Root_ReturnsBasePathWithoutDotSegment()
    {
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");

        var result = mapper.ToRemotePath("C:\\CloudDrive");

        Assert.Equal("/instance_399", result);
    }

    [Fact]
    public void ToRemotePath_RootWithSlashBase_ReturnsSlash()
    {
        var mapper = new PathMapper("C:\\CloudDrive", "/");

        var result = mapper.ToRemotePath("C:\\CloudDrive");

        Assert.Equal("/", result);
    }

    [Fact]
    public void ToRemotePath_LocalPath_ReturnsCorrectPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        
        // Act
        var result = mapper.ToRemotePath("C:\\CloudDrive\\Documents\\file.txt");
        
        // Assert
        Assert.Equal("/instance_399/Documents/file.txt", result);
    }

    [Fact]
    public void ToLocalPath_RemotePath_ReturnsCorrectPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        
        // Act
        var result = mapper.ToLocalPath("/instance_399/Documents/file.txt");
        
        // Assert
        Assert.Equal("C:\\CloudDrive\\Documents\\file.txt", result);
    }

    [Fact]
    public void RoundTrip_LocalToRemoteToLocal_PreservesPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        var localPath = "C:\\CloudDrive\\Documents\\Work\\file.txt";
        
        // Act
        var remotePath = mapper.ToRemotePath(localPath);
        var roundTripPath = mapper.ToLocalPath(remotePath);
        
        // Assert
        Assert.Equal(localPath, roundTripPath);
    }

    [Fact]
    public void RoundTrip_RemoteToLocalToRemote_PreservesPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        var remotePath = "/instance_399/Documents/Work/file.txt";
        
        // Act
        var localPath = mapper.ToLocalPath(remotePath);
        var roundTripPath = mapper.ToRemotePath(localPath);
        
        // Assert
        Assert.Equal(remotePath, roundTripPath);
    }

    [Fact]
    public void ToRelativePath_LocalPath_ReturnsRelativePath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        
        // Act
        var result = mapper.ToRelativePath("C:\\CloudDrive\\Documents\\file.txt");
        
        // Assert
        Assert.Equal("Documents\\file.txt", result);
    }

    [Fact]
    public void GetSyncRootPath_ReturnsSyncRootPath()
    {
        // Arrange
        var expectedRoot = "C:\\CloudDrive";
        var mapper = new PathMapper(expectedRoot, "/instance_399");
        
        // Act
        var result = mapper.GetSyncRootPath();
        
        // Assert
        Assert.Equal(expectedRoot, result);
    }

    [Fact]
    public void RemotePathToNormalizedPath_Root_ReturnsCorrectPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        
        // Act
        var result = mapper.RemotePathToNormalizedPath("/instance_399");
        
        // Assert
        Assert.Equal("\\CloudDrive", result);
    }

    [Fact]
    public void RemotePathToNormalizedPath_Subfolder_ReturnsCorrectPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        
        // Act
        var result = mapper.RemotePathToNormalizedPath("/instance_399/Documents");
        
        // Assert
        Assert.Equal("\\CloudDrive\\Documents", result);
    }

    [Fact]
    public void RoundTrip_NormalizedToRemoteToNormalized_PreservesPath()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        var normalizedPath = "\\CloudDrive\\Documents\\Work\\file.txt";
        
        // Act
        var remotePath = mapper.NormalizedPathToRemotePath(normalizedPath);
        var roundTripPath = mapper.RemotePathToNormalizedPath(remotePath);
        
        // Assert
        Assert.Equal(normalizedPath, roundTripPath);
    }

    [Fact]
    public void HandlesUnicodeCharacters_Correctly()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        var unicodeFolder = "Dokumente";
        
        // Act
        var remotePath = mapper.ToRemotePath($"C:\\CloudDrive\\{unicodeFolder}");
        var localPath = mapper.ToLocalPath(remotePath);
        
        // Assert
        Assert.Contains(unicodeFolder, remotePath);
        Assert.Contains(unicodeFolder, localPath);
    }

    [Fact]
    public void HandlesSpecialCharacters_Correctly()
    {
        // Arrange
        var mapper = new PathMapper("C:\\CloudDrive", "/instance_399");
        var specialFolder = "My Folder (2024)";
        
        // Act
        var remotePath = mapper.ToRemotePath($"C:\\CloudDrive\\{specialFolder}");
        var localPath = mapper.ToLocalPath(remotePath);
        
        // Assert
        Assert.Contains(specialFolder, remotePath);
        Assert.Contains(specialFolder, localPath);
    }

    [Theory]
    [InlineData("C:\\CloudDrive", "/instance_399", "\\CloudDrive", "/instance_399")]
    [InlineData("C:\\Users\\testuser\\CloudDriveClient03", "/remote_instance", "\\Users\\testuser\\CloudDriveClient03", "/remote_instance")]
    [InlineData("D:\\Sync", "/", "\\Sync", "/")]
    public void RootPathConversion_VariousConfigurations(string syncRoot, string remoteBase, string normalizedRoot, string expectedRemote)
    {
        // Arrange
        var mapper = new PathMapper(syncRoot, remoteBase);
        
        // Act
        var result = mapper.NormalizedPathToRemotePath(normalizedRoot);
        
        // Assert
        Assert.Equal(expectedRemote, result);
    }
}
