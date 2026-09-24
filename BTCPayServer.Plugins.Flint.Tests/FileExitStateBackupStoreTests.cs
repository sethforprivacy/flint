using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The real <see cref="FileExitStateBackupStore"/>, over a real temp directory: what it stores is a
/// multi-megabyte secret that has to survive being written, and both halves of that sentence are
/// filesystem behaviour a fake cannot stand in for.
/// </summary>
/// <remarks>
/// No feature gate is involved — the store is storage, not the feature; whether anything is written to
/// it is decided above it, and covered in <c>SparkExitStateAutoBackupTests</c>.
/// </remarks>
public class FileExitStateBackupStoreTests
{
    private const string Store = "store-1";

    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public async Task A_store_with_no_file_reads_as_absent_rather_than_failing()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        Assert.Null(await store.ReadAsync(Store));
        Assert.Null(await store.TakenAtAsync(Store));
        Assert.False(await store.DeleteAsync(Store));
    }

    [Fact]
    public async Task What_is_written_comes_back_and_the_write_leaves_no_temporary_behind()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "the-stored-backup-blob");

        Assert.Equal("the-stored-backup-blob", await store.ReadAsync(Store));

        // The atomic write lands via a `.tmp` sibling renamed over the target. A leftover temp is a
        // half-written backup sitting in the same directory as the real one — an operator reading
        // this directory (and they are the only audience that should) should never find one.
        Assert.Empty(Directory.GetFiles(store.StorageDirectory(), "*.tmp"));

        Assert.NotNull(await store.TakenAtAsync(Store));
    }

    [Fact]
    public async Task A_second_write_replaces_the_first_rather_than_failing_on_the_existing_file()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "first-backup");
        await store.WriteAsync(Store, "second-backup");

        Assert.Equal("second-backup", await store.ReadAsync(Store));
    }

    [Fact]
    public async Task Taken_before_the_first_write_is_null_and_a_deletion_makes_it_null_again()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        Assert.Null(await store.TakenAtAsync(Store));

        await store.WriteAsync(Store, "the-stored-backup-blob");
        var written = await store.TakenAtAsync(Store);
        Assert.NotNull(written);

        // UTC-offset zero, because a caller compares it against its own UTC-sourced clock and an
        // unspecified offset would compare two different zones as if they were one.
        Assert.Equal(TimeSpan.Zero, written!.Value.Offset);

        Assert.True(await store.DeleteAsync(Store));
        Assert.Null(await store.ReadAsync(Store));
        Assert.Null(await store.TakenAtAsync(Store));
    }

    [Fact]
    public async Task The_directory_holds_the_backup_and_nothing_of_the_backup_appears_in_the_log()
    {
        using var dir = new TempDirectory();
        var log = new CapturingLogger<FileExitStateBackupStore>();
        var store = Create(dir, log);
        var secret = "blob-that-must-not-be-logged-9f3a";

        await store.WriteAsync(Store, secret);
        Assert.Equal(secret, await store.ReadAsync(Store));
        await store.DeleteAsync(Store);

        Assert.DoesNotContain(secret, log.AllText);
    }

    [Fact]
    public async Task The_backups_directory_is_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "the-stored-backup-blob");

        // The same hardening the SDK's per-store directory and the log directory get — this one
        // holds, per store, the wallet's whole exit state as one readable string.
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(store.StorageDirectory()));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_backup_file_itself_is_owner_only()
    {
        Assert.SkipWhen(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix file modes.");

        using var dir = new TempDirectory();
        var store = Create(dir);

        await store.WriteAsync(Store, "the-stored-backup-blob");

        // The 0700 directory keeps other accounts out; this is the second line of defence for the
        // one that is already in — the mode is set on the temporary before the rename carries it to
        // the target, so the secret is never world-readable under either name, and a reader that
        // reaches past the directory still meets a file it cannot open.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(store.PathFor(Store)));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_stream_with_nothing_stored_answers_null_and_an_open_one_answers_the_stored_bytes()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        // Null, not an empty stream — the download answers "there is no backup" only to a null, and a
        // zero-byte file would have read as a backup that imports as nothing.
        await using (var absent = await store.OpenReadAsync(Store))
            Assert.Null(absent);

        await store.WriteAsync(Store, "the-stored-backup-blob");

        // The bytes a download hands over, read back off an open handle: this seam never interprets
        // the content, and the streaming read is the same rule — what comes out is what went in.
        await using var stream = await store.OpenReadAsync(Store)
            ?? throw new InvalidOperationException("the stored backup did not open");
        using var reader = new StreamReader(stream);
        Assert.Equal("the-stored-backup-blob", await reader.ReadToEndAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task An_open_download_is_neither_blocked_by_the_next_write_nor_cut_short_by_it()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);
        await store.WriteAsync(Store, "first-backup");

        // The FileShare promise, as a fact about a filesystem rather than a comment: a pass that
        // refreshes the backup halfway through a download renames a new file over the one being
        // served, and the handle already open reads to a clean end of the bytes it opened — the
        // response is one whole backup or another, never an error and never a mixture.
        await using var serving = await store.OpenReadAsync(Store)
            ?? throw new InvalidOperationException("the stored backup did not open");
        await store.WriteAsync(Store, "second-backup-longer");

        using var reader = new StreamReader(serving);
        Assert.Equal("first-backup", await reader.ReadToEndAsync());
    }

    [Fact]
    public void A_store_id_that_could_escape_the_owner_only_directory_is_refused()
    {
        using var dir = new TempDirectory();
        var store = Create(dir);

        // The id is BTCPay's own, not attacker-controlled — but a separator in it would place a
        // backup outside the directory whose permissions are the whole protection, so it is a
        // refusal rather than a judgment call.
        _ = Assert.Throws<ArgumentException>(() => store.PathFor("../elsewhere"));
        _ = Assert.Throws<ArgumentException>(() => store.PathFor(""));
    }

    private static FileExitStateBackupStore Create(
        TempDirectory dir, CapturingLogger<FileExitStateBackupStore>? log = null) =>
        new(Options.Create(new DataDirectories { DataDir = dir.Path }),
            log ?? new CapturingLogger<FileExitStateBackupStore>());

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "spark-backup-store-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
