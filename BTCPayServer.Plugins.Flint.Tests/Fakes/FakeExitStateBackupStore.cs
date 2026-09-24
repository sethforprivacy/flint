using System.IO;
using System.Text;
using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IExitStateBackupStore"/> for tests whose subject is a <em>caller</em> of the
/// store — what it writes, what it refuses, and what it leaves untouched on failure.
/// </summary>
/// <remarks>
/// Deliberately not used by anything that tests the store's own behaviour (the layout, the atomic
/// replace, the permissions): those tests run the real <see cref="FileExitStateBackupStore"/> over a
/// temp directory, because a fake would be asserting the fake. This one exists so the exit-service
/// tests can watch the calls and script failures without a filesystem in the way.
/// </remarks>
public sealed class FakeExitStateBackupStore : IExitStateBackupStore
{
    private readonly Dictionary<string, string> _files = [];

    /// <summary>Every store id the method was called with, in call order.</summary>
    public List<string> ReadCalls { get; } = [];

    /// <summary>Every store id the method was called with, in call order.</summary>
    public List<string> WriteCalls { get; } = [];

    /// <summary>Every store id the method was called with, in call order.</summary>
    public List<string> DeleteCalls { get; } = [];

    /// <summary>Thrown by every read while set.</summary>
    public Exception? FailReadWith { get; set; }

    /// <summary>Thrown by every write while set.</summary>
    public Exception? FailWriteWith { get; set; }

    /// <summary>The write time every stored file reports; the fake keeps one stamp for all of them.</summary>
    public DateTimeOffset? TakenAt { get; set; }

    /// <summary>What is stored for a store, or null when nothing is. Reads the subject's own writes.</summary>
    public string? Stored(string storeId) => _files.GetValueOrDefault(storeId);

    public Task<string?> ReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ReadCalls.Add(storeId);
        return FailReadWith is { } failure
            ? Task.FromException<string?>(failure)
            : Task.FromResult(Stored(storeId));
    }

    // A fresh stream per call, as a file would give: the caller owns and disposes what it opens. The
    // bytes are the UTF-8 encoding the file store's own read would produce, so a controller served by
    // this fake sees the same payload a controller served by the real store would.
    public Task<Stream?> OpenReadAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (FailReadWith is { } failure)
            return Task.FromException<Stream?>(failure);

        var stored = Stored(storeId);
        return stored is null
            ? Task.FromResult<Stream?>(null)
            : Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes(stored)));
    }

    public Task<DateTimeOffset?> TakenAtAsync(string storeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Stored(storeId) is null ? null : TakenAt);

    public Task WriteAsync(string storeId, string backup, CancellationToken cancellationToken = default)
    {
        WriteCalls.Add(storeId);
        if (FailWriteWith is { } failure)
            return Task.FromException(failure);

        _files[storeId] = backup;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string storeId, CancellationToken cancellationToken = default)
    {
        DeleteCalls.Add(storeId);
        return Task.FromResult(_files.Remove(storeId));
    }
}
