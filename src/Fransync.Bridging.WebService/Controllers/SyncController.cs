namespace Fransync.Bridging.WebService.Controllers;
using Microsoft.AspNetCore.Mvc;
using Models;
using Services;
using System.Text;
using System.Security.Cryptography;



[ApiController]
[Route("sync")]
public class SyncController(ISyncStore store, IWebSocketManager wsManager) : ControllerBase
{
    [HttpPost("manifest")]
    public async Task<IActionResult> PostManifest([FromBody] FileManifest? manifest)
    {
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.RelativePath))
            return BadRequest();

        manifest.RelativePath = NormalizePath(manifest.RelativePath);
        store.StoreManifest(manifest);
        Console.WriteLine($"[RECEIVED MANIFEST] {manifest.RelativePath}");

        var notification = new WebSocketMessage
        {
            Type = "manifest_received",
            Data = new ManifestNotification
            {
                RelativePath = manifest.RelativePath,
                FileName = manifest.FileName,
                FileSize = manifest.FileSize,
                BlockCount = manifest.Blocks.Count
            }
        };

        await wsManager.BroadcastToAllAsync(notification);
        return Ok();
    }

    [HttpPost("block")]
    public async Task<IActionResult> PostBlock([FromBody] BlockPayload payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.FileId))
            return BadRequest();

        payload.FileId = NormalizePath(payload.FileId);
        store.StoreBlock(payload);
        Console.WriteLine($"[RECEIVED BLOCK] {payload.FileId} | Block {payload.BlockIndex}");

        var notification = new WebSocketMessage
        {
            Type = "block_received",
            Data = new BlockNotification
            {
                FileId = payload.FileId,
                BlockIndex = payload.BlockIndex,
                Hash = payload.Hash,
                Size = Convert.FromBase64String(payload.Data).Length
            }
        };

        await wsManager.BroadcastToAllAsync(notification);
        return Ok();
    }

    [HttpPost("operation")]
    public async Task<IActionResult> PostFileOperation([FromBody] FileOperationCommand? operation)
    {
        if (operation == null || string.IsNullOrWhiteSpace(operation.RelativePath))
            return BadRequest();

        operation.RelativePath = NormalizePath(operation.RelativePath);
        if (!string.IsNullOrEmpty(operation.OldRelativePath))
        {
            operation.OldRelativePath = NormalizePath(operation.OldRelativePath);
        }

        bool success = operation.OperationType switch
        {
            FileOperationType.Deleted => store.DeleteManifest(operation.RelativePath),
            FileOperationType.Renamed when !string.IsNullOrEmpty(operation.OldRelativePath)
                => store.RenameManifest(operation.OldRelativePath, operation.RelativePath),
            _ => true // Created/Modified are handled by manifest upload
        };

        if (success)
        {
            Console.WriteLine($"[FILE OPERATION] {operation.OperationType}: {operation.RelativePath}");

            var notification = new WebSocketMessage
            {
                Type = "file_operation",
                Data = operation
            };

            await wsManager.BroadcastToAllAsync(notification);
            return Ok();
        }

        return NotFound();
    }

    [HttpGet("manifest/{*relativePath}")]
    public IActionResult GetManifest(string relativePath)
    {
        var decodedPath = Uri.UnescapeDataString(relativePath);
        var normalizedPath = NormalizePath(decodedPath);

        var manifest = store.GetManifest(normalizedPath);
        if (manifest == null)
        {
            Console.WriteLine($"[MANIFEST NOT FOUND] Requested: '{relativePath}' | Normalized: '{normalizedPath}'");
            return NotFound();
        }

        Console.WriteLine($"[MANIFEST REQUESTED] {normalizedPath}");
        return Ok(manifest);
    }

    [HttpGet("block/{blockIndex:int}/{*fileId}")]
    public IActionResult GetBlock(int blockIndex, string fileId)
    {
        var decodedFileId = Uri.UnescapeDataString(fileId);
        var normalizedFileId = NormalizePath(decodedFileId);

        var blockData = store.GetBlock(normalizedFileId, blockIndex);
        if (blockData == null)
        {
            Console.WriteLine($"[BLOCK NOT FOUND] FileId: '{fileId}' | Normalized: '{normalizedFileId}' | Block: {blockIndex}");
            return NotFound();
        }

        var response = new BlockPayload
        {
            FileId = normalizedFileId,
            BlockIndex = blockIndex,
            Data = Convert.ToBase64String(blockData)
        };

        Console.WriteLine($"[BLOCK REQUESTED] {normalizedFileId} | Block {blockIndex}");
        return Ok(response);
    }

    [HttpGet("manifests")]
    public IActionResult GetAllManifests()
    {
        var paths = store.GetAllManifestPaths();
        Console.WriteLine($"[MANIFEST LIST REQUESTED] Found {paths.Count()} manifests");
        return Ok(paths);
    }

    [HttpGet("snapshot")]
    public IActionResult GetDirectorySnapshot()
    {
        try
        {
            var manifests = store.GetAllManifestPaths().ToList();
            var snapshot = new DirectorySnapshot
            {
                Timestamp = DateTime.UtcNow,
                TotalFiles = manifests.Count
            };

            foreach (var path in manifests)
            {
                var manifest = store.GetManifest(path);
                if (manifest != null)
                {
                    var fileSnapshot = new FileSnapshot
                    {
                        RelativePath = manifest.RelativePath,
                        FileName = manifest.FileName,
                        FileSize = manifest.FileSize,
                        LastModified = DateTime.UtcNow, // You might want to store this in the manifest
                        ContentHash = CalculateManifestHash(manifest),
                        BlockCount = manifest.Blocks.Count
                    };

                    snapshot.Files[path] = fileSnapshot;
                    snapshot.TotalSize += manifest.FileSize;
                }
            }

            snapshot.DirectoryHash = CalculateDirectoryHash(snapshot.Files.Values.ToList());

            Console.WriteLine($"[SNAPSHOT REQUESTED] {snapshot.TotalFiles} files, Hash: {snapshot.DirectoryHash}");
            return Ok(snapshot);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SNAPSHOT ERROR] {ex.Message}");
            return StatusCode(500, "Error creating snapshot");
        }
    }

    [HttpPost("register-directory")]
    public async Task<IActionResult> RegisterDirectoryStructure([FromBody] DirectorySnapshot? snapshot)
    {
        if (snapshot == null)
            return BadRequest();

        try
        {
            // Store the complete directory structure from the source client
            store.StoreDirectorySnapshot(snapshot);
            Console.WriteLine($"[DIRECTORY REGISTERED] {snapshot.TotalFiles} files, Hash: {snapshot.DirectoryHash}");

            // Notify all clients about the new directory structure
            var notification = new WebSocketMessage
            {
                Type = "directory_structure_updated",
                Data = new
                {
                    TotalFiles = snapshot.TotalFiles,
                    TotalSize = snapshot.TotalSize,
                    DirectoryHash = snapshot.DirectoryHash,
                    Timestamp = snapshot.Timestamp
                }
            };

            await wsManager.BroadcastToAllAsync(notification);
            return Ok();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DIRECTORY REGISTRATION ERROR] {ex.Message}");
            return StatusCode(500, "Error registering directory structure");
        }
    }

    [HttpGet("source-snapshot")]
    public IActionResult GetSourceDirectorySnapshot()
    {
        try
        {
            var sourceSnapshot = store.GetSourceDirectorySnapshot();
            if (sourceSnapshot == null)
            {
                return NotFound("No source directory structure registered");
            }

            Console.WriteLine($"[SOURCE SNAPSHOT REQUESTED] {sourceSnapshot.TotalFiles} files");
            return Ok(sourceSnapshot);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOURCE SNAPSHOT ERROR] {ex.Message}");
            return StatusCode(500, "Error retrieving source snapshot");
        }
    }


    private string CalculateManifestHash(FileManifest manifest)
    {
        // Use block hashes to create a content hash
        var combinedHashes = string.Join("", manifest.Blocks.OrderBy(b => b.Index).Select(b => b.Hash));
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combinedHashes));
        return Convert.ToHexString(hashBytes);
    }

    private string CalculateDirectoryHash(List<FileSnapshot> files)
    {
        var sortedFiles = files.OrderBy(f => f.RelativePath).ToList();
        var sb = new StringBuilder();

        foreach (var file in sortedFiles)
        {
            sb.Append($"{file.RelativePath}|{file.FileSize}|{file.ContentHash}|");
        }

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}