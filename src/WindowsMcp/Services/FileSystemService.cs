using System.IO.Compression;
using System.Text;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class FileSystemService : IFileSystemService
{

    public async Task<string> ReadTextAsync(string path, long maxBytes, string encoding, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = PathPolicy.Normalize(path);
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new InvalidOperationException($"File size {info.Length} exceeds max_bytes {maxBytes}");
        var enc = ResolveEncoding(encoding, info);
        return await File.ReadAllTextAsync(path, enc, ct);
    }

    public async Task<byte[]> ReadBytesAsync(string path, long maxBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = PathPolicy.Normalize(path);
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new InvalidOperationException($"File size {info.Length} exceeds max_bytes {maxBytes}");
        return await File.ReadAllBytesAsync(path, ct);
    }

    public async Task WriteTextAsync(string path, string content, string encoding, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = PathPolicy.Normalize(path);
        var enc = encoding.ToLowerInvariant() switch
        {
            "utf-16" => Encoding.Unicode,
            "ascii"  => Encoding.ASCII,
            _        => new UTF8Encoding(false)   // utf-8, no BOM
        };
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tmp, content, enc, ct);
        // Atomic rename with retry on Windows EBUSY. On any final failure
        // (including the third attempt that rethrows), clean up the temp file
        // before propagating to avoid orphaned .tmp.<guid> files.
        for (int i = 0; i < 3; i++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) when (i < 2)
            {
                await Task.Delay(50 * (i + 1), ct);
            }
            catch (IOException)
            {
                try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
                throw;
            }
        }
    }

    public Task<FileInfoDto> GetInfoAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = PathPolicy.Normalize(path);
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        var size = info is FileInfo fi ? fi.Length : 0;
        return Task.FromResult(new FileInfoDto(
            Path:        info.FullName,
            Size:        size,
            Created:     info.CreationTimeUtc,
            Modified:    info.LastWriteTimeUtc,
            Accessed:    info.LastAccessTimeUtc,
            Attributes:  info.Attributes.ToString(),
            IsDirectory: info is DirectoryInfo));
    }

    public Task<FileSearchHit[]> SearchAsync(string root, string? pattern, long? minSize, DateTime? modifiedSince, bool findDuplicates, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        root = PathPolicy.Normalize(root);
        if (!Directory.Exists(root))
            throw new ArgumentException($"Search root does not exist: {root}");
        var hits = new List<FileSearchHit>();
        var files = Directory.EnumerateFiles(root, pattern ?? "*", SearchOption.AllDirectories);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (hits.Count >= PathPolicy.MaxSearchHits) break;
            try
            {
                var info = new FileInfo(f);
                if (minSize.HasValue && info.Length < minSize.Value) continue;
                if (modifiedSince.HasValue && info.LastWriteTimeUtc < modifiedSince.Value) continue;
                hits.Add(new FileSearchHit(info.FullName, info.Length, info.LastWriteTimeUtc));
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible files */ }
        }

        if (findDuplicates)
        {
            // Group by size, then hash equal-size candidates
            var grouped = hits.GroupBy(h => h.Size).Where(g => g.Count() > 1);
            var dups = new List<FileSearchHit>();
            foreach (var group in grouped)
            {
                ct.ThrowIfCancellationRequested();
                // Files that can't be hashed (locked/denied) return null and are skipped —
                // one unreadable file must not abort the whole duplicate search.
                var byHash = group
                    .Select(h => (Hit: h, Hash: HashFile(h.Path)))
                    .Where(x => x.Hash is not null)
                    .GroupBy(x => x.Hash!, x => x.Hit);
                foreach (var hg in byHash.Where(g => g.Count() > 1))
                    dups.AddRange(hg);
            }
            return Task.FromResult(dups.ToArray());
        }
        return Task.FromResult(hits.ToArray());
    }

    public async Task<string> HashFileAsync(string path, string algorithm = "sha256", CancellationToken ct = default)
    {
        path = PathPolicy.Normalize(path);
        using System.Security.Cryptography.HashAlgorithm hasher = algorithm.ToLowerInvariant() switch
        {
            "sha256" => System.Security.Cryptography.SHA256.Create(),
            "sha1"   => System.Security.Cryptography.SHA1.Create(),
            "md5"    => System.Security.Cryptography.MD5.Create(),
            _ => throw new ArgumentException($"Unknown algorithm '{algorithm}'; expected sha256|sha1|md5")
        };
        await using var stream = File.OpenRead(path);
        var hash = await hasher.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var md5 = System.Security.Cryptography.MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or access-denied file: skip it from dedup rather than fail the search.
            return null;
        }
    }

    public Task CopyAsync(string src, string dst, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        src = PathPolicy.Normalize(src);
        dst = PathPolicy.Normalize(dst);
        File.Copy(src, dst, overwrite: true);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string src, string dst, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        src = PathPolicy.Normalize(src);
        dst = PathPolicy.Normalize(dst);
        File.Move(src, dst, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = PathPolicy.Normalize(path);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<string[]> ListAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = PathPolicy.Normalize(path);
        return Task.FromResult(Directory.EnumerateFileSystemEntries(path).ToArray());
    }

    public Task ZipAsync(string srcDir, string dstZip, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        srcDir = PathPolicy.Normalize(srcDir);
        dstZip = PathPolicy.Normalize(dstZip);
        if (File.Exists(dstZip)) File.Delete(dstZip);
        ZipFile.CreateFromDirectory(srcDir, dstZip);
        return Task.CompletedTask;
    }

    public Task UnzipAsync(string srcZip, string dstDir, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        srcZip = PathPolicy.Normalize(srcZip);
        dstDir = PathPolicy.Normalize(dstDir);
        ZipFile.ExtractToDirectory(srcZip, dstDir, overwriteFiles: true);
        return Task.CompletedTask;
    }

    private static Encoding ResolveEncoding(string encoding, FileInfo info) =>
        encoding.ToLowerInvariant() switch
        {
            "utf-8"  => Encoding.UTF8,
            "utf-16" => Encoding.Unicode,
            "ascii"  => Encoding.ASCII,
            "auto"   => DetectEncodingFromBom(info) ?? Encoding.UTF8,
            _        => Encoding.UTF8
        };

    private static Encoding? DetectEncodingFromBom(FileInfo info)
    {
        using var s = info.OpenRead();
        var bom = new byte[4];
        var read = s.Read(bom, 0, 4);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return null;
    }
}
