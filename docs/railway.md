[<- Docs index](README.md)

# Deploying on Railway

This page covers the parts of a Railway deployment that are specific to Flint.
For general BTCPay Server on Railway guidance, consult the BTCPay docs.

## Persistent volume

Flint writes per-store SDK state (SQLite databases, key material) and the SDK
log to the BTCPay DataDir:

```
<DataDir>/Plugins/Flint/<storeId>/   # SDK state per store
<DataDir>/Plugins/Flint/logs/        # sdk.log
```

All of this must survive a deploy or a container restart. In Railway, attach a
volume to your BTCPay service and mount it at the DataDir path. The DataDir is
usually `/var/lib/btcpay` in Docker images, but verify against the image you
use.

**Railway volume settings (recommended):**
- Size: start at 5 GB and adjust; each active store's SDK state is small but
  the log grows without bound at `info` level on a busy server.
- Mount path: wherever `$BTCPAY_DATADIR` points in your image, typically
  `/var/lib/btcpay`.

Without a persistent volume, every deploy wipes the SDK state and the plugin
must re-sync each store's wallet from scratch. The mnemonic is encrypted and
stored in BTCPay's Postgres database, so the funds are safe, but the initial
sync takes a few minutes and sweeps are paused while it runs.

## Environment variables

| Variable | Notes |
|---|---|
| `BTCPAY_DATADIR` | Absolute path to the volume mount. Verified by `GET /api/v1/stores/{storeId}/spark` as `storageDirectory`. |
| `BTCPAY_POSTGRES` | Standard BTCPay Postgres connection string. Flint's own schema (`plugin_flint`) is created in this database. |

No Flint-specific environment variables are required. All configuration is per
store, set via the UI or the Greenfield API.

## Log rotation

The SDK log at `<DataDir>/Plugins/Flint/logs/sdk.log` is not capped or rotated
by the plugin; see [Known limitations](limitations.md#sdk-log-rotation) for the
reason. On Railway, two options:

1. **Container log policy**: if your Railway deployment uses a logging driver
   that captures stdout/stderr and rotates at the platform level, the forwarded
   lines from BTCPay's own logger are already covered. The file-based `sdk.log`
   is a second copy written by the Rust SDK directly, outside any container
   logging policy.

2. **logrotate inside the container**: add `logrotate` to your image and
   install the config from `scripts/flint-logrotate.conf`. The `copytruncate`
   directive is required because the SDK holds the file handle open.

## Health check and readiness

The plugin exposes no dedicated health endpoint. Use BTCPay's own health check
(`GET /api/v1/health`) for the service layer, then `GET
/api/v1/stores/{storeId}/spark` with an API key to verify that `walletRunning`
is `true` for each store after a deploy.

A store whose wallet is not running one minute after startup is worth
investigating in the BTCPay log (search for `Flint` or the store id).

## Typical deploy flow

```bash
# After every Railway deploy, verify each store
BTCPAY=https://your-btcpay.railway.app
KEY=<api-key-with-canmodifystoresettings>

for store_id in store1 store2; do
    status=$(curl -sS -H "Authorization: token $KEY" \
        "$BTCPAY/api/v1/stores/$store_id/spark")
    echo "$store_id: wallet=$(echo "$status" | jq -r .walletRunning), \
         network=$(echo "$status" | jq -r .networkStatus.isOperational)"
done
```

To provision a set of stores from scratch, use `scripts/setup-stores.sh`.

## Secrets

The plugin stores each store's encrypted mnemonic in BTCPay's Postgres database.
The key that decrypts it lives in the DataDir (ASP.NET Data Protection keyring).
Back both up independently:

- Postgres: Railway's automated backups or `pg_dump` on a schedule.
- DataDir: snapshot the Railway volume or `rsync` it to cold storage.

A mnemonic whose decryption key is lost is permanently inaccessible. If the
volume is lost but Postgres is intact, the mnemonic row exists but cannot be
decrypted; the store must be re-provisioned with a new seed, and the old seed
must be used to recover funds separately (via any Spark-compatible wallet).
