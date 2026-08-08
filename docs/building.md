[← Docs index](README.md)

# Building

For development you additionally need:

- .NET SDK 10.0 or later
- Git with submodule support
- PostgreSQL and the other BTCPay development dependencies (via Docker) for running BTCPay locally

Clone with submodules, or initialise them after the fact:

```bash
git clone --recurse-submodules https://github.com/sethforprivacy/flint.git
cd btcpayserver-plugin-spark
```

```bash
# if you cloned without --recurse-submodules
git submodule update --init --recursive
```

Then build:

```bash
dotnet build
```

To produce an installable `.btcpay` the way CI does — BTCPay's own `PluginPacker`, run over the Release
build output — see [`.github/workflows/package.yml`](../.github/workflows/package.yml); the same two
commands work locally:

```bash
target_dir="$(dotnet build BTCPayServer.Plugins.Flint/BTCPayServer.Plugins.Flint.csproj \
  --configuration Release -t:Build -getProperty:TargetDir)"

dotnet run --configuration Release \
  --project btcpayserver/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj -- \
  "$target_dir" BTCPayServer.Plugins.Flint ./packed
```

`-t:Build` is load-bearing: `-getProperty` on its own only *evaluates* the project and skips every
target, so without it you get an output directory with nothing built into it and `PluginPacker` fails on
a missing assembly.

The `btcpayserver` submodule is pinned to a specific stable BTCPay Server release tag (currently
`v2.4.2`). The pin, the `TargetFramework` in
[`BTCPayServer.Plugins.Flint.csproj`](../BTCPayServer.Plugins.Flint/BTCPayServer.Plugins.Flint.csproj)
and `Constants.BuiltAgainstBTCPayServerVersion` must always agree.

`Constants.MinBTCPayServerVersion` is a **separate** number — the declared support floor, and what BTCPay
checks before loading the plugin. It is currently `2.4.1`, the same as the pin. That the two happen to be
equal today is the safe state, not a redundancy: they stay separate constants so a submodule bump can move
the built-against version without silently moving the floor. Raising the floor is a support decision — it
drops every host below the new value — so the update automation bumps the submodule and the built-against
constant and leaves the floor alone.

**Lowering the floor is the dangerous direction, and a clean build plus a green suite is not evidence for
it.** The floor was once set to `2.4.0` on exactly that evidence, and the plugin broke on every 2.4.0 host:
five views use `<vc:title-header />`, and `TitleHeader` only exists from v2.4.1 onwards. A Razor view
component is resolved *by name at render time*, so the compiler emits a string and the unit suite — which
never renders a view — sees nothing. In production the first request to any Spark page threw
`A view component named 'TitleHeader' could not be found`, BTCPay auto-disabled the plugin and restarted,
and every plugin route (MVC and Greenfield) 404'd until it was re-enabled by hand. Lower the floor only
with proof of *runtime* compatibility on a real host of that version — every view rendered — not just
proof that it compiles. `ViewComponentCompatibilityTests` mechanises the view-component part of that check
against whatever tag the submodule is pinned to.
