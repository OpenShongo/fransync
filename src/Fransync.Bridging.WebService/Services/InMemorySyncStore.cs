using Fransync.Bridging.WebService.Models;
using System.Collections.Concurrent;

namespace Fransync.Bridging.WebService.Services;

public class InMemorySyncStore : ISyncStore
{
    private readonly ConcurrentDictionary<string, FileManifest> _manifests = new();
    private readonly ConcurrentDictionary<string, byte[]> _blocks = new();
    private DirectorySnapshot? _sourceDirectorySnapshot;
    private readonly ConcurrentDictionary<string, DateTime> _deletedFiles = new();

    public void StoreManifest(FileManifest manifest)
    {
        var normalizedPath = NormalizePath(manifest.RelativePath);
        _manifests[normalizedPath] = manifest;
    }

    public void StoreBlock(BlockPayload payload)
    {
        var normalizedFileId = NormalizePath(payload.FileId);
        var key = $"{normalizedFileId}#{payload.BlockIndex}";
        _blocks[key] = Convert.FromBase64String(payload.Data);
    }

    public FileManifest? GetManifest(string relativePath)
    {
        var normalizedPath = NormalizePath(relativePath);
        _manifests.TryGetValue(normalizedPath, out var manifest);
        return manifest;
    }

    public byte[]? GetBlock(string fileId, int blockIndex)
    {
        var normalizedFileId = NormalizePath(fileId);
        var key = $"{normalizedFileId}#{blockIndex}";
        _blocks.TryGetValue(key, out var block);
        return block;
    }

    public IEnumerable<string> GetAllManifestPaths()
    {
        return _manifests.Keys.ToList();
    }

    public bool DeleteManifest(string relativePath)
    {
        var normalizedPath = NormalizePath(relativePath);
        var manifestRemoved = _manifests.TryRemove(normalizedPath, out var manifest);

        if (manifestRemoved && manifest != null)
        {
            // Remove associated blocks
            for (int i = 0; i < manifest.Blocks.Count; i++)
            {
                var blockKey = $"{normalizedPath}#{i}";
                _blocks.TryRemove(blockKey, out _);
            }

            // Mark as deleted (enhanced functionality from the second method)
            _deletedFiles[normalizedPath] = DateTime.UtcNow;
        }

        return manifestRemoved;
    }

    public bool RenameManifest(string oldRelativePath, string newRelativePath)
    {
        var oldNormalizedPath = NormalizePath(oldRelativePath);
        var newNormalizedPath = NormalizePath(newRelativePath);

        if (!_manifests.TryRemove(oldNormalizedPath, out var manifest))
        {
            return false;
        }

        // Update manifest with new path
        manifest.RelativePath = newNormalizedPath;
        manifest.FileName = Path.GetFileName(newRelativePath);
        _manifests[newNormalizedPath] = manifest;

        // Rename associated blocks
        for (int i = 0; i < manifest.Blocks.Count; i++)
        {
            var oldBlockKey = $"{oldNormalizedPath}#{i}";
            var newBlockKey = $"{newNormalizedPath}#{i}";

            if (_blocks.TryRemove(oldBlockKey, out var blockData))
            {
                _blocks[newBlockKey] = blockData;
            }
        }

        return true;
    }

    public void StoreDirectorySnapshot(DirectorySnapshot snapshot)
    {
        _sourceDirectorySnapshot = snapshot;

        // Mark files that were in the previous snapshot but not in the new one as deleted
        if (_sourceDirectorySnapshot != null)
        {
            var currentFiles = snapshot.Files.Keys.ToHashSet();
            var previousFiles = _sourceDirectorySnapshot.Files.Keys.ToHashSet();

            foreach (var deletedFile in previousFiles.Except(currentFiles))
            {
                MarkFileAsDeleted(deletedFile);
            }
        }
    }

    public DirectorySnapshot? GetSourceDirectorySnapshot()
    {
        return _sourceDirectorySnapshot;
    }

    public void MarkFileAsDeleted(string relativePath)
    {
        _deletedFiles[relativePath] = DateTime.UtcNow;
        // Also remove from manifests and blocks
        DeleteManifest(relativePath);
    }

    public IEnumerable<string> GetDeletedFiles()
    {
        return _deletedFiles.Keys.ToList();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}