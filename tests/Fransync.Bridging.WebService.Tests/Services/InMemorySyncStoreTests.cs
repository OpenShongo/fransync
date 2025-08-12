using Fransync.Bridging.WebService.Models;
using Fransync.Bridging.WebService.Services;
using FluentAssertions;

namespace Fransync.Bridging.WebService.Tests.Services;

public class InMemorySyncStoreTests
{
    private readonly InMemorySyncStore _store = new();

    [Fact]
    public void StoreManifest_ShouldStoreAndRetrieveManifest()
    {
        // Arrange
        var manifest = CreateTestFileManifest("test/file.txt");

        // Act
        _store.StoreManifest(manifest);
        var retrieved = _store.GetManifest("test/file.txt");

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.RelativePath.Should().Be("test/file.txt");
        retrieved.FileName.Should().Be("file.txt");
        retrieved.FileSize.Should().Be(1024);
    }

    [Fact]
    public void StoreManifest_ShouldNormalizePathSeparators()
    {
        // Arrange
        var manifest = CreateTestFileManifest("test\\file.txt");

        // Act
        _store.StoreManifest(manifest);
        var retrieved = _store.GetManifest("test/file.txt");

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.RelativePath.Should().Be("test\\file.txt"); // Original manifest unchanged
    }

    [Fact]
    public void StoreBlock_ShouldStoreAndRetrieveBlock()
    {
        // Arrange
        var testData = "Hello, World!"u8.ToArray();
        var payload = new BlockPayload
        {
            FileId = "test/file.txt",
            BlockIndex = 0,
            Hash = "test-hash",
            Data = Convert.ToBase64String(testData)
        };

        // Act
        _store.StoreBlock(payload);
        var retrieved = _store.GetBlock("test/file.txt", 0);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved.Should().BeEquivalentTo(testData);
    }

    [Fact]
    public void GetManifest_WhenNotExists_ShouldReturnNull()
    {
        // Act
        var result = _store.GetManifest("nonexistent.txt");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetBlock_WhenNotExists_ShouldReturnNull()
    {
        // Act
        var result = _store.GetBlock("nonexistent.txt", 0);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetAllManifestPaths_ShouldReturnAllStoredPaths()
    {
        // Arrange
        var manifest1 = CreateTestFileManifest("file1.txt");
        var manifest2 = CreateTestFileManifest("folder/file2.txt");

        // Act
        _store.StoreManifest(manifest1);
        _store.StoreManifest(manifest2);
        var paths = _store.GetAllManifestPaths().ToList();

        // Assert
        paths.Should().HaveCount(2);
        paths.Should().Contain("file1.txt");
        paths.Should().Contain("folder/file2.txt");
    }

    [Fact]
    public void DeleteManifest_ShouldRemoveManifestAndBlocks()
    {
        // Arrange
        var manifest = CreateTestFileManifest("test/file.txt");
        var blockPayload = new BlockPayload
        {
            FileId = "test/file.txt",
            BlockIndex = 0,
            Hash = "test-hash",
            Data = Convert.ToBase64String("test data"u8.ToArray())
        };

        _store.StoreManifest(manifest);
        _store.StoreBlock(blockPayload);

        // Act
        var result = _store.DeleteManifest("test/file.txt");

        // Assert
        result.Should().BeTrue();
        _store.GetManifest("test/file.txt").Should().BeNull();
        _store.GetBlock("test/file.txt", 0).Should().BeNull();
    }

    [Fact]
    public void DeleteManifest_WhenNotExists_ShouldReturnFalse()
    {
        // Act
        var result = _store.DeleteManifest("nonexistent.txt");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void RenameManifest_ShouldUpdatePathAndBlocks()
    {
        // Arrange
        var manifest = CreateTestFileManifest("old/path.txt");
        var blockPayload = new BlockPayload
        {
            FileId = "old/path.txt",
            BlockIndex = 0,
            Hash = "test-hash",
            Data = Convert.ToBase64String("test data"u8.ToArray())
        };

        _store.StoreManifest(manifest);
        _store.StoreBlock(blockPayload);

        // Act
        var result = _store.RenameManifest("old/path.txt", "new/path.txt");

        // Assert
        result.Should().BeTrue();

        // Old path should not exist
        _store.GetManifest("old/path.txt").Should().BeNull();
        _store.GetBlock("old/path.txt", 0).Should().BeNull();

        // New path should exist
        var renamedManifest = _store.GetManifest("new/path.txt");
        renamedManifest.Should().NotBeNull();
        renamedManifest!.RelativePath.Should().Be("new/path.txt");
        renamedManifest.FileName.Should().Be("path.txt");

        var renamedBlock = _store.GetBlock("new/path.txt", 0);
        renamedBlock.Should().NotBeNull();
    }

    [Fact]
    public void RenameManifest_WhenNotExists_ShouldReturnFalse()
    {
        // Act
        var result = _store.RenameManifest("nonexistent.txt", "new.txt");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void StoreDirectorySnapshot_ShouldStoreAndRetrieve()
    {
        // Arrange
        var snapshot = new DirectorySnapshot
        {
            Timestamp = DateTime.UtcNow,
            TotalFiles = 2,
            TotalSize = 2048,
            DirectoryHash = "test-hash",
            Files = new Dictionary<string, FileSnapshot>
            {
                ["file1.txt"] = new() { RelativePath = "file1.txt", FileSize = 1024 },
                ["file2.txt"] = new() { RelativePath = "file2.txt", FileSize = 1024 }
            }
        };

        // Act
        _store.StoreDirectorySnapshot(snapshot);
        var retrieved = _store.GetSourceDirectorySnapshot();

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.TotalFiles.Should().Be(2);
        retrieved.Files.Should().HaveCount(2);
    }

    [Fact]
    public void MarkFileAsDeleted_ShouldAddToDeletedFiles()
    {
        // Arrange
        var manifest = CreateTestFileManifest("test/file.txt");
        _store.StoreManifest(manifest);

        // Act
        _store.MarkFileAsDeleted("test/file.txt");

        // Assert
        var deletedFiles = _store.GetDeletedFiles().ToList();
        deletedFiles.Should().Contain("test/file.txt");
        _store.GetManifest("test/file.txt").Should().BeNull();
    }

    private static FileManifest CreateTestFileManifest(string relativePath) => new()
    {
        RelativePath = relativePath,
        FileName = Path.GetFileName(relativePath),
        FileSize = 1024,
        BlockSize = 64 * 1024,
        Blocks = new List<BlockInfo>
        {
            new() { Index = 0, Hash = "block-hash-0", Size = 1024 }
        }
    };
}