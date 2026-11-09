namespace BTCPayServer.Plugins.Flint.Views;

/// <summary>
/// Menu-item identifiers for the plugin's pages.
/// </summary>
/// <remarks>
/// <para>
/// These are the values passed to <c>ViewData.SetLayoutModel</c> and matched by the
/// <c>layout-menu-item</c> tag helper in <c>SparkNav.cshtml</c>, which is how a page highlights its own
/// entry in the store's Plugins navigation. Strings rather than an enum because the tag helper compares
/// them as strings.
/// </para>
/// <para>
/// One constant per <em>nav entry</em>, not per page: the tag helper also emits the value as an element id
/// (<c>menu-item-{value}</c>), so two entries sharing a constant would produce a duplicate id and light up
/// together. Pages that are reached from a section rather than from the nav — the sweep confirmation and
/// the removal page — borrow the constant of the entry they sit under, which is what keeps that entry
/// highlighted while the merchant is on them. The values are prefixed because the id lands in a document
/// shared with core and every other installed plugin.
/// </para>
/// </remarks>
public static class SparkNavPages
{
    /// <summary>The top-level entry: the status page, or setup while the store has no Spark wallet.</summary>
    public const string Spark = "Spark";

    /// <summary>Sweep configuration and history, and the confirmation page a manual sweep goes through.</summary>
    public const string Sweeps = "SparkSweeps";

    /// <summary>Stable Balance settings. Rendered only where Stable Balance can work — mainnet.</summary>
    public const string StableBalance = "SparkStableBalance";

    /// <summary>Send a Lightning payment from the store's Spark wallet.</summary>
    public const string Send = "SparkSend";

    /// <summary>Receive a Lightning payment: generate a BOLT11 invoice.</summary>
    public const string Receive = "SparkReceive";

    /// <summary>
    /// Wallet details, recovery-phrase provenance and the settings most stores never touch. The deposits and
    /// removal pages borrow this entry: both are reached from the Advanced page rather than from the nav.
    /// </summary>
    public const string Advanced = "SparkAdvanced";
}

/// <summary>
/// The navigation category shared by every Flint page.
/// </summary>
/// <remarks>
/// <para>
/// The same mechanism core's own store-settings nav uses: each page stamps the category onto its
/// <c>LayoutModel</c>, and <c>SparkNav.cshtml</c> renders its sub-entries only while
/// <c>ViewData.IsCategory</c> reports the merchant is inside the section. That is what makes the Flint
/// menu collapse to its one top-level entry everywhere else in BTCPay.
/// </para>
/// <para>
/// Deliberately not a constant on <see cref="SparkNavPages"/>: <c>SparkNavTests</c> reads that class's
/// literal fields as the set of menu-item identifiers, and the category is not a menu item.
/// </para>
/// </remarks>
public static class SparkNavCategory
{
    /// <summary>Namespaced like core's category ids, because it lands in ViewData shared with every plugin.</summary>
    public const string Id = "BTCPayServer.Plugins.Flint.Views.SparkNavPages";
}
