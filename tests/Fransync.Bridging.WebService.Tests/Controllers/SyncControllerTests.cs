using Fransync.Bridging.WebService.Controllers;
using Fransync.Bridging.WebService.Models;
using Fransync.Bridging.WebService.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;
using FluentAssertions;

namespace Fransync.Bridging.WebService.Tests.Controllers;

public class SyncControllerTests
{
    private readonly Mock<ISyncStore> _mockStore = new();
    private readonly Mock<IWebSocketManager> _mockWebSocketManager = new();
    private readonly SyncController _controller;

    public SyncControllerTests()
    {
        _controller = new SyncController(_mockStore.Object, _mockWebSocketManager.Object);
    }

    [Fact]
    public async Task PostManifest_WithValidManifest_ShouldReturnOk()
    {
        // Arrange
        var manifest = CreateTestFileManifest("test/file.txt");

        // Act
        var result = await _controller.PostManifest(manifest);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockStore.Verify(s => s.StoreManifest(It.IsAny<FileManifest>()), Times.Once);
        _mockWebSocketManager.Verify(ws => ws.BroadcastToAllAsync(It.IsAny<WebSocketMessage>()), Times.Once);
    }

    [Fact]
    public async Task PostManifest_WithNullManifest_ShouldReturnBadRequest()
    {
        // Act
        var result = await _controller.PostManifest(null);

        // Assert
        result.Should().BeOfType<BadRequestResult>();
        _mockStore.Verify(s => s.StoreManifest(It.IsAny<FileManifest>()), Times.Never);
    }

    [Fact]
    public async Task PostManifest_WithEmptyRelativePath_ShouldReturnBadRequest()
    {
        // Arrange
        var manifest = CreateTestFileManifest("");

        // Act
        var result = await _controller.PostManifest(manifest);

        // Assert
        result.Should().BeOfType<BadRequestResult>();
        _mockStore.Verify(s => s.StoreManifest(It.IsAny<FileManifest>()), Times.Never);
    }

    [Fact]
    public async Task PostBlock_WithValidPayload_ShouldReturnOk()
    {
        // Arrange
        var payload = new BlockPayload
        {
            FileId = "test/file.txt",
            BlockIndex = 0,
            Hash = "test-hash",
            Data = Convert.ToBase64String("test data"u8.ToArray())
        };

        // Act
        var result = await _controller.PostBlock(payload);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockStore.Verify(s => s.StoreBlock(It.IsAny<BlockPayload>()), Times.Once);
        _mockWebSocketManager.Verify(ws => ws.BroadcastToAllAsync(It.IsAny<WebSocketMessage>()), Times.Once);
    }

    [Fact]
    public async Task PostBlock_WithNullPayload_ShouldReturnBadRequest()
    {
        // Act
        var result = await _controller.PostBlock(null);

        // Assert
        result.Should().BeOfType<BadRequestResult>();
        _mockStore.Verify(s => s.StoreBlock(It.IsAny<BlockPayload>()), Times.Never);
    }

    [Fact]
    public async Task PostFileOperation_WithDeleteOperation_ShouldCallDeleteManifest()
    {
        // Arrange
        var operation = new FileOperationCommand
        {
            OperationType = FileOperationType.Deleted,
            RelativePath = "test/file.txt"
        };
        _mockStore.Setup(s => s.DeleteManifest(It.IsAny<string>())).Returns(true);

        // Act
        var result = await _controller.PostFileOperation(operation);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockStore.Verify(s => s.DeleteManifest("test/file.txt"), Times.Once);
        _mockWebSocketManager.Verify(ws => ws.BroadcastToAllAsync(It.IsAny<WebSocketMessage>()), Times.Once);
    }

    [Fact]
    public async Task PostFileOperation_WithRenameOperation_ShouldCallRenameManifest()
    {
        // Arrange
        var operation = new FileOperationCommand
        {
            OperationType = FileOperationType.Renamed,
            RelativePath = "new/path.txt",
            OldRelativePath = "old/path.txt"
        };
        _mockStore.Setup(s => s.RenameManifest(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // Act
        var result = await _controller.PostFileOperation(operation);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockStore.Verify(s => s.RenameManifest("old/path.txt", "new/path.txt"), Times.Once);
        _mockWebSocketManager.Verify(ws => ws.BroadcastToAllAsync(It.IsAny<WebSocketMessage>()), Times.Once);
    }

    [Fact]
    public async Task PostFileOperation_WhenOperationFails_ShouldReturnNotFound()
    {
        // Arrange
        var operation = new FileOperationCommand
        {
            OperationType = FileOperationType.Deleted,
            RelativePath = "test/file.txt"
        };
        _mockStore.Setup(s => s.DeleteManifest(It.IsAny<string>())).Returns(false);

        // Act
        var result = await _controller.PostFileOperation(operation);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
        _mockWebSocketManager.Verify(ws => ws.BroadcastToAllAsync(It.IsAny<WebSocketMessage>()), Times.Never);
    }

    [Fact]
    public void GetManifest_WhenExists_ShouldReturnOkWithManifest()
    {
        // Arrange
        var manifest = CreateTestFileManifest("test/file.txt");
        _mockStore.Setup(s => s.GetManifest("test/file.txt")).Returns(manifest);

        // Act
        var result = _controller.GetManifest("test/file.txt");

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().Be(manifest);
    }

    [Fact]
    public void GetManifest_WhenNotExists_ShouldReturnNotFound()
    {
        // Arrange
        _mockStore.Setup(s => s.GetManifest(It.IsAny<string>())).Returns((FileManifest?)null);

        // Act
        var result = _controller.GetManifest("nonexistent.txt");

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetBlock_WhenExists_ShouldReturnOkWithBlockPayload()
    {
        // Arrange
        var testData = "test block data"u8.ToArray();
        _mockStore.Setup(s => s.GetBlock("test/file.txt", 0)).Returns(testData);

        // Act
        var result = _controller.GetBlock(0, "test/file.txt");

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var payload = okResult.Value.Should().BeOfType<BlockPayload>().Subject;
        payload.FileId.Should().Be("test/file.txt");
        payload.BlockIndex.Should().Be(0);
        payload.Data.Should().Be(Convert.ToBase64String(testData));
    }

    [Fact]
    public void GetBlock_WhenNotExists_ShouldReturnNotFound()
    {
        // Arrange
        _mockStore.Setup(s => s.GetBlock(It.IsAny<string>(), It.IsAny<int>())).Returns((byte[]?)null);

        // Act
        var result = _controller.GetBlock(0, "nonexistent.txt");

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetAllManifests_ShouldReturnAllPaths()
    {
        // Arrange
        var paths = new[] { "file1.txt", "folder/file2.txt" };
        _mockStore.Setup(s => s.GetAllManifestPaths()).Returns(paths);

        // Act
        var result = _controller.GetAllManifests();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().BeEquivalentTo(paths);
    }

    [Fact]
    public async Task RegisterDirectoryStructure_WithValidSnapshot_ShouldReturnOk()
    {
        // Arrange
        var snapshot = new DirectorySnapshot
        {
            Timestamp = DateTime.UtcNow,
            TotalFiles = 1,
            DirectoryHash = "test-hash",
            Files = new Dictionary<string, FileSnapshot>()
        };

        // Act
        var result = await _controller.RegisterDirectoryStructure(snapshot);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockStore.Verify(s => s.StoreDirectorySnapshot(snapshot), Times.Once);
        _mockWebSocketManager.Verify(ws => ws.BroadcastToAllAsync(It.IsAny<WebSocketMessage>()), Times.Once);
    }

    [Fact]
    public async Task RegisterDirectoryStructure_WithNullSnapshot_ShouldReturnBadRequest()
    {
        // Act
        var result = await _controller.RegisterDirectoryStructure(null);

        // Assert
        result.Should().BeOfType<BadRequestResult>();
        _mockStore.Verify(s => s.StoreDirectorySnapshot(It.IsAny<DirectorySnapshot>()), Times.Never);
    }

    [Fact]
    public void GetSourceDirectorySnapshot_WhenExists_ShouldReturnOkWithSnapshot()
    {
        // Arrange
        var snapshot = new DirectorySnapshot { TotalFiles = 1 };
        _mockStore.Setup(s => s.GetSourceDirectorySnapshot()).Returns(snapshot);

        // Act
        var result = _controller.GetSourceDirectorySnapshot();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        okResult.Value.Should().Be(snapshot);
    }

    [Fact]
    public void GetSourceDirectorySnapshot_WhenNotExists_ShouldReturnNotFound()
    {
        // Arrange
        _mockStore.Setup(s => s.GetSourceDirectorySnapshot()).Returns((DirectorySnapshot?)null);

        // Act
        var result = _controller.GetSourceDirectorySnapshot();

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
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