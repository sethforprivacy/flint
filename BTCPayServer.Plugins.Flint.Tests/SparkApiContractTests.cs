using System.Reflection;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The published API contract: what the OpenAPI fragment says must be what the controller does.
/// </summary>
/// <remarks>
/// <para>
/// A hand-written OpenAPI fragment is the price of documenting the things no generator could infer — that a
/// mnemonic is disclosed exactly once, that a balance is indicative, that a <c>200</c> from the sweep endpoint does
/// not mean money moved. The price of <em>that</em> is drift, and drift in an API document is worse than no
/// document: a merchant scripting against a documented permission that is not the enforced one gets a 403 they
/// cannot explain, and a caller reading a documented field name that is not the serialised one gets nulls.
/// </para>
/// <para>
/// So everything mechanically checkable is checked here against the controller and the serializer rather than
/// against a reviewer's attention: the routes, the HTTP methods, the API-key permission each one needs, every
/// member name, and every enum's members.
/// </para>
/// </remarks>
public class SparkApiContractTests
{
    private static readonly Lazy<Task<JObject>> Document = new(() => new SparkSwaggerProvider().Fetch());

    /// <summary>
    /// Every action on the Greenfield controller, and the permission the caller needs for it.
    /// </summary>
    private static IEnumerable<(MethodInfo Method, string Path, string Verb, string Policy)> Operations()
    {
        foreach (var method in typeof(GreenfieldSparkController)
                     .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var route = method.GetCustomAttributes<HttpMethodAttribute>().SingleOrDefault();
            if (route?.Template is null)
                continue;

            var authorize = method.GetCustomAttributes<AuthorizeAttribute>().Single();
            Assert.NotNull(authorize.Policy);

            yield return (
                method,
                // "~/api/…" makes the template absolute rather than relative to a controller route; the document
                // names the path itself.
                route.Template.TrimStart('~'),
                Assert.Single(route.HttpMethods).ToLowerInvariant(),
                authorize.Policy!);
        }
    }

    [Fact]
    public void The_controller_is_a_Greenfield_controller_by_BTCPays_own_conventions()
    {
        var type = typeof(GreenfieldSparkController);

        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());

        var authorize = type.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal(AuthenticationSchemes.Greenfield, authorize!.AuthenticationSchemes);

        Assert.Equal(CorsPolicies.All, type.GetCustomAttribute<EnableCorsAttribute>()?.PolicyName);
    }

    [Fact]
    public void Every_action_states_its_scheme_and_its_policy_rather_than_inheriting_one()
    {
        // Stated per action on purpose: the class-level attribute authenticates but does not authorise, and a read
        // endpoint that silently inherited a write policy — or the other way round — is exactly the mistake worth
        // making impossible to make quietly.
        foreach (var (method, path, verb, policy) in Operations())
        {
            var authorize = method.GetCustomAttributes<AuthorizeAttribute>().Single();
            Assert.Equal(AuthenticationSchemes.Greenfield, authorize.AuthenticationSchemes);

            var isServerLevel = path.StartsWith("/api/v1/server/", StringComparison.Ordinal);
            string expected;
            if (isServerLevel)
                expected = Policies.CanModifyServerSettings;
            else if (verb is "get")
                expected = Policies.CanViewStoreSettings;
            else
                expected = Policies.CanModifyStoreSettings;

            Assert.Equal(expected, policy);

            // Reads may not require a write permission and writes may not accept a read one.
            var requiresWrite = policy == Policies.CanModifyStoreSettings
                                || policy == Policies.CanModifyServerSettings;
            Assert.True(
                verb is "get" || requiresWrite,
                $"{verb.ToUpperInvariant()} {path} changes state but is gated on {policy}");
        }
    }

    [Fact]
    public void The_only_store_id_any_action_binds_comes_from_the_route()
    {
        // The companion to "no request model has a store id": a parameter can name a store too, and a
        // [FromQuery] or body-bound one would be authorised against the caller's own store and then acted on.
        // Core resolves the scope from route data first, so the route parameter is safe and is the only one
        // permitted — and it is pinned to that source explicitly rather than left to binder precedence.
        foreach (var (method, path, verb, _) in Operations())
        {
            foreach (var parameter in method.GetParameters())
            {
                if (!parameter.Name!.Contains("store", StringComparison.OrdinalIgnoreCase))
                    continue;

                Assert.True(
                    parameter.GetCustomAttribute<FromRouteAttribute>() is not null,
                    $"{verb.ToUpperInvariant()} {path} binds '{parameter.Name}' from somewhere other than the "
                    + "route. BTCPay authorises the store from route data, so any other source is a store the "
                    + "caller may not have been authorised for.");

                Assert.Equal("storeId", parameter.Name);
            }
        }
    }

    [Fact]
    public async Task Every_endpoint_is_documented_under_its_own_path_and_verb()
    {
        var document = await Document.Value;
        var paths = Assert.IsType<JObject>(document["paths"]);

        foreach (var (_, path, verb, _) in Operations())
        {
            var operation = paths[path]?[verb];
            Assert.True(
                operation is not null,
                $"{verb.ToUpperInvariant()} {path} is not in the plugin's OpenAPI fragment, so it will not appear "
                + "in BTCPay's API docs.");

            Assert.Equal("Spark", Assert.Single(Assert.IsType<JArray>(operation!["tags"])).Value<string>());
            Assert.False(string.IsNullOrWhiteSpace(operation["summary"]?.Value<string>()));
            Assert.False(string.IsNullOrWhiteSpace(operation["description"]?.Value<string>()));
        }
    }

    [Fact]
    public async Task The_documented_API_key_permission_is_the_one_the_action_enforces()
    {
        // The highest-value assertion in this file. A merchant issues an API key with the permissions the docs name;
        // if those are not the permissions the action is gated on, the key is either useless or over-privileged.
        var document = await Document.Value;
        var paths = Assert.IsType<JObject>(document["paths"]);

        foreach (var (_, path, verb, policy) in Operations())
        {
            var security = Assert.IsType<JArray>(paths[path]![verb]!["security"]);
            var scopes = Assert.IsType<JArray>(Assert.IsType<JObject>(Assert.Single(security))["API_Key"]);

            Assert.Equal(policy, Assert.Single(scopes).Value<string>());
        }
    }

    /// <summary>
    /// The prose of a 403 names the permission the action actually enforces.
    /// </summary>
    /// <remarks>
    /// The companion to the assertion above, and the reason it is not redundant: the machine-readable
    /// <c>security</c> block and the sentence a human reads sit in different places in the same document and
    /// drifted apart in exactly the way you would expect. An external audit found two write endpoints — the
    /// deposit claim and the Stable Balance <c>PUT</c>, both of which move money — telling the reader they were
    /// forbidden to <em>view</em> the store. A merchant debugging a 403 against that sentence would issue a
    /// read-only key and get the same 403 again.
    /// </remarks>
    [Fact]
    public async Task The_403_a_caller_reads_names_the_permission_the_action_enforces()
    {
        var document = await Document.Value;
        var paths = Assert.IsType<JObject>(document["paths"]);

        foreach (var (_, path, verb, policy) in Operations())
        {
            var description = paths[path]![verb]!["responses"]!["403"]!["description"]!.Value<string>()!;
            var reads = policy == Policies.CanViewStoreSettings;

            Assert.True(
                description.Contains(reads ? "view" : "modify", StringComparison.OrdinalIgnoreCase),
                $"{verb.ToUpperInvariant()} {path} is gated on {policy}, but its documented 403 reads "
                + $"\"{description}\".");

            // And not the other word as well, which would make the sentence true of nothing.
            Assert.DoesNotContain(
                reads ? "modify" : "view", description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task No_documented_path_is_missing_from_the_controller()
    {
        // The other direction: a documented endpoint that does not exist is a caller writing a script against
        // nothing.
        var document = await Document.Value;
        var declared = Operations()
            .Select(o => (o.Path, o.Verb))
            .ToHashSet();

        foreach (var path in Assert.IsType<JObject>(document["paths"]).Properties())
        {
            foreach (var verb in Assert.IsType<JObject>(path.Value).Properties())
            {
                Assert.Contains((path.Name, verb.Name), declared);
            }
        }
    }

    /// <summary>
    /// The CLR types the fragment claims to describe, and the schema name each is documented under.
    /// </summary>
    public static TheoryData<string, Type> DocumentedModels() => new()
    {
        { "SparkStatusData", typeof(SparkStatusData) },
        { "SparkNetworkStatusData", typeof(SparkNetworkStatusData) },
        { "SparkProvisionRequest", typeof(SparkProvisionRequest) },
        { "SparkProvisionResponse", typeof(SparkProvisionResponse) },
        { "SparkSweepSettings", typeof(SweepSettingsInput) },
        { "SparkSweepConfigurationData", typeof(SparkSweepConfigurationData) },
        { "SparkSweepRecordData", typeof(SparkSweepRecordData) },
        { "SparkSweepRequest", typeof(SparkSweepRequest) },
        { "SparkSweepResultData", typeof(SparkSweepResultData) },
        { "SparkSweepPreviewData", typeof(SparkSweepPreviewData) },
        { "SparkSweepDestinationData", typeof(SparkSweepDestinationData) },
        { "SparkSweepQuoteData", typeof(SparkSweepQuoteData) },
        { "SparkBalanceSyncData", typeof(SparkBalanceSyncData) }
    };

    [Theory]
    [MemberData(nameof(DocumentedModels))]
    public async Task A_documented_schema_has_exactly_the_members_the_type_serialises(string schemaName, Type type)
    {
        var document = await Document.Value;
        var schema = document["components"]?["schemas"]?[schemaName];
        Assert.True(schema is not null, $"{schemaName} is not in the fragment's components/schemas");

        // Every schema declares additionalProperties: false, which is a claim that the listed members are all of
        // them. That claim is what this test enforces.
        Assert.False(schema!["additionalProperties"]!.Value<bool>());

        var documented = Assert.IsType<JObject>(schema["properties"]).Properties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Asked of the contract resolver rather than of a serialised instance, so a type with required constructor
        // parameters needs no fabricated sample — and so the answer is the serializer's, not this test's guess.
        var contract = Assert.IsType<JsonObjectContract>(ApiJson.Settings.ContractResolver!.ResolveContract(type));
        var serialised = contract.Properties
            .Where(p => !p.Ignored)
            .Select(p => p.PropertyName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(serialised, documented);
    }

    /// <summary>
    /// The CLR enums the fragment claims to describe, and the schema name each is documented under.
    /// </summary>
    /// <remarks>
    /// The enum <em>values</em> matter as much as the names: they cross the wire as strings, and every one of these
    /// properties carries a <c>StringEnumConverter</c> for that reason (BTCPay configures no global one). A member
    /// added to a CLR enum and not to the fragment is an outcome a caller has no way to know can happen.
    /// </remarks>
    public static TheoryData<string, Type> DocumentedEnums() => new()
    {
        { "SparkSeedSource", typeof(SeedSource) },
        { "SparkLightningWiringState", typeof(SparkLightningWiringState) },
        { "SparkSweepDestinationMode", typeof(SweepDestinationMode) },
        { "SparkSweepConfirmationSpeed", typeof(SweepConfirmationSpeed) },
        { "SparkSweepAddressStatus", typeof(SweepAddressStatus) },
        { "SparkSweepTrigger", typeof(SweepTrigger) },
        { "SparkSweepRecordStatus", typeof(SweepRecordStatus) },
        { "SparkSweepOutcomeKind", typeof(SweepOutcomeKind) },
        { "SparkSweepRefusalCode", typeof(SweepRefusalCode) }
    };

    [Theory]
    [MemberData(nameof(DocumentedEnums))]
    public async Task A_documented_enum_has_exactly_the_members_the_type_has(string schemaName, Type type)
    {
        var document = await Document.Value;
        var schema = document["components"]?["schemas"]?[schemaName];
        Assert.True(schema is not null, $"{schemaName} is not in the fragment's components/schemas");

        // Documented as strings because that is how they travel; an integer here would be a lie about the wire.
        Assert.Equal("string", schema!["type"]!.Value<string>());

        var documented = Assert.IsType<JArray>(schema["enum"]).Select(v => v.Value<string>()!).ToList();
        Assert.Equal(Enum.GetNames(type), documented);
    }

    [Fact]
    public async Task Every_schema_the_fragment_references_is_one_it_defines()
    {
        // A dangling $ref renders as an empty box in BTCPay's API docs and breaks every client generator.
        var document = await Document.Value;
        var defined = Assert.IsType<JObject>(document["components"]!["schemas"]!).Properties()
            .Select(p => $"#/components/schemas/{p.Name}")
            .ToHashSet(StringComparer.Ordinal);

        // Core's own fragment supplies these, and BTCPay merges every provider's document into one before serving
        // it, so they are legitimately external.
        var suppliedByCore = new[]
        {
            "#/components/parameters/StoreId",
            "#/components/schemas/ProblemDetails",
            "#/components/schemas/ValidationProblemDetails"
        };

        foreach (var reference in document.Descendants().OfType<JProperty>()
                     .Where(p => p.Name == "$ref")
                     .Select(p => p.Value.Value<string>()!)
                     .Distinct())
        {
            if (suppliedByCore.Contains(reference))
                continue;

            Assert.Contains(reference, defined);
        }
    }

    [Fact]
    public async Task The_disclosure_and_indicative_balance_caveats_are_in_the_published_document()
    {
        // Two properties of this API that a caller cannot discover by trying it, and that cost real money to learn
        // the hard way: the generated phrase is never retrievable again, and the balance is not an accounting
        // figure. They are documented, so they are asserted — a fragment edit that quietly drops them is a
        // regression in the only place a merchant would have read them.
        var document = await Document.Value;
        var schemas = document["components"]!["schemas"]!;

        var mnemonic = schemas["SparkProvisionResponse"]!["properties"]!["mnemonic"]!["description"]!
            .Value<string>()!;
        Assert.Contains("exactly once", mnemonic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never again", mnemonic, StringComparison.OrdinalIgnoreCase);

        var balance = schemas["SparkStatusData"]!["properties"]!["balanceSats"]!["description"]!.Value<string>()!;
        Assert.Contains("Indicative", balance, StringComparison.OrdinalIgnoreCase);

        var sweep = document["paths"]!["/api/v1/stores/{storeId}/spark/sweep"]!["post"]!["description"]!
            .Value<string>()!;
        Assert.Contains("cooperative exit", sweep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not that money moved", sweep, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_in_the_published_document_offers_a_unilateral_exit()
    {
        // The exit-path policy, asserted against the artefact merchants and integrators read. Sweeps are cooperative
        // exits only; a documented path, parameter, field or enum value suggesting otherwise would be a promise the
        // plugin does not keep and must not start keeping by accident.
        //
        // Names and enum values only, deliberately. Prose is allowed to say the word — the drainWhenSweeping
        // description exists precisely to deny that "drain" means a unilateral exit, and a check that banned the
        // word outright would delete the disclaimer rather than the affordance.
        var document = await Document.Value;

        var names = document.Descendants()
            .OfType<JProperty>()
            .Select(p => p.Name)
            .Concat(document.Descendants()
                .OfType<JProperty>()
                .Where(p => p.Name is "enum")
                .SelectMany(p => p.Value.Values<string>()!))
            .ToList();

        Assert.NotEmpty(names);

        foreach (var forbidden in new[] { "unilateral", "forceexit", "force-exit", "forceclose", "force-close" })
        {
            Assert.DoesNotContain(
                names,
                name => name is not null && name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // And the one enum that decides what a sweep does still offers exactly the destinations it should.
        //
        // An exact set rather than a "does not contain" check, so a fourth member cannot be added without
        // someone coming here and justifying it. The three that are here are all cooperative:
        //
        //   StoreWallet, StaticAddress — cooperative exits to a Bitcoin address, via the service provider.
        //   EvmAddress                 — a cross-chain send. At the point money leaves this wallet it is an
        //                                ordinary Spark transfer to the bridge provider's own Spark deposit
        //                                address; the provider then settles on the destination chain. No
        //                                unilateral exit is involved, and no parameter anywhere selects one.
        //
        // A member that could not be described in those terms would be the thing this test exists to stop.
        Assert.Equal(
            new[] { "StoreWallet", "StaticAddress", "EvmAddress" },
            Assert.IsType<JArray>(
                    document["components"]!["schemas"]!["SparkSweepDestinationMode"]!["enum"])
                .Select(v => v.Value<string>()));
    }
}
