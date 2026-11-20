# SOL Wallet

Personal Solana wallet for one keypair. You keep the keypair file. The service signs native SOL transfers locally, exposes a small HTTP API, and gives the admin a Telegram UI.

Use it to pay people straight from your own wallet: queue payouts over the API, or send one withdrawal from Telegram. You pay the Solana network fee. There is no exchange withdrawal fee, because the SOL never sits on an exchange account.

Balances and amounts in this service are native SOL. `1 SOL = 1_000_000_000` lamports. The API accepts a payout immediately. A background worker signs and broadcasts the next one, then waits until the signature is finalized before taking the next one.

The HTTP API listens on `http://127.0.0.1:5101` only.

## What it does

- Shows the wallet address, balance, and recent transactions.
- Queues SOL payouts through `POST /api/sol/payouts`.
- Sends one transfer at a time, in order, and confirms it on-chain before taking the next one.
- Saves the signed transaction before broadcasting it. After an RPC timeout it checks that same signature again instead of signing a second transfer.
- Rebroadcasts the same signed transaction while its blockhash is still valid.
- Puts anything uncertain or expired-and-ambiguous in `NeedsReview` and stops signing until that row is resolved.
- Notifies the Telegram admin about queued payouts, failures, and incoming SOL.
- Lets that same admin check the wallet, withdraw, pause payouts, and toggle success alerts from a private chat.

`requestId` is an idempotency key. Sending the same payout twice returns the existing record. Sending the same id with different details is rejected.

Optional `comment` is written with the Solana Memo program. It is plain text, at most 123 UTF-8 bytes.

## Stack

| Piece | Used for |
| --- | --- |
| .NET 10, ASP.NET Core | HTTP API, hosted workers, configuration |
| PostgreSQL, EF Core 10, Npgsql | Payout queue, settings, deposit dedup, EF migrations applied on startup |
| [Solana.Rpc](https://www.nuget.org/packages/Solana.Rpc) / [Solana.Programs](https://www.nuget.org/packages/Solana.Programs) 10.0.0 | Keypair loading, transaction build, and local ed25519 signing |
| Solana JSON-RPC | Balance, transactions, blockhash, broadcast, signature status |
| [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) | Admin UI and payout alerts |
| [Solana Explorer](https://explorer.solana.com) | Explorer links in API responses and Telegram |

Signing checks that the Solana CLI keypair derives `SOLANA_WALLET_ADDRESS`. It never generates or replaces a keypair. Only one process holds the wallet lock, so two copies cannot sign at the same time.

## Requirements

- .NET 10 SDK
- PostgreSQL database named `DBName`
- A Solana address on mainnet, devnet, or testnet
- The Solana CLI keypair JSON for that address (a 64-number array), if you want signing. Without it the API can still read address, balance, and transactions. Payout creation returns `503`.
- A Telegram bot token and your numeric Telegram user id, if you want the bot. The user id is required even when the bot token is empty.
- One RPC URL. There is no fallback endpoint.

## Configure

Copy `.env.example` to `.env` in the project directory, or export the same variables in the environment. Real environment variables win over `.env`. Do not commit `.env`.

| Variable | Required | Meaning |
| --- | --- | --- |
| `SOLANA_NETWORK` | yes | `mainnet`, `devnet`, or `testnet`. `mainnet-beta` is accepted as `mainnet`. |
| `SOLANA_RPC_URL` | yes | One absolute `http` or `https` RPC URL. |
| `SOLANA_WALLET_ADDRESS` | yes | Base58 address. Must match the keypair. |
| `SOLANA_KEYPAIR_PATH` | for payouts | Absolute path to the Solana CLI keypair JSON. Leave empty to disable signing. |
| `API_KEY` | yes | 16–256 characters, no spaces. Clients send it as `X-Api-Key`. |
| `TELEGRAM_BOT_TOKEN` | no | Bot token from [@BotFather](https://t.me/BotFather). Empty disables the bot. Alerts then go to the log. |
| `TELEGRAM_ADMIN_ID` | yes | Numeric Telegram user id of the only person allowed to use the bot. |
| `SOLANA_PAYOUT_ENABLED` | no | `true` or `false`. Default `false`, so the first start keeps payouts paused until the admin resumes them. Ignored after settings already exist in Postgres. |
| `SOLANA_MAX_PAYOUT` | no | Largest single payout in SOL. Default `1`. Up to 9 decimal places. |
| `SOLANA_MIN_RESERVE` | no | SOL that must stay on the wallet. Default `0.02`. |
| `SOLANA_MAX_PRIORITY_FEE_LAMPORTS` | no | Cap on the priority fee for one transfer. Default `10000`. Integer from 0 to 1000000000. |
| `SOLANA_FEE_BUFFER_LAMPORTS` | no | Extra lamports reserved per queued payout, on top of the 5000 lamport signature fee and the priority fee. Default `20000`. |
| `SOLANA_CONFIRMATION_COMMITMENT` | no | `processed`, `confirmed`, or `finalized`. Default `finalized`. A payout is confirmed only at this commitment, after the destination and amount are checked. |
| `DATABASE_CONNECTION_STRING` | yes | Npgsql connection string. `Database` must be `DBName`. |

Each queued or in-flight payout reserves `amount + 5000 + max priority fee + fee buffer` lamports, and recently confirmed payouts stay reserved for 45 seconds so the balance can catch up.

Create the database, then run:

```bash
dotnet run
```

Startup applies EF migrations. A ready process logs that it is listening on `http://127.0.0.1:5101`.

`dotnet run -- --self-check` checks SOL parsing, address parsing, and payout recovery rules, then exits. It does not read `.env`, open Postgres, or call Solana.

On the server, the `solana-wallet` service user must be able to read the keypair file. The service does not create that file.

## API

`/health` is open. Everything under `/api` needs the header `X-Api-Key`. Comparison is constant-time. A bad or missing key is `401`:

```json
{ "error": "Unauthorized." }
```

Errors use that same `{ "error": "..." }` body. JSON is camelCase. Null fields are omitted.

`balanceNano`, `feeNano`, and `amountNano` are lamport integers. The decimal fields are SOL.

### `GET /health`

No API key.

```json
{ "status": "ok" }
```

### `GET /api/sol/address`

```json
{
  "address": "Eij...",
  "bounceableAddress": "Eij...",
  "rawAddress": "Eij...",
  "network": "devnet",
  "explorerUrl": "https://explorer.solana.com/address/Eij...?cluster=devnet"
}
```

Solana has one address form, so `address`, `bounceableAddress`, and `rawAddress` are the same base58 public key. Use that when sending SOL to this wallet.

### `GET /api/sol/balance`

```json
{
  "address": "Eij...",
  "network": "devnet",
  "balance": "1.5",
  "balanceNano": "1500000000",
  "status": "active",
  "initialized": true,
  "retrievedAtUtc": "2026-11-20T12:00:00+00:00"
}
```

`status` is `active` when the account exists and `nonexist` when it does not. A missing account can still receive SOL.

### `GET /api/sol/transactions?limit=10&offset=0`

`limit` is 1–50, default 10. `offset` is zero or greater. The service walks Solana's newest-first signature list, up to 1000 signatures.

```json
{
  "limit": 10,
  "offset": 0,
  "hasMore": false,
  "transactions": [
    {
      "hash": "signature",
      "logicalTime": "12345",
      "timestampUtc": "2026-11-20T12:00:00+00:00",
      "fee": "0.000005",
      "feeNano": "5000",
      "aborted": false,
      "endStatus": "confirmed",
      "explorerUrl": "https://explorer.solana.com/tx/signature?cluster=devnet",
      "messages": [
        {
          "direction": "incoming",
          "amount": "0.025",
          "amountNano": "25000000",
          "counterparty": "Eij...",
          "opcode": "transfer",
          "bounced": false
        }
      ]
    }
  ]
}
```

`hash` is the transaction signature. `logicalTime` is the slot. `direction` is `incoming`, `outgoing`, or `other`. `bounced` is always false.

### `POST /api/sol/payouts`

Body limit is 4 KB. Amount is a decimal string or a JSON number, up to 9 decimal places.

```bash
curl -sS -X POST http://127.0.0.1:5101/api/sol/payouts \
  -H "X-Api-Key: $API_KEY" \
  -H "Content-Type: application/json" \
  -d "{\"requestId\":\"8f1c2a10-6c3e-4a7b-9d2e-1b0c4f5a6e7d\",\"address\":\"Eij...\",\"amount\":\"0.025\",\"comment\":\"withdrawal-123\"}"
```

`202 Accepted`:

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Queued"
}
```

`Location` is `/api/sol/payouts/{id}`.

The worker will not sign while payouts are paused, while another payout is still in flight, while any payout is `NeedsReview`, or while the on-chain wallet check fails. Creation itself only queues the row when the spendable balance covers the amount, the fee reserve, and `SOLANA_MIN_RESERVE`.

| Status | When |
| --- | --- |
| `400` | Bad JSON, `requestId` is not a UUID, address or amount is invalid, amount is above `SOLANA_MAX_PAYOUT`, comment is too long, or the destination is this same wallet. |
| `401` | Missing or wrong `X-Api-Key`. |
| `409` | `requestId` already belongs to a different payout, or the wallet cannot cover this payout. |
| `429` | Solana RPC rate limit. |
| `502` | Solana RPC rejected the balance read or returned something unreadable. |
| `503` | Signing is not configured, or the wallet failed its local or on-chain check. |
| `504` | Solana RPC timed out. |

### `GET /api/sol/payouts/{id}`

`id` is the payout UUID, not `requestId`.

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "requestId": "8f1c2a10-6c3e-4a7b-9d2e-1b0c4f5a6e7d",
  "address": "Eij...",
  "amount": "0.025",
  "comment": "withdrawal-123",
  "status": "Confirmed",
  "transactionHash": "signature",
  "explorerUrl": "https://explorer.solana.com/tx/signature?cluster=devnet",
  "attempts": 1,
  "createdAtUtc": "2026-11-20T12:00:00+00:00",
  "updatedAtUtc": "2026-11-20T12:00:20+00:00",
  "confirmedAtUtc": "2026-11-20T12:00:20+00:00"
}
```

`transactionHash` is the Solana signature. Unknown id is `404`. A non-UUID id is `400`.

### `GET /api/sol/payouts?limit=20&offset=0`

`limit` is 1–50, default 20. The body is `{ limit, offset, hasMore, payouts }` and each item matches the single-payout object.

### Payout statuses

| Status | Meaning |
| --- | --- |
| `Queued` | Stored. Waiting for the worker. |
| `Signed` | Transaction is signed and stored. Not treated as broadcast yet. |
| `Broadcasted` | Sent to Solana RPC, or the RPC already knew this signature. Waiting for confirmation. |
| `Confirmed` | The signature reached the configured commitment. Destination delta, source debit, and memo were checked. |
| `Failed` | Terminal failure. `lastError` says why. The signature was not spent twice. |
| `NeedsReview` | The chain state does not safely match this payout. Nothing else is signed until this row is changed. |

A Solana transaction can still land until the finalized block height passes `lastValidBlockHeight`. The worker rebroadcasts the same signed bytes until then. A missing signature, an RPC timeout, a restart, or an expired blockhash then becomes `NeedsReview`. The worker does not sign a replacement. `getSignatureStatuses` returning null is not proof the earlier transaction failed.

To unblock the queue after you have checked the signature on the explorer, update that row yourself:

```sql
-- landed: destination received the SOL
update payouts
set status = 'Confirmed', confirmed_at_utc = now(), last_error = null
where id = 'PAYOUT_UUID' and status = 'NeedsReview';

-- did not land
update payouts
set status = 'Failed', last_error = 'Resolved after review.'
where id = 'PAYOUT_UUID' and status = 'NeedsReview';
```

Do not queue the same payment again until you know the first signature cannot land.

## Telegram

Set `TELEGRAM_BOT_TOKEN` and open a private chat with the bot. Anyone else gets "This bot is private." Group chats are ignored except for a pointer to the private chat.

The menu shows network, address, and balance.

| Action | What it does |
| --- | --- |
| Address | The wallet address, plus a Solana Explorer link. |
| Balance | SOL balance and account status. |
| Transactions | Pages of 10, with counterparties and amounts. |
| Withdraw | Address, amount, optional memo, then an explicit confirm. Uses the same payout queue and limits as the API. |
| Payouts | Recent payouts and their statuses. |
| Payout settings | Pause, resume, and toggle success alerts. |
| Refresh | Reloads the menu. |

Commands: `/start`, `/address`, `/balance`, `/transactions`, `/payouts`, `/settings`.

Incoming finalized SOL deposits are watched about every 15 seconds. The signature is stored in Postgres before the alert is treated as sent, so a restart does not announce the same deposit twice. The first start arms the cursor at the latest signature and stays quiet about older transfers.

## Safety

- The keypair, bot token, API key, and database password stay in the environment or the keypair file. They are not in `appsettings.json`.
- Logs redact configured secrets, keypair JSON, and long base64 blobs.
- The API is bound to localhost. Reach it from another machine with an SSH tunnel, or put a reverse proxy in front of it. Keep `X-Api-Key` on that hop.
- `SOLANA_PAYOUT_ENABLED=false` on a fresh database leaves payouts paused. Resume them from Telegram after you have checked the address and the max payout.
- Treat `NeedsReview` as a stop. Do not send that payout again until you have looked at the signature on Solana Explorer.
- Native SOL only. No SPL tokens.

## SSH tunnel

```bash
ssh -N -L 5101:127.0.0.1:5101 user@host
```

Leave the command running, then call the API as if it were local:

```bash
curl -sS http://127.0.0.1:5101/health
curl -sS http://127.0.0.1:5101/api/sol/balance -H "X-Api-Key: $API_KEY"
```

If local port `5101` is already in use:

```bash
ssh -N -L 15101:127.0.0.1:5101 user@host
curl -sS http://127.0.0.1:15101/health
```

## Devnet

1. Point `SOLANA_NETWORK` at `devnet` and `SOLANA_RPC_URL` at `https://api.devnet.solana.com`.
2. Use the devnet keypair path in `SOLANA_KEYPAIR_PATH`. The derived address must equal `SOLANA_WALLET_ADDRESS`.
3. Create database `DBName`.
4. Leave `SOLANA_PAYOUT_ENABLED=false`.
5. Run `dotnet run`.
6. Open the bot, check the address and balance, then resume payouts from Payout Settings when you actually want to send.
7. Fund the wallet with the Solana CLI, from the machine that holds the keypair: `solana airdrop 1 <address> --url devnet`.
8. Send a small payout, such as `0.025`, and wait until the status is `Confirmed` before sending the next one.

This service does not send a transfer by itself on startup.

## Production

GitHub Actions on `main` publishes the app and restarts it on a host over SSH. `EC2_HOST`, `EC2_PORT`, `EC2_USER`, and `EC2_SSH_KEY` are Actions secrets. The process runs as `solana-wallet` from `/opt/solana-wallet` and reads `/env/solana-wallet.env`. The health check is `GET http://127.0.0.1:5101/health` on that machine.

This unit does not touch the `gram-wallet` service, its directory, or its env file.
