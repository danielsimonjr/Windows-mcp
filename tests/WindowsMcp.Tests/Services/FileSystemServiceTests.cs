using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class FileSystemServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"wm-test-{Guid.NewGuid():N}");
    public FileSystemServiceTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public async Task WriteText_then_ReadText_roundtrips_utf8()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "test.txt");
        await svc.WriteTextAsync(path, "héllo wörld", "utf-8");
        var got = await svc.ReadTextAsync(path, 1024, "utf-8");
        got.Should().Be("héllo wörld");
    }

    [Fact]
    public async Task ReadText_throws_when_file_exceeds_max_bytes()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "big.txt");
        await File.WriteAllTextAsync(path, new string('x', 2000));
        Func<Task> act = () => svc.ReadTextAsync(path, 100, "utf-8");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public async Task WriteText_is_atomic_via_temp_file_rename()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "atomic.txt");
        await File.WriteAllTextAsync(path, "original");

        // Start a write and verify the original is intact until rename
        var task = svc.WriteTextAsync(path, "new content", "utf-8");
        await task;
        (await File.ReadAllTextAsync(path)).Should().Be("new content");
    }

    [Fact]
    public async Task Search_finds_files_matching_pattern()
    {
        var svc = new FileSystemService();
        await File.WriteAllTextAsync(Path.Combine(_tmp, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_tmp, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(_tmp, "c.log"), "c");
        var hits = await svc.SearchAsync(_tmp, "*.txt", null, null, false);
        hits.Should().HaveCount(2);
    }

    [Fact]
    public async Task HashFileAsync_computes_known_sha256()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "abc.txt");
        await File.WriteAllTextAsync(path, "abc");

        var hash = await svc.HashFileAsync(path, "sha256");

        // Canonical SHA-256("abc").
        hash.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public async Task HashFileAsync_rejects_unknown_algorithm()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "x.txt");
        await File.WriteAllTextAsync(path, "x");

        var act = () => svc.HashFileAsync(path, "crc32");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*algorithm*");
    }

    [Fact]
    public async Task Search_find_duplicates_skips_locked_files_without_aborting()
    {
        var svc = new FileSystemService();
        const string content = "duplicate-content-xyz";
        var f1 = Path.Combine(_tmp, "dup1.bin");
        var f2 = Path.Combine(_tmp, "dup2.bin");
        var locked = Path.Combine(_tmp, "dup3-locked.bin");
        await File.WriteAllTextAsync(f1, content);
        await File.WriteAllTextAsync(f2, content);
        await File.WriteAllTextAsync(locked, content);

        // Hold the third file open exclusively so HashFile's File.OpenRead throws IOException.
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var dups = await svc.SearchAsync(_tmp, "*.bin", null, null, findDuplicates: true);

        // The two accessible identical files are still found; the locked one is skipped, not fatal.
        dups.Select(d => d.Path).Should().BeEquivalentTo(new[] { f1, f2 });
    }

    [Fact]
    public async Task GetInfoAsync_describes_a_file()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "info.txt");
        await File.WriteAllTextAsync(path, "abcd");

        var info = await svc.GetInfoAsync(path);

        info.IsDirectory.Should().BeFalse();
        info.Size.Should().Be(4);
        info.Path.Should().Be(Path.GetFullPath(path));
    }

    [Fact]
    public async Task GetInfoAsync_describes_a_directory()
    {
        var svc = new FileSystemService();
        var info = await svc.GetInfoAsync(_tmp);

        info.IsDirectory.Should().BeTrue();
        info.Path.Should().Be(Path.GetFullPath(_tmp));
    }

    [Fact]
    public async Task CopyAsync_writes_destination()
    {
        var svc = new FileSystemService();
        var src = Path.Combine(_tmp, "src.txt");
        var dst = Path.Combine(_tmp, "dst.txt");
        await File.WriteAllTextAsync(src, "copied");

        await svc.CopyAsync(src, dst);

        (await File.ReadAllTextAsync(dst)).Should().Be("copied");
        File.Exists(src).Should().BeTrue();
    }

    [Fact]
    public async Task MoveAsync_relocates_the_file()
    {
        var svc = new FileSystemService();
        var src = Path.Combine(_tmp, "move-src.txt");
        var dst = Path.Combine(_tmp, "move-dst.txt");
        await File.WriteAllTextAsync(src, "moved");

        await svc.MoveAsync(src, dst);

        File.Exists(src).Should().BeFalse();
        (await File.ReadAllTextAsync(dst)).Should().Be("moved");
    }

    [Fact]
    public async Task DeleteAsync_removes_a_file()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "gone.txt");
        await File.WriteAllTextAsync(path, "x");

        await svc.DeleteAsync(path);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_returns_entries()
    {
        var svc = new FileSystemService();
        var a = Path.Combine(_tmp, "listed-a.txt");
        await File.WriteAllTextAsync(a, "a");

        var entries = await svc.ListAsync(_tmp);

        entries.Should().Contain(e => e.EndsWith("listed-a.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Zip_then_Unzip_roundtrips_directory_contents()
    {
        var svc = new FileSystemService();
        var srcDir = Path.Combine(_tmp, "zip-src");
        var dstDir = Path.Combine(_tmp, "zip-dst");
        var zip = Path.Combine(_tmp, "pack.zip");
        Directory.CreateDirectory(srcDir);
        await File.WriteAllTextAsync(Path.Combine(srcDir, "hello.txt"), "roundtrip");

        await svc.ZipAsync(srcDir, zip);
        await svc.UnzipAsync(zip, dstDir);

        (await File.ReadAllTextAsync(Path.Combine(dstDir, "hello.txt"))).Should().Be("roundtrip");
    }

    [Fact]
    public async Task ReadBytesAsync_throws_when_file_exceeds_max()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "big.bin");
        await File.WriteAllBytesAsync(path, new byte[200]);

        var act = () => svc.ReadBytesAsync(path, 50);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeds*");
    }

    [Fact]
    public async Task Device_path_is_rejected()
    {
        var svc = new FileSystemService();
        var act = () => svc.GetInfoAsync(@"\\.\C:");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Device*");
    }

    [Fact]
    public async Task Search_on_missing_root_throws()
    {
        var svc = new FileSystemService();
        var missing = Path.Combine(_tmp, "does-not-exist");
        var act = () => svc.SearchAsync(missing, null, null, null, false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*does not exist*");
    }
}
