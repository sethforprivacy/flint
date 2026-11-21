[← Docs index](README.md)

# Operations: SparkLedger SDK Backfill

One-time runbook for the v1.3.1.3 SDK-primary backfill deployment.

## Shell patterns

**All server commands use `ssh host 'bash -s' < script.sh`.** Never embed
single-quoted SQL inside `ssh '...'` strings — the outer quotes prevent heredoc
expansion and corrupt the SQL. Write scripts with `set -euo pipefail` and
pipe them in.

**`pg_dump -n`** fails on schema names that contain dots (e.g.
`BTCPayServer.Plugins.Flint`). Use `-Fc` (custom format) for a full-database
dump; `pg_restore --list` then filters by object name.

**The EF migrations history table** is
`"BTCPayServer.Plugins.Flint"."BTCPayServer.Plugins.Flint"`, not
`__EFMigrationsHistory`. BTCPay plugins namespace their history table under
the plugin's own schema.

**`$PLUGIN_DIR` and other shell variables** set in the calling shell are not
available inside `ssh 'bash -s' < script.sh` runs — the script runs in the
remote shell. Put all variable assignments inside the script.

---

## A — Backup

```bash
cat > /tmp/flint-backup.sh <<'EOF'
set -euo pipefail
D=/root/btcpay-backups/$(date +%Y-%m-%d)
mkdir -p "$D"
echo "[A1] pg_dump custom-format..."
docker exec generated_postgres_1 pg_dump -U postgres -d btcpayservermainnet -Fc -f /tmp/full.dump
echo "[A2] SparkLedger objects in dump:"
docker exec generated_postgres_1 pg_restore --list /tmp/full.dump | grep -c SparkLedger
echo "[A3] copy dump to host..."
docker cp generated_postgres_1:/tmp/full.dump "$D/btcpayservermainnet.dump"
docker exec generated_postgres_1 rm /tmp/full.dump
echo "[A4] result:"
ls -lh "$D"
EOF
ssh btcpay-lunanode 'bash -s' < /tmp/flint-backup.sh
```

---

## B — Delete backfill rows (guard: 40 backfill, 0 live)

```bash
cat > /tmp/flint-delete.sh <<'EOF'
set -euo pipefail
cat > /root/flint-delete.sql <<'EOSQL'
DO $$
DECLARE cnt_backfill int; cnt_live int;
BEGIN
  SELECT COUNT(*) INTO cnt_backfill FROM "BTCPayServer.Plugins.Flint"."SparkLedger" WHERE "IsBackfill" = true;
  SELECT COUNT(*) INTO cnt_live     FROM "BTCPayServer.Plugins.Flint"."SparkLedger" WHERE "IsBackfill" = false;
  IF cnt_backfill <> 40 THEN RAISE EXCEPTION 'STOP: expected 40 backfill rows, found %', cnt_backfill; END IF;
  IF cnt_live > 0  THEN RAISE EXCEPTION 'STOP: % live rows exist', cnt_live; END IF;
  DELETE FROM "BTCPayServer.Plugins.Flint"."SparkLedger" WHERE "IsBackfill" = true;
  RAISE NOTICE 'Deleted % backfill rows', cnt_backfill;
END $$;
EOSQL
docker exec -i generated_postgres_1 psql -U postgres -d btcpayservermainnet -v ON_ERROR_STOP=1 < /root/flint-delete.sql
rm /root/flint-delete.sql
docker exec generated_postgres_1 psql -U postgres -d btcpayservermainnet \
  -c 'SELECT COUNT(*) FROM "BTCPayServer.Plugins.Flint"."SparkLedger";'
EOF
ssh btcpay-lunanode 'bash -s' < /tmp/flint-delete.sh
```

If the DO-block raises `STOP:`, do not proceed — investigate and report.

---

## C — Copy 1.3.1.2 plugin for rollback

```bash
cat > /tmp/flint-rollback-copy.sh <<'EOF'
set -euo pipefail
D=/root/btcpay-backups/$(date +%Y-%m-%d)
SRC=/var/lib/docker/volumes/generated_btcpay_pluginsdir/_data/BTCPayServer.Plugins.Flint
if [ ! -d "$SRC" ]; then
  echo "ERROR: plugin dir not found at $SRC"
  find /var/lib/docker/volumes -maxdepth 4 -name "BTCPayServer.Plugins.Flint" -type d 2>/dev/null
  exit 1
fi
cp -a "$SRC" "$D/Flint-$(date +%Y%m%d).bak"
ls -lh "$D/Flint-$(date +%Y%m%d).bak"
EOF
ssh btcpay-lunanode 'bash -s' < /tmp/flint-rollback-copy.sh
```

---

## D — Upload via UI

Upload the `.btcpay` via **BTCPay → Server Settings → Plugins → Upload** and restart when prompted.

The `.btcpay` is built locally by PluginPacker when GitHub Actions are unavailable:

```bash
cd flint-main
target_dir="$(dotnet build BTCPayServer.Plugins.Flint/BTCPayServer.Plugins.Flint.csproj \
  --configuration Release -t:Build -getProperty:TargetDir 2>/dev/null)"
mkdir -p ~/Releases/flint/1.3.1.3
dotnet run --configuration Release \
  --project btcpayserver/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj -- \
  "$target_dir" BTCPayServer.Plugins.Flint ~/Releases/flint/1.3.1.3
```

---

## E — Verify migrations after restart

```bash
cat > /tmp/flint-verify.sh <<'EOF'
set -euo pipefail
echo "[E1] migrations:"
docker exec generated_postgres_1 psql -U postgres -d btcpayservermainnet \
  -c 'SELECT "MigrationId" FROM "BTCPayServer.Plugins.Flint"."BTCPayServer.Plugins.Flint" ORDER BY 1;'
echo "[E2] recent errors in BTCPay log:"
docker logs generated_btcpayserver_1 --since 10m 2>&1 \
  | grep -iE 'error|exception|flint' | tail -20 || true
EOF
ssh btcpay-lunanode 'bash -s' < /tmp/flint-verify.sh
```

Expected migrations:
- `20261113180000_SparkLedger`
- `20261121000000_SparkLedgerDescription`

---

## F — dryRun backfill (run by operator with a scoped key)

The key needs `CanModifyStoreSettings` on each store (not global).

```bash
BTCPAY_URL=https://btcpay.btcfactura.com
read -rs BTCPAY_KEY

# BTCfactura
printf 'Authorization: token %s\n' "$BTCPAY_KEY" | \
  curl -sS --fail-with-body -H @- -X POST \
  "$BTCPAY_URL/api/v1/stores/FjT9PaKKot8EGhdMXXxKVCxEX1nzcaQDfroSgKtaz6Vr/spark/ledger/backfill?dryRun=true" \
  | python3 -m json.tool

# PPay
printf 'Authorization: token %s\n' "$BTCPAY_KEY" | \
  curl -sS --fail-with-body -H @- -X POST \
  "$BTCPAY_URL/api/v1/stores/6iLTZTW8Sr1CxNxTApSLzpvTdJbDD7jkYD7A9cWGa2iz/spark/ledger/backfill?dryRun=true" \
  | python3 -m json.tool
```

Response fields (each with `skipped`, `created`, `failed`):
`dryRun`, `storeId`, `lightningReceives`, `stablecoinReceives`, `sends`,
`sweeps`, `unilateralExits`, `total`.

No reconciliation (balance) in the response — verify balance after the real
run with the DB query in step G.

---

## G — Verify reconciliation in DB (after real run)

```bash
cat > /tmp/flint-reconcile.sh <<'EOF'
set -euo pipefail
docker exec generated_postgres_1 psql -U postgres -d btcpayservermainnet << 'EOSQL'
SELECT
  "StoreId",
  "IsBackfill",
  COUNT(*)                                                        AS cnt,
  SUM(CASE WHEN "EntryType" IN (0,1,5) THEN "AmountMsat" ELSE 0 END) / 1000 AS credits_sats,
  SUM(CASE WHEN "EntryType" IN (2,3,4) THEN "AmountMsat"+"FeeMsat" ELSE 0 END) / 1000 AS debits_sats
FROM "BTCPayServer.Plugins.Flint"."SparkLedger"
GROUP BY "StoreId", "IsBackfill"
ORDER BY "StoreId", "IsBackfill";
EOSQL
EOF
ssh btcpay-lunanode 'bash -s' < /tmp/flint-reconcile.sh
```

EntryType key: `0`=LightningReceive, `1`=StablecoinReceive, `2`=Send,
`3`=Sweep, `4`=UnilateralExit, `5`=OnchainDeposit.
