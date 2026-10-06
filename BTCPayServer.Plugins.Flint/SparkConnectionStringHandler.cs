using System;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint;

/// <summary>
/// Claims Lightning connection strings of the form
/// <c>type=flint;store-id=&lt;storeId&gt;;key=&lt;random&gt;</c> and resolves them to the
/// store's <see cref="SparkLightningClient"/>.
/// </summary>
/// <remarks>
/// <para><b>Why <c>type=flint</c>, and what was checked before taking a generic word.</b></para>
/// <para>
/// Verified against BTCPay Server v2.4.1 and BTCPayServer.Lightning: no connection-string handler
/// claims <c>spark</c>. The complete set of built-in <c>type=</c> values is <c>clightning</c>,
/// <c>lnd-rest</c>, <c>lnd-grpc</c>, <c>lndhub</c>, <c>phoenixd</c> and <c>eclair</c> — each
/// checked in the corresponding <c>*ConnectionStringHandler.Create</c>, all six registered in
/// <c>Hosting/BTCPayServerServices.cs</c>. The legacy shesek <c>spark-wallet</c> does appear in
/// core, but only as <c>ExternalServiceTypes.Spark</c> in <c>Configuration/ExternalService.cs</c>:
/// that is a Server Settings link with its own unrelated format
/// (<c>server=…;cookiefile=…</c>) and it never reaches a Lightning connection-string handler.
/// </para>
/// <para>
/// So <c>spark</c> is technically free, and we still do not take it, for two reasons. First,
/// <c>ExternalServiceTypes.Spark</c> means "spark" already denotes something else to BTCPay users
/// and operators, and a support conversation about "the Spark connection string" would be
/// ambiguous. Second, another registry plugin ("Spark - Beta", <c>p-i-g-g-y/btcpay-spark</c>) has
/// gone dark with unknown claims on the name; a plugin-level collision on
/// <c>type=</c> is silent and would misroute a merchant's connection string. This was
/// <c>breezspark</c> for exactly that reason: naming the backing SDK left no room for ambiguity.
/// </para>
/// <para>
/// <c>flint</c> is a more generic word and reopens that risk in principle. It was taken knowingly,
/// after confirming nothing claims it: not core's six built-ins above, not the plugin handlers that
/// do exist (<c>breez</c>, <c>micro</c>, <c>app</c>), and nothing in public code search. The
/// judgement is that a plugin author picking a colliding discriminator is their bug to avoid. If one
/// ever does, the fix is to move back to a vendor-specific value rather than to race them for it —
/// a collision here is silent, so it must not be settled by whoever ships last.
/// </para>
/// <para><b>Store binding, and exactly what it does and does not close.</b> The connection string carries
/// both the store id and a random key, and the handler verifies server-side that the key belongs to that
/// store. That closes the <em>key-without-a-store-id</em> hole in the prior-art plugin, where a bare key
/// could be pointed at any wallet.</para>
/// <para>
/// <b>Store binding is enforced now, in two plugin layers, neither of which lives in
/// <see cref="Create"/>.</b> This handler still receives only the string and the network —
/// <see cref="ILightningConnectionStringHandler"/> passes no owning store — so the store is resolved from
/// <em>inside</em> the string and nothing in here can compare it to the store being configured. What
/// closes the cross-store copy is the plugin around this handler. Saving a string on a store it does not
/// belong to is refused by <see cref="SparkLightningClient.Validate"/>, which runs inside the save request
/// where core has placed the configured store on the <c>HttpContext</c>; a configuration that predates
/// that check is cleared by <see cref="Services.SparkLightningConfigSweeper"/>, which runs at startup and
/// about every half hour thereafter, and which also rotates the victim's payment key so previously leaked
/// copies of the string stop resolving. The 2026-08-07 audit's "copy store B's whole string onto store A"
/// reproduction now fails validation on every HTTP save path, and a copy that already exists is swept
/// within about half an hour.
/// </para>
/// <para>
/// The string remains a <b>bearer spend credential</b> within that bound — exactly like an LND macaroon,
/// anyone who holds it may still save it on the store it names — so it still must be treated as a secret,
/// and the residual exposure is this handler's own limitation, unchanged: <see cref="Create"/> resolves
/// from the string alone, so enforcement depends on the two plugin layers, not on the string itself.
/// <see cref="Services.SparkStoreProvisioner"/> rotates the payment key on every provision, so re-running
/// setup revokes every previously issued string; between provisions the string never expires. Do not
/// restate the older claim that store binding closes cross-store hijack outright — it now closes the
/// <em>save</em> paths and sweeps what predates them.
/// </para>
/// <para>There is no <c>server=</c> key. What that means for core's <c>IsSafe</c> check changed in BTCPay 2.4.5
/// (see <see cref="SparkConnectionString.GeneratePaymentKey"/>); the plugin's own provisioning never runs it.</para>
/// <para><b>Why the resolver arrives as a factory rather than as a dependency.</b></para>
/// <para>
/// This class is the one part of the plugin that BTCPay constructs from <em>inside</em> its own
/// container graph: <c>PaymentMethodHandlerDictionary</c> enumerates
/// <c>IEnumerable&lt;IPaymentMethodHandler&gt;</c>, core's <c>LightningLikePaymentHandler</c> takes
/// <c>LightningClientFactoryService</c>, and that takes
/// <c>IEnumerable&lt;ILightningConnectionStringHandler&gt;</c> — which is this. So anything this
/// constructor pulls in is built while core is still building itself, and if any of it leads back to
/// <c>PaymentMethodHandlerDictionary</c> the graph has a cycle. It did: <c>SparkService</c> reaches it
/// through <c>SparkLightningWiring</c>. Because the cycle ran through factory delegates the container
/// could not detect it, so rather than reporting a circular dependency it recursed, and its
/// <c>StackGuard</c> then continued resolution on a second thread while the first waited holding the
/// container's root lock — deadlocking BTCPay's startup before any hosted service or startup task ran.
/// </para>
/// <para>
/// Deferring the lookup to <see cref="Create"/> removes this class from that graph: it is now
/// constructible with no plugin state at all, and by the time a connection string is resolved the
/// container is long since built. Keep it that way — a dependency added to this constructor is a
/// dependency BTCPay constructs mid-graph.
/// </para>
/// </remarks>
public class SparkConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly Func<ISparkClientResolver> _resolver;

    /// <param name="resolver">
    /// Invoked per <see cref="Create"/> call, never at construction. See the remarks on this class for why
    /// that is a correctness requirement and not a style preference.
    /// </param>
    public SparkConnectionStringHandler(Func<ISparkClientResolver> resolver)
    {
        _resolver = resolver;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var parsed = SparkConnectionString.Parse(
            connectionString, out var storeId, out var paymentKey, out var parseError);

        switch (parsed)
        {
            case SparkConnectionStringParseResult.NotOurs:
                // Null error as well as null client: BTCPay only keeps offering the string to other
                // handlers while nobody has reported a problem with it.
                error = null;
                return null;

            case SparkConnectionStringParseResult.Invalid:
                error = parseError;
                return null;
        }

        // Network validation lives in the resolver, alongside the store lookup, because both produce
        // the same kind of "you cannot use this here" message and the resolver is what knows which
        // network this server actually runs on.
        var resolution = _resolver().Resolve(storeId!, paymentKey!, network);
        if (resolution.Client is null)
        {
            // Deliberately does not distinguish "unknown store" from "wrong key": that would turn this
            // handler into an oracle for other stores' payment keys.
            error = resolution.Error ?? "This Spark wallet is not configured for this store";
            return null;
        }

        error = null;
        return resolution.Client;
    }
}
