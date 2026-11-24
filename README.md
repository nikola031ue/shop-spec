# shop

The ShopHub shop application (Requirements 2.1–2.3): one web app per shop, deployed by
[shop-operator](https://github.com/nikola031ue/shop-operator-spec) for every `Shop` resource.

- **Customers** search items, fill a cart and place orders. An order waits for payment
  (`PendingPayment`); payment is spec 005.
- **The admin** signs in, manages items (name, stock, price) and lists orders.
- **One image** holds the .NET 10 API and the Angular app, and runs on either database the operator
  provides: PostgreSQL (CloudNativePG, `database: standard`) or Redis (`database: light`).

Specification, plan and contracts: `shophub-specs/specs/003-shop-app` (HTTP API:
`contracts/http-api.md`, data model: `data-model.md`).

## Settings

The app reads only environment variables; shop-operator 0.2.0+ sets all of them
(`contracts/operator-0.2.0.md`). Secrets are passed by Secret reference, and their values never
appear in responses or logs.

| Variable | Required | Meaning |
|----------|----------|---------|
| `SHOP_NAME` | ✓ | Shown in the storefront header |
| `WALLET_ADDRESS` | ✓ | Stored on every order as the payment address |
| `ADMIN_PASSWORD` | ✓ | Initial admin password (operator Secret `<shop>-admin`, key `password`) |
| `DATABASE_URL` | one of these two | PostgreSQL URI, e.g. `postgresql://app:pw@host:5432/app` (CNPG Secret `<shop>-db-app`, key `uri`) |
| `REDIS_URL` | one of these two | `redis://host:6379`, no credentials |
| `REDIS_PASSWORD` | with `REDIS_URL` | Redis password (operator Secret `<shop>-redis-auth`) |
| `ORDER_RATE_LIMIT_PER_MINUTE` | – | Orders per minute per client IP; default 10 |

Missing or invalid settings stop the start with a message that names the variable, never its
value. Until the database answers, the app does not open port 80, so the operator's readiness
probe waits.

## The admin password

> ⚠️ **Do not paste the admin password anywhere**: not in chat, tickets, commits or logs.

1. The operator generates the initial password into Secret `<shop>-admin` (24 random characters).
   Read it yourself in your own terminal:

   ```bash
   kubectl get secret <shop>-admin -n <namespace> -o jsonpath='{.data.password}' | base64 -d
   ```

2. Sign in at `/admin`. While the generated password is in use, the admin area asks you to choose
   your own (at least 12 characters).
3. After the change, the Secret no longer matters: the app never resets a password the admin has
   chosen. If the Secret is deleted while the generated password is still in use, the operator
   creates a new one and the app switches to it.

Sessions:

- **Sign-in**: an HttpOnly, `SameSite=Strict` cookie, valid for 8 hours. It works on every
  replica, because the cookie keys are kept in the shop's database.
- **Sign-out and password change** end every admin session: a copied cookie stops working.
- **Lockout**: after 5 wrong passwords within 15 minutes, sign-in is refused for 15 minutes, even
  with the right password. The count is kept in the database, so it applies across replicas. There
  is no override: wait for the lock to expire.

## Order rules

- Prices and totals always come from the shop's item records. The price the customer saw is only
  compared: if it changed, the order is refused (409) and the cart shows the difference.
- Stock goes down when the order is placed, in the same step that stores it. It is not given back
  for unpaid orders yet (spec 005).
- At most 50 lines and 1,000 pieces per item per order. Order placement is limited per client IP
  (`ORDER_RATE_LIMIT_PER_MINUTE`), with 429 and `Retry-After` beyond it.

## Run locally

Requirements: .NET SDK (version in `global.json`), Node 24 (or 22), Docker.

```bash
# A database (pick one)
docker run -d --name shop-pg -p 5432:5432 -e POSTGRES_USER=app -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=app postgres:18.7
docker run -d --name shop-redis -p 6379:6379 redis:7.2.16 redis-server --requirepass dev

# Build the Angular app into src/Shop.Api/wwwroot
(cd src/Shop.Web && npm ci && npx ng build)

# Run the API (serves the app on http://localhost:5080)
export SHOP_NAME="Dev shop" WALLET_ADDRESS=0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed ADMIN_PASSWORD=dev-admin-password
export DATABASE_URL=postgresql://app:dev@localhost:5432/app      # or: REDIS_URL=redis://localhost:6379 REDIS_PASSWORD=dev
dotnet run --project src/Shop.Api
```

After a frontend change, run `npx ng build` again; the API serves the built files.

The image works the same way:

```bash
docker build -t shop:dev .
docker run --rm -p 8080:80 -e SHOP_NAME=… -e WALLET_ADDRESS=… -e ADMIN_PASSWORD=… -e DATABASE_URL=… shop:dev
```

## Tests

```bash
dotnet test --solution Shop.slnx          # unit + integration (Testcontainers: needs Docker)
(cd src/Shop.Web && npx ng lint && npx ng test --watch=false)
dotnet format Shop.slnx --verify-no-changes
```

- One store contract suite (`tests/Shop.Api.Tests/Integration/ShopStoreContract.cs`) runs against
  PostgreSQL 18.7 and Redis 7.2.16 (with a password). The API suite runs through the full app on
  both.
- They include concurrent orders that must never oversell, the lockout, stolen-cookie refusal,
  rate limiting, and a log scan for leaked secrets.

## Layout

```text
src/Shop.Api/        .NET 10 minimal API
  Endpoints/         Public.cs (storefront), Admin.cs, OrderRateLimit.cs
  Auth/              admin sign-in, lockout, session version, Data-Protection keys in the store
  Domain/            items, orders, admin rules
  Storage/           IShopStore + Postgres/ (SQL scripts) + Redis/ (Lua scripts)
src/Shop.Web/        Angular app: store/ (storefront, cart), admin/ (items, orders, password)
tests/Shop.Api.Tests/ unit + Testcontainers integration tests
```

## Releases

Images are published to `ghcr.io/nikola031ue/shop:X.Y.Z` by the `release` CI job when a `vX.Y.Z`
tag is pushed on `main`. The job requires `lint` and `test` to pass, refuses non-SemVer tags and
never overwrites an existing version or publishes `latest`.

To roll a release out, set `shopImage.tag` in kube-state `clusters/local/shop-operator/values.yaml`;
shop-operator then rolls every Shop to the new image. `main` is protected: PRs only, CI
required, linear history.
