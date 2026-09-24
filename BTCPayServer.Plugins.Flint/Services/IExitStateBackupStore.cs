using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The stored unilateral-exit state backup of a store, read and written as one opaque string.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for:</b> the transactions an exit is built from live only in the SDK's local storage.
/// While the Spark operators are reachable they can be fetched again; when that storage is gone and the
/// operators are not, they cannot be recovered from anywhere and the leaves they cover can no longer be
/// exited. The blob this store keeps is the copy that survives the device, taken automatically by
/// <see cref="ExitStateBackupTask"/> rather than left to an operator remembering to export one.
/// </para>
/// <para>
/// <b>It is a secret and every implementation must treat it as one.</b> It carries every leaf of the
/// wallet and the transactions under them, which discloses the balance, how it is split, and what the
/// wallet has received and spent. It must never be logged, echoed in an exception message, or returned to
/// a page — an implementation may log the store id and a length, and nothing of the content.
/// </para>
/// <para>
/// <b>Its content is <em>not</em> validated on the way in, anywhere in this plugin.</b> The encoding is
/// the SDK's own and the SDK is the only thing that can judge it; a check invented here would reject a
/// valid backup from a future SDK, and a rejected backup is a lost exit. The only bound any caller applies
/// is a length cap against a wrong paste.
/// </para>
/// <para>
/// This is the only seam through which the backup is read or written. Nothing else opens the file —
/// including nothing that parses it.
/// </para>
/// </remarks>
public interface IExitStateBackupStore
{
    /// <summary>
    /// The stored backup for a store, or null when none is stored.
    /// </summary>
    /// <remarks>
    /// A genuine IO failure is not swallowed into a null: a read that cannot be answered is a different
    /// fact from one that answered "absent", and the caller decides what to do with it.
    /// </remarks>
    Task<string?> ReadAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the stored backup as a readable stream, or null when none is stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The read for a caller that only moves the content.</b> A download wants the file's bytes on a
    /// response and never looks at them; answering it with <see cref="ReadAsync"/> plus an encoding of the
    /// result puts a multi-megabyte secret in memory twice — the string read, then the byte array handed
    /// on — to produce a single pass-through copy. A stream is the same answer a buffer at a time, and it
    /// is the only way this seam serves the blob without holding it.
    /// </para>
    /// <para>
    /// <b>Distinct from <see cref="ReadAsync"/>, not a second spelling of it.</b> The string read is for a
    /// caller that must <em>judge</em> the content — compare it against a paste, import it, decide what it
    /// says; this one is for a caller that must not, and a caller that streams should never read the
    /// stream back into a string.
    /// </para>
    /// <para>
    /// Like <see cref="ReadAsync"/>, an open that cannot be answered is not swallowed into a null:
    /// "absent" is a fact the caller redirects with; "unreadable" is a fault the caller surfaces.
    /// </para>
    /// </remarks>
    Task<Stream?> OpenReadAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// When the backup was written, or null when none is stored.
    /// </summary>
    Task<DateTimeOffset?> TakenAtAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a backup, atomically replacing any previous one.
    /// </summary>
    Task WriteAsync(string storeId, string backup, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the stored backup. Returns whether there was one to remove.
    /// </summary>
    Task<bool> DeleteAsync(string storeId, CancellationToken cancellationToken = default);
}
