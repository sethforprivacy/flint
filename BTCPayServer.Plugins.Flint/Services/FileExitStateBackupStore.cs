using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Exit-state backups as one owner-only file per store under
/// <c>&lt;DataDir&gt;/Plugins/Flint/exit-state/&lt;storeId&gt;.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A file, deliberately, not a column.</b> The backup is a multi-megabyte secret, and the store's
/// settings blob is deserialized on every settings read — a several-megabyte value in that column would be
/// carried through the settings cache, cloned on every read, and re-serialized on every save, for a blob
/// nothing reads except on connect. On disk it is read once and written atomically, and a settings write
/// that races a backup can neither lose nor half-overwrite it.
/// </para>
/// <para>
/// <b>A sibling of the SDK's per-store directory, not a child of it.</b> The SDK's directory is handed to
/// the SDK, so the plugin cannot assume it stays stable in shape or existence, and a file the plugin owns
/// next to one it does not is one it can create, restrict and clean up on its own terms.
/// </para>
/// <para>
/// <b>The write is <see cref="File.Move(string,string,bool)"/> over a temporary, always.</b> A
/// half-written backup that imports as a corrupt one is worse than no write at all: the operator would
/// believe their exit data was stored while it could never be restored. The rename is the only way to make
/// "either the old backup or the new one, never a mixture" true without a locking protocol this plugin
/// would then have to get right.
/// </para>
/// <para>
/// <b>Nothing here interprets the content.</b> It is read and written as one string and never parsed,
/// validated or trimmed — see <see cref="IExitStateBackupStore"/> for why that is a hard rule.
/// </para>
/// </remarks>
public sealed class FileExitStateBackupStore : IExitStateBackupStore
{
    /// <summary>The directory every store's backup file lives in.</summary>
    private const string Subdirectory = "exit-state";

    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly ILogger<FileExitStateBackupStore> _logger;

    public FileExitStateBackupStore(
        IOptions<DataDirectories> dataDirectories,
        ILogger<FileExitStateBackupStore> logger)
    {
        _dataDirectories = dataDirectories;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> ReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(storeId);

        // Absent is a fact and returns null; a directory that cannot be read or a file that cannot be
        // opened throws, because a caller that treated "unreadable" as "none stored" would clear a
        // merchant's only exit data on a transient error.
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> TakenAtAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(storeId);
        if (!File.Exists(path))
            return Task.FromResult<DateTimeOffset?>(null);

        // The filesystem's write time is the answer, UTC so the offset is unambiguous. What a reader wants
        // from it is "how stale is this?", and only a UTC stamp can be compared against a store's own
        // monotonic pass times without knowing the host's zone.
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return Task.FromResult<DateTimeOffset?>(written);
    }

    /// <inheritdoc />
    public async Task WriteAsync(string storeId, string backup, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(backup);

        var path = PathFor(storeId);
        var temporary = path + ".tmp";
        EnsureDirectory();

        // Written to a sibling rather than in place, then renamed over the target. The overwrite flag is
        // what makes the move a replace: without it the second backup for a store would throw on the
        // existing file.
        //
        // The guard covers the write as well as the rename, because a half-written backup is debris
        // whichever of the two failed: a full disk, an IO error mid-write, or a cancellation part way
        // through a multi-megabyte blob all leave a partial copy of the secret behind, and nothing else
        // ever removes it. The target is only ever reached by the rename, so a failed pass here leaves the
        // backup that was stored before it exactly as it was — old or new, never a mixture.
        try
        {
            await File.WriteAllTextAsync(temporary, backup, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // The temp has no value once the write or the replace failed, and leaving it beside every
            // store's real backup is how a directory of secrets accumulates debris.
            TryDelete(temporary);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(storeId);
        if (!File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    /// <summary>
    /// The directory this store owns: a sibling of the SDK's per-store storage, created and restricted
    /// exactly as that one is.
    /// </summary>
    internal string StorageDirectory() => Path.Combine(
        _dataDirectories.Value.DataDir, "Plugins", Constants.WorkDirName, Subdirectory);

    /// <summary>One store's backup file.</summary>
    internal string PathFor(string storeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        // Every path in the plugin's own layout is built from a store id BTCPay generated. The id is not
        // attacker-controlled — it is a store's own identifier, resolved from an authorised request — but a
        // store id containing a path separator would place the file outside the owner-only directory this
        // class exists to keep it in, so it is refused rather than reasoned about.
        if (storeId.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || storeId.Contains(Path.DirectorySeparatorChar)
            || storeId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"A store id cannot be used as a file name because it contains a path separator: {storeId}",
                nameof(storeId));
        }

        return Path.Combine(StorageDirectory(), storeId + ".txt");
    }

    private void EnsureDirectory()
    {
        var directory = StorageDirectory();

        // Owner-only from the first instant it exists, never created at the umask and restricted
        // afterwards — the same pairing FileSparkStorageProvider.GetTarget applies to the SDK's directory,
        // so a backup is never momentarily world-readable in the window before a chmod.
        SparkDirectoryPermissions.CreateOwnerOnly(directory);
        SparkDirectoryPermissions.RestrictToOwner(directory, _logger);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            // Nothing names the content: the temp holds a backup, and a failure to delete it is the
            // operator's cleanup problem, not a reason to put the secret in a log line.
            _logger.LogWarning(ex,
                "Could not delete the temporary exit-state backup at {Path}", path);
        }
    }
}
