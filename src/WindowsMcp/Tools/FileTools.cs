using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class FileTools
{
    private readonly IFileSystemService _fs;
    private readonly IInputService _input;
    private readonly IFileStreamService _streams;

    public FileTools(IFileSystemService fs, IInputService input, IFileStreamService streams)
    {
        _fs = fs;
        _input = input;
        _streams = streams;
    }

    [McpServerTool, Description("Search for files. root: starting directory. pattern: glob (e.g. '*.txt'). min_size: bytes. modified_since: ISO 8601 datetime. find_duplicates: group identical files.")]
    public async Task<string> FileSearch(
        [Description("Root directory to search from")] string root,
        [Description("Glob pattern, e.g. '*.txt'")] string? pattern = null,
        [Description("Minimum file size in bytes")] long? min_size = null,
        [Description("Only files modified since this datetime (ISO 8601)")] string? modified_since = null,
        [Description("Group results by content hash to find duplicates")] bool find_duplicates = false,
        CancellationToken ct = default)
    {
        DateTime? since = null;
        if (!string.IsNullOrWhiteSpace(modified_since))
        {
            if (!DateTime.TryParse(modified_since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                throw new ArgumentException($"'modified_since' must be a valid ISO 8601 datetime, got: '{modified_since}'");
            since = parsed;
        }

        var hits = await _fs.SearchAsync(root, pattern, min_size, since, find_duplicates, ct);
        return JsonSerializer.Serialize(hits);
    }

    [McpServerTool, Description("File operations: copy, move, delete, list. copy, move, and delete require confirm:true.")]
    public async Task<string> FileManage(
        [Description("Action: copy, move, delete, list")] string action,
        [Description("Source path")] string src,
        [Description("Destination path (required for copy/move)")] string? dst = null,
        [Description("Must be true to confirm copy, move, or delete")] bool confirm = false,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "copy":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for copy/move/delete");
                if (string.IsNullOrWhiteSpace(dst))
                    throw new ArgumentException("'copy' requires dst");
                await _fs.CopyAsync(src, dst, ct);
                return $"copied '{src}' to '{dst}'";

            case "move":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for copy/move/delete");
                if (string.IsNullOrWhiteSpace(dst))
                    throw new ArgumentException("'move' requires dst");
                await _fs.MoveAsync(src, dst, ct);
                return $"moved '{src}' to '{dst}'";

            case "delete":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for delete");
                await _fs.DeleteAsync(src, ct);
                return $"deleted '{src}'";

            case "list":
                var entries = await _fs.ListAsync(src, ct);
                return JsonSerializer.Serialize(entries);

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected copy|move|delete|list");
        }
    }

    [McpServerTool, Description("Type a file path into a focused open/save dialog.")]
    public async Task<string> FileDialog(
        [Description("File path to type into the active open/save dialog")] string path)
    {
        await _input.TypeAsync(path);
        return "typed path into focused dialog";
    }

    [McpServerTool, Description("Read a file as text.")]
    public async Task<string> FileRead(
        [Description("File path to read")] string path,
        [Description("Maximum bytes to read")] long max_bytes = 1048576,
        [Description("Text encoding: auto, utf-8, utf-16, ascii")] string encoding = "auto",
        CancellationToken ct = default)
    {
        return await _fs.ReadTextAsync(path, max_bytes, encoding, ct);
    }

    [McpServerTool, Description("Write text content to a file. Requires confirm:true.")]
    public async Task<string> FileWrite(
        [Description("File path to write")] string path,
        [Description("Text content to write")] string content,
        [Description("Text encoding, e.g. utf-8")] string encoding = "utf-8",
        [Description("Must be true to confirm the file write")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for file writes");
        await _fs.WriteTextAsync(path, content, encoding, ct);
        return $"wrote {content.Length} chars to '{path}'";
    }

    [McpServerTool, Description("Compute a file's hash digest for integrity checks or IOC lookups (e.g. VirusTotal). algorithm: sha256 (default), sha1, or md5. Returns the lowercase hex digest.")]
    public async Task<string> FileHash(
        [Description("File path to hash")] string path,
        [Description("Hash algorithm: sha256, sha1, or md5")] string algorithm = "sha256",
        CancellationToken ct = default)
    {
        return await _fs.HashFileAsync(path, algorithm, ct);
    }

    [McpServerTool, Description("List NTFS alternate data streams (e.g. Zone.Identifier or hidden payloads) on a file, and the reparse target if the path is a symlink/junction. Forensic checks that file_info doesn't surface.")]
    public async Task<string> FileStreams(
        [Description("File or directory path to inspect")] string path,
        CancellationToken ct = default)
    {
        var streams = await _streams.GetStreamsAsync(path, ct);
        return JsonSerializer.Serialize(streams);
    }

    [McpServerTool, Description("Get metadata for a file or directory.")]
    public async Task<string> FileInfo(
        [Description("Path to inspect")] string path,
        CancellationToken ct = default)
    {
        var info = await _fs.GetInfoAsync(path, ct);
        return JsonSerializer.Serialize(info);
    }

    [McpServerTool, Description("Zip or unzip an archive. action: zip|unzip. Requires confirm:true.")]
    public async Task<string> Archive(
        [Description("Action: zip or unzip")] string action,
        [Description("Source path (directory to zip, or zip file to unzip)")] string src,
        [Description("Destination path (zip file to create, or directory to extract to)")] string dst,
        [Description("Must be true to confirm zip or unzip")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for zip/unzip");
        switch (action.ToLowerInvariant())
        {
            case "zip":
                await _fs.ZipAsync(src, dst, ct);
                return $"zipped '{src}' to '{dst}'";

            case "unzip":
                await _fs.UnzipAsync(src, dst, ct);
                return $"unzipped '{src}' to '{dst}'";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected zip|unzip");
        }
    }
}
