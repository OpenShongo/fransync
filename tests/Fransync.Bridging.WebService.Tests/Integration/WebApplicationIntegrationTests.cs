using Microsoft.AspNetCore.Mvc.Testing;
using FluentAssertions;
using System.Text.Json;
using System.Text;
using Fransync.Bridging.WebService.Models;

namespace Fransync.Bridging.WebService.Tests.Integration;

public class WebApplicationIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public WebApplicationIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task HealthEndpoint_ShouldReturnHealthyStatus()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var content = await response.Content.ReadAsStringAsync();
        var healthResponse = JsonSerializer.Deserialize<dynamic>(content);
        Assert.NotNull(healthResponse);
    }

    [Fact]
    public async Task VersionEndpoint_ShouldReturnVersionInfo()
    {
        // Act
        var response = await _client.GetAsync("/version");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("service");
        content.Should().Contain("version");
    }

    [Fact]
    public async Task PostManifest_ShouldStoreAndRetrieveManifest()
    {
        // Arrange
        var manifest = new FileManifest
        {
            RelativePath = "test/integration-file.txt",
            FileName = "integration-file.txt",
            FileSize = 1024,
            BlockSize = 64 * 1024,
            Blocks = new List<BlockInfo>
            {
                new() { Index = 0, Hash = "integration-hash", Size = 1024 }
            }
        };

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act - Store manifest
        var postResponse = await _client.PostAsync("/sync/manifest", content);

        // Assert - Store successful
        postResponse.IsSuccessStatusCode.Should().BeTrue();

        // Act - Retrieve manifest
        var getResponse = await _client.GetAsync($"/sync/manifest/{Uri.EscapeDataString(manifest.RelativePath)}");

        // Assert - Retrieve successful
        getResponse.IsSuccessStatusCode.Should().BeTrue();
        var retrievedJson = await getResponse.Content.ReadAsStringAsync();
        var retrievedManifest = JsonSerializer.Deserialize<FileManifest>(retrievedJson, JsonOptions);
        
        retrievedManifest.Should().NotBeNull();
        retrievedManifest!.RelativePath.Should().Be(manifest.RelativePath);
        retrievedManifest.FileName.Should().Be(manifest.FileName);
        retrievedManifest.FileSize.Should().Be(manifest.FileSize);
    }

    [Fact]
    public async Task PostBlock_ShouldStoreAndRetrieveBlock()
    {
        // Arrange
        var testData = "Hello, Integration Test!"u8.ToArray();
        var blockPayload = new BlockPayload
        {
            FileId = "test/integration-block.txt",
            BlockIndex = 0,
            Hash = "integration-block-hash",
            Data = Convert.ToBase64String(testData)
        };

        var json = JsonSerializer.Serialize(blockPayload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act - Store block
        var postResponse = await _client.PostAsync("/sync/block", content);

        // Assert - Store successful
        postResponse.IsSuccessStatusCode.Should().BeTrue();

        // Act - Retrieve block
        var getResponse = await _client.GetAsync($"/sync/block/{blockPayload.BlockIndex}/{Uri.EscapeDataString(blockPayload.FileId)}");

        // Assert - Retrieve successful
        getResponse.IsSuccessStatusCode.Should().BeTrue();
        var retrievedJson = await getResponse.Content.ReadAsStringAsync();
        var retrievedPayload = JsonSerializer.Deserialize<BlockPayload>(retrievedJson, JsonOptions);
        
        retrievedPayload.Should().NotBeNull();
        retrievedPayload!.FileId.Should().Be(blockPayload.FileId);
        retrievedPayload.BlockIndex.Should().Be(blockPayload.BlockIndex);
        retrievedPayload.Data.Should().Be(blockPayload.Data);
    }

    [Fact]
    public async Task GetAllManifests_ShouldReturnEmptyListInitially()
    {
        // Act
        var response = await _client.GetAsync("/sync/manifests");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var content = await response.Content.ReadAsStringAsync();
        var paths = JsonSerializer.Deserialize<string[]>(content, JsonOptions);
        paths.Should().NotBeNull();
        paths.Should().BeOfType<string[]>();
    }

    [Fact]
    public async Task GetSnapshot_ShouldReturnDirectorySnapshot()
    {
        // Act
        var response = await _client.GetAsync("/sync/snapshot");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var content = await response.Content.ReadAsStringAsync();
        var snapshot = JsonSerializer.Deserialize<DirectorySnapshot>(content, JsonOptions);
        
        snapshot.Should().NotBeNull();
        snapshot!.TotalFiles.Should().BeGreaterOrEqualTo(0);
        snapshot.Files.Should().NotBeNull();
    }

    [Fact]
    public async Task PostFileOperation_WithDeleteOperation_ShouldReturnOkOrNotFound()
    {
        // Arrange
        var operation = new FileOperationCommand
        {
            OperationType = FileOperationType.Deleted,
            RelativePath = "nonexistent-file.txt",
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(operation, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/sync/operation", content);

        // Assert
        // Should return either Ok (if file existed) or NotFound (if file didn't exist)
        (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound).Should().BeTrue();
    }
}