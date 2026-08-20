using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace BTCPayServer.Plugins.Flint.Models;

/// <summary>
/// The unilateral-exit page: the disclosure, the quote form, the funding instructions, and — once built — the
/// signed transactions the operator has to broadcast by hand.
/// </summary>
/// <remarks>
/// <para>
/// One view model for what reads like five pages, because they are five states of the same object and a
/// merchant should never have to work out which page they are on. Which section renders is decided by
/// <see cref="DisclosureAcknowledged"/> and <see cref="ActiveRecord"/>'s status, never by a query string.
/// </para>
/// <para>
/// <b>Nothing here is a guard.</b> Every field below is either display state read from
/// <see cref="Services.ISparkUnilateralExitService.ReadAsync"/> or a form value posted straight back to the
/// service, which re-validates all of it. The fee-rate bounds and the "acknowledged" flag exist on this type
/// so the page can be honest about what will be accepted, not so the page can accept anything.
/// </para>
/// <para>
/// The quote form's two fields — and the explorer URL, which posts to its own action — are the only members
/// that ever come back off a form. <see cref="StoreId"/> is
/// <see cref="BindNeverAttribute"/> for the reason spelled out on <see cref="Controllers.SparkController"/> —
/// model binding prefers form values over route values, so a bindable store id is a cross-store hole — and
/// every piece of display state is <see cref="ValidateNeverAttribute"/> so a record read out of the database
/// cannot fail this form's validation.
/// </para>
/// </remarks>
public class SparkExitViewModel
{
    [BindNever]
    public string StoreId { get; set; } = string.Empty;

    /// <summary>False hides every form: nothing can be quoted or built without a live wallet.</summary>
    public bool WalletRunning { get; set; }

    /// <summary>
    /// Whether the operator has accepted the disclosure. Stored server-side, so this is a fact about the store
    /// rather than about this render — the quote form is hidden when it is false and the service refuses anyway.
    /// </summary>
    public bool DisclosureAcknowledged { get; set; }

    /// <summary>The Spark balance, for context beside the quote form.</summary>
    public long BalanceSats { get; set; }

    /// <summary>The store's one in-flight exit, or null when there is none.</summary>
    [ValidateNever]
    public UnilateralExitRecord? ActiveRecord { get; set; }

    /// <summary>Newest-first records, terminal ones included, for the history table.</summary>
    [ValidateNever]
    public IReadOnlyList<UnilateralExitRecord> History { get; set; } = [];

    /// <summary>
    /// What the explorer says sits on the active record's funding address, or null for "unknown".
    /// </summary>
    /// <remarks>
    /// Null and zero are different answers and the page renders them differently. Zero means the explorer
    /// answered and the operator has not sent anything yet; null means nobody knows — no explorer is configured
    /// for this network, or the one that is could not be reached — and an operator must not read that as "my
    /// funding has not arrived".
    /// </remarks>
    public long? FundingReceivedSat { get; set; }

    /// <summary>
    /// The largest single confirmed output on the funding address, or null for "unknown".
    /// </summary>
    /// <remarks>
    /// This, and not <see cref="FundingReceivedSat"/>, is the figure the build is judged by: the fee-bumping
    /// transaction spends one outpoint, so five outputs adding up to the requirement fund nothing. The page
    /// compares this one against the requirement for exactly that reason — a merchant reading a sufficient
    /// total beside a "not funded yet" build would conclude the plugin was broken and top up again.
    /// </remarks>
    public long? FundingLargestOutputSat { get; set; }

    /// <summary>
    /// The BIP32 path of the funding key, so the funding sats are recoverable from the seed by hand.
    /// </summary>
    /// <remarks>
    /// Shown because the funding address is on a hardened path of the plugin's own that no other wallet will
    /// derive on its own. An operator who abandons an exit, or whose server dies after they funded one, needs
    /// this string and their recovery phrase to get that money back — and nowhere else in the product prints
    /// it.
    /// </remarks>
    public string? FundingKeyPath { get; set; }

    /// <summary>
    /// The signed transactions of a built exit, as the service read them back. Empty until the build has run.
    /// </summary>
    /// <remarks>
    /// Deserialised by <see cref="Services.ISparkUnilateralExitService"/>, which also owns the write side, so
    /// there is exactly one set of serialiser options for the format. This page never opens the column itself:
    /// a malformed one arrives here as <see cref="TransactionsUnreadable"/> and gets rendered as an
    /// explanation, because an exception thrown inside a Razor template would take the whole page — the
    /// funding address and the history included — with it.
    /// </remarks>
    [ValidateNever]
    public IReadOnlyList<SparkExitTransaction> Transactions { get; set; } = [];

    /// <summary>
    /// True when the record claims to be built but its transaction column could not be read.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than swallowed. That column <em>is</em> the exit — nothing else holds the signed hex —
    /// so an operator seeing an empty table needs to know whether the build produced nothing or whether the
    /// page failed to read it, because those call for opposite next steps.
    /// </remarks>
    public bool TransactionsUnreadable { get; set; }

    /// <summary>
    /// How many leaves the active record's quote pinned, or null with no active record.
    /// </summary>
    /// <remarks>
    /// Shown because it is the only figure that tells an operator how much broadcasting is ahead of them: one
    /// package per branch, each waiting on the previous level's confirmation.
    /// </remarks>
    public int? LeafCount { get; set; }

    /// <summary>
    /// The store's esplora override as currently stored, which is also the explorer form's current value.
    /// </summary>
    /// <remarks>
    /// Rendered into the input rather than left blank on purpose: that form clears the override when it is
    /// posted empty, so a box that showed nothing while an override was set would delete it the first time
    /// somebody pressed Save to change something else.
    /// </remarks>
    public string? EsploraApiUrl { get; set; }

    /// <summary>The chain this server runs on, named in the copy that depends on it.</summary>
    public string NetworkName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this server is on mainnet, which decides how loudly the explorer form is presented.
    /// </summary>
    /// <remarks>
    /// Off mainnet there is no default explorer at all — mempool.space has no regtest — so funding discovery
    /// simply refuses until an override is set. On mainnet the override is a privacy preference. Same form,
    /// two quite different meanings, and the page says which one applies.
    /// </remarks>
    public bool IsMainnet { get; set; }

    /// <summary>Fee rate the exit tree is quoted at, in sat/vB.</summary>
    /// <remarks>
    /// No <c>[Range]</c> attribute. The bounds live in the service, which refuses out-of-range rates on both
    /// surfaces; duplicating them here as validation would mean two numbers to keep in step, and the one that
    /// mattered would be the one nobody edited. The input's <c>min</c>/<c>max</c> are a courtesy to the
    /// merchant in exactly the way the sweep form's are.
    /// </remarks>
    [Display(Name = "Fee rate")]
    public long FeeRateSatPerVbyte { get; set; }

    /// <summary>Where the recovered coins are swept once the tree has been unrolled.</summary>
    /// <remarks>
    /// Baked into the signed sweep transaction, so it cannot be changed after the build — which is why the
    /// service parses it against the store's network before it persists a record, rather than at build time
    /// when the operator has already paid for a funding UTXO.
    /// </remarks>
    [Display(Name = "Destination address")]
    public string? DestinationAddress { get; set; }
}
