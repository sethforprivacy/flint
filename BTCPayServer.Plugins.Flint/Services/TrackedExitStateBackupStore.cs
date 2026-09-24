using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The <see cref="IExitStateBackupStore"/> everything else resolves: the file store, with every write
/// and every delete moving the <see cref="ExitStateBackupScheduler"/>'s belief about what is stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a decorator rather than a call at each writer.</b> The scheduler keeps a per-store hash of
/// the content it believes is stored, and a hash that disagrees with the file is a decision made
/// against a fiction in both directions: a belief left stale by a manual write calls the next due
/// pass's export "unchanged" only until it does not — one stale pass rewrites a multi-megabyte blob to
/// say what the manual write already said — and a belief that survives a deletion lets a pass skip a
/// store whose file no longer exists at all, which is a store silently left without its only
/// device-proof copy. The writers that reach this seam are not one: the page's export, the paste and
/// the clear, the adoption of a backup left at the old settings location, and the scheduled pass all
/// write through it, and a fifth is one feature away from forgetting the scheduler call — because at
/// a call site, <c>store.WriteAsync</c> looks complete without it. Through this type the promise is
/// structural: the only published path to the file carries the bookkeeping with it.
/// </para>
/// <para>
/// <b>Reads stay pure pass-throughs, deliberately.</b> The scheduled pass seeds the scheduler from a
/// <see cref="ReadAsync"/> it makes itself, at the one moment the seed is wanted — first contact with a
/// store after a restart — and hooks here would take that decision away from it: every incidental read
/// (a paste comparison, a page's staleness check, a download) would silently re-seed the belief from
/// whatever was on disk, and a read racing a write would launder a half-truth into the scheduler as
/// knowledge.
/// </para>
/// <para>
/// <b>The belief follows the call's outcome, never its intent.</b> A write that throws leaves the
/// previous file exactly as it was, so the note belongs only after a successful one, with the content
/// that write carried. A delete is the interesting case and its note is unconditional, not
/// <c>if (deleted)</c>: after a delete that returned, the file is absent whether it removed something
/// or found nothing to remove, and "nothing stored" is the only belief true for both answers — a
/// caller that learned "there was none" while keeping the old hash would be keeping a claim about a
/// file it has just confirmed is not there. A delete that throws is different again and notes nothing:
/// whether the file survived a failed removal is unknown, and the previous belief, however stale,
/// beats guessing a store empty when it may hold the wallet's only copy. The consequence of the null
/// is intended and worth stating plainly: a cleared backup is believed absent, so the next due pass
/// treats whatever it exports as changed and writes it — a clear self-heals rather than leaving the
/// store uncovered, which is what the page promises when it says the plugin keeps the backup current
/// on its own.
/// </para>
/// </remarks>
public sealed class TrackedExitStateBackupStore : IExitStateBackupStore
{
    private readonly IExitStateBackupStore _inner;
    private readonly ExitStateBackupScheduler _scheduler;

    public TrackedExitStateBackupStore(
        IExitStateBackupStore inner,
        ExitStateBackupScheduler scheduler)
    {
        _inner = inner;
        _scheduler = scheduler;
    }

    /// <inheritdoc />
    public Task<string?> ReadAsync(string storeId, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(storeId, cancellationToken);

    /// <inheritdoc />
    public Task<DateTimeOffset?> TakenAtAsync(string storeId, CancellationToken cancellationToken = default) =>
        _inner.TakenAtAsync(storeId, cancellationToken);

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string storeId, CancellationToken cancellationToken = default) =>
        _inner.OpenReadAsync(storeId, cancellationToken);

    /// <inheritdoc />
    public async Task WriteAsync(string storeId, string backup, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(storeId, backup, cancellationToken).ConfigureAwait(false);

        // Only after the write landed — noting a failed one would teach the scheduler a file the
        // catch in every caller knows was never written. The content stays a reference long enough to
        // hash inside the scheduler, which keeps only the digest.
        _scheduler.NoteStoredContent(storeId, backup);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var deleted = await _inner.DeleteAsync(storeId, cancellationToken).ConfigureAwait(false);

        // Unconditional — see the type remarks: the call having returned is itself the whole
        // justification, and whether it removed something or not adds nothing to it.
        _scheduler.NoteStoredContent(storeId, null);
        return deleted;
    }
}
