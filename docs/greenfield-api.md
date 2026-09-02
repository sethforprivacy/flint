[← Docs index](README.md)

# Automating it: the Greenfield API

Everything the setup, status and sweep pages do is also reachable with a BTCPay **API key**, so a store
can be set up, inspected and swept from a script — for headless mainnet validation, host-level
end-to-end tests, or a merchant who prefers not to click. The endpoints appear in your server's own API
docs at `/docs` and in `/swagger/v1/swagger.json`.

The API is a second *surface*, not a second implementation: it calls the same services the pages call, so
a configuration one accepts is one the other accepts, and a sweep either triggers goes through the same
engine with the same guards.

| Method | Path | API-key permission |
|---|---|---|
| `GET` | `/api/v1/stores/{storeId}/spark` | `btcpay.store.canviewstoresettings` |
| `POST` | `/api/v1/stores/{storeId}/spark` | `btcpay.store.canmodifystoresettings` |
| `DELETE` | `/api/v1/stores/{storeId}/spark` | `btcpay.store.canmodifystoresettings` |
| `GET` | `/api/v1/stores/{storeId}/spark/sweep` | `btcpay.store.canviewstoresettings` |
| `PUT` | `/api/v1/stores/{storeId}/spark/sweep` | `btcpay.store.canmodifystoresettings` |
| `POST` | `/api/v1/stores/{storeId}/spark/sweep` | `btcpay.store.canmodifystoresettings` |
| `POST` | `/api/v1/stores/{storeId}/spark/sync` | `btcpay.store.canmodifystoresettings` |
| `GET` | `/api/v1/stores/{storeId}/spark/deposit` | `btcpay.store.canviewstoresettings` |
| `POST` | `/api/v1/stores/{storeId}/spark/deposit/claim` | `btcpay.store.canmodifystoresettings` |
| `GET` | `/api/v1/stores/{storeId}/spark/stable-balance` | `btcpay.store.canviewstoresettings` |
| `PUT` | `/api/v1/stores/{storeId}/spark/stable-balance` | `btcpay.store.canmodifystoresettings` |

A key scoped to a single store works; `btcpay.store.canmodifystoresettings` covers the read endpoints too.
Basic authentication is accepted wherever an API key is, as everywhere in Greenfield.

Three things are worth knowing before scripting against it.

- **A generated recovery phrase is returned exactly once**, in the response to the `POST` that generated
  it, and never again. There is no endpoint that reads a seed back — the phrase is stored encrypted with
  keys in the BTCPay data directory and nothing reads it out. If your script does not persist that value,
  the store's funds depend entirely on that server's data directory surviving. Do not log the response.
- **`POST .../spark/sweep` answers `200` when the engine reached a decision, not when money moved.** Read
  `outcome`: only `Swept` means a cooperative exit was accepted. `Refused` is a routine steady state — a
  store whose fee ceiling sits below the current exit fee stays there indefinitely and nothing is wrong —
  so gate on `outcome`, and read `refusalCode` for the stable identity of a refusal.
- **`balanceSats` is indicative.** It is read from the SDK's cache without forcing a sync, lags settlement
  by around 20 seconds, and drifts by a few sats. Do not reconcile against it. Use
  `POST .../spark/sync` when you need a current reading.
- **`GET .../spark/sweep` includes a `warnings` array.** On mainnet, it is non-empty when the
  balance threshold or minimum sweep amount are below the recommended defaults (which were measured on
  regtest). Scripts should print this array at provisioning time.

Setting a store up with a fresh seed and reading its state back:

```bash
BTCPAY=https://your.btcpay.host
STORE=<store id>
KEY=<API key with btcpay.store.canmodifystoresettings on that store>
AUTH="Authorization: token $KEY"

# 1. Provision with a newly generated seed. The phrase in this response is the only copy the server
#    will ever hand out, so it is captured here before anything else happens.
curl -sS -X POST "$BTCPAY/api/v1/stores/$STORE/spark" \
  -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"seedSource":"generate"}' | tee /tmp/spark-provision.json | jq '{
        walletRunning: .status.walletRunning,
        lightningWiring: .status.lightningWiring,
        identityPubkey: .status.identityPubkey
      }'

# 2. Save the recovery phrase somewhere durable, then remove the file. It is not retrievable again.
jq -r .mnemonic /tmp/spark-provision.json > ~/spark-store-$STORE.seed
chmod 600 ~/spark-store-$STORE.seed
rm /tmp/spark-provision.json

# 3. Read the status back. `configured` and `walletRunning` should both be true, `lightningWiring`
#    should be "Spark", and `networkStatus.isOperational` should be true.
curl -sS "$BTCPAY/api/v1/stores/$STORE/spark" -H "$AUTH" | jq

# 4. Optional: configure sweeping. PUT is a full replacement, so send every field you want in force.
curl -sS -X PUT "$BTCPAY/api/v1/stores/$STORE/spark/sweep" \
  -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"enabled":true,"balanceThresholdSats":200000,"minimumSweepSats":100000,
       "maxFeePercent":3.0,"drainWhenSweeping":true,"confirmationSpeed":"Medium",
       "destinationMode":"StoreWallet"}' | jq .settings

# 5. Quote a sweep without sending anything: no address is reserved and no record is written.
curl -sS -X POST "$BTCPAY/api/v1/stores/$STORE/spark/sweep" \
  -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"preview":true}' | jq
```

To tear the store's Spark configuration down again — which destroys this server's copy of the keys, and
clears the store's Lightning payment method only if it still points at this store's Spark wallet:

```bash
curl -sS -X DELETE "$BTCPAY/api/v1/stores/$STORE/spark" -H "$AUTH" -i | head -1
```

Force a wallet sync and read a current balance (the SDK cache may lag settlement by ~20 s):

```bash
curl -sS -X POST "$BTCPAY/api/v1/stores/$STORE/spark/sync" -H "$AUTH" | jq
# { "walletRunning": true, "balanceSats": 218432, "syncedAt": "2026-08-23T..." }
```

Configure a sweep webhook and receive a POST after each successful sweep:

```bash
# Add sweepWebhookUrl alongside the other sweep settings.
curl -sS -X PUT "$BTCPAY/api/v1/stores/$STORE/spark/sweep" \
  -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"enabled":true,"balanceThresholdSats":200000,"minimumSweepSats":100000,
       "maxFeePercent":3.0,"drainWhenSweeping":true,"destinationMode":"StoreWallet",
       "sweepWebhookUrl":"https://your.endpoint/spark-swept"}' | jq .settings
```

The payload delivered to the webhook URL:

```json
{
  "storeId": "...",
  "idempotencyKey": "<uuid>",
  "txId": "<on-chain txid>",
  "amountSats": 215000,
  "feeSats": 3000,
  "destination": "bc1q...",
  "destinationMode": "StoreWallet",
  "trigger": "Automatic",
  "completedAt": "2026-08-23T12:34:56.789+00:00"
}
```

Delivery failures are logged as warnings and never retried. The sweep record in the database is the
authoritative source of truth.
