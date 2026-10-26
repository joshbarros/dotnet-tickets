# dotnet-tickets

Ticket tiers and the orders that draw them down. The scarce number is the **tickets left** in a tier (a section of a show), and the **Tiers** module is its only writer: it holds tickets with a single conditional `UPDATE` that matches only while the tickets left still cover the order, so when two buyers fight for the last ticket, one commits a hold and the other is told the tier is short and no hold is written. A buyer reference that contains the poison token `POISON` is committed as rejected by the ETickets module, the message is dead-lettered, and a compensating command gives the tickets back. A later refund also puts the tickets back on sale, but it does not roll back the e-ticket that was already issued.

One ASP.NET Core process, one Postgres database. An HTTP write saves the order and an outbox row in one local transaction; a `BackgroundService` relay publishes outbox rows to RabbitMQ (the work queue) and Kafka (the event log); the next module consumes from RabbitMQ, claims an inbox key in the same transaction as its own write, and writes its own row and outbox row. Saga steps never call each other in-process. Delivery is at-least-once: the relay can publish and crash before it marks a row sent, so the row goes out again, and every consumer dedupes with its inbox key. Each saga step only moves an order forward from the status it expects.

> The Compose network is local and unauthenticated. There is no login, no token and no cookie. Do not publish these ports; Compose binds all of them to `127.0.0.1`.

![The tickets UI after a few orders: one sold, one refunded with its e-ticket kept, one rejected for the poison token, one short](docs/screenshot.png)

*The UI reads everything from the API. Shown: a sold order, a refund that put 2 tickets back on sale and kept its e-ticket, an order rejected for the poison token, and one that was short.*

## Why a conditional update

Selling the same last ticket twice is the classic ticketing bug: check the count, then decrement it later, and two buyers both pass the check. The tier's count is changed by one statement, `UPDATE ... SET TicketsLeft = TicketsLeft - n WHERE TicketsLeft >= n`, and zero rows updated means the order is short. A check constraint (`TicketsLeft` between 0 and the tier's total) is the safety net behind it. The race test was run against a read-then-write version, where it fails.

## Money

Prices are whole cents on the tier. An order costs the face value (price × tickets) plus a 12% service fee, truncated to a whole cent, so two $50.00 tickets cost $112.00. An order is 1 to 6 tickets. The arithmetic is `checked`: it throws instead of wrapping.

## Order statuses

```mermaid
flowchart LR
    Requested -->|tickets held| Held
    Requested -->|short: no hold written| Rejected
    Held -->|e-ticket issued| Sold
    Held -->|poison reference: e-ticket rejected| Rejected
    Held -->|refund| Refunded
    Sold -->|refund| Refunded
```

`Refunded` and `Rejected` (after a hold) put the tickets back through a `ReleaseTickets` command, and Tiers releases a hold at most once. The e-ticket has its own status (`Issued` or `Rejected`) and a refund never changes it. If the refund gets there before the final mark, the late mark finds the order is no longer `Held` and does nothing.

## How the modules talk

```mermaid
sequenceDiagram
    participant C as Client
    participant O as Orders
    participant T as Tiers
    participant E as ETickets

    C->>O: POST /api/orders
    Note over O: order + outbox row, one transaction
    O-->>C: 202 Requested
    O->>T: tickets.reserve (relay → RabbitMQ, copy to Kafka)
    Note over T: inbox claim, conditional UPDATE, hold, outbox row
    alt tickets left cover the order
        T->>E: tickets.held
        T->>O: tickets.held (Requested → Held)
        alt reference is clean
            Note over E: e-ticket, outbox row
            E->>O: eticket.issued (Held → Sold)
        else reference contains POISON
            Note over E: committed Rejected, then the message is dead-lettered
            E->>T: tickets.release (compensating command)
            E->>O: eticket.rejected (Held → Rejected)
            T->>O: tickets.released
        end
    else tier short
        T->>O: tickets.short (Requested → Rejected, no hold)
    end
    C->>O: POST /api/orders/{id}/refund
    O->>T: tickets.release (Held or Sold → Refunded)
    T->>O: tickets.released
```

| Module | Owns | Consumes | Emits |
| --- | --- | --- | --- |
| **Orders** (`Modules/Orders`) | `Orders`: status, tickets, total, buyer reference. Validation, the poison rule, the total. | `tickets.held`, `tickets.short`, `eticket.issued`, `eticket.rejected`, `tickets.released` | `tickets.reserve`, `tickets.release` (refund) |
| **Tiers** (`Modules/Tiers`) | `Tiers.TicketsLeft` (sole writer after the tier opens) and `TicketHolds`. | `tickets.reserve`, `tickets.release` (prefetch 1) | `tickets.held`, `tickets.short`, `tickets.released` |
| **ETickets** (`Modules/ETickets`) | `ETickets`. | `tickets.held` | `eticket.issued`, `eticket.rejected`, `tickets.release` (poison) |
| **Messaging** (`Messaging`) | `OutboxMessages`, `InboxMessages`, the relay, the consumer loop with retry and dead-letter, the Kafka log. | | |

RabbitMQ topology is declared in code (`Messaging/Topology.cs`): exchange `tickets.work`, one queue per module (`tiers.commands`, `etickets.work`, `orders.events`), a `.retry` queue per module (2 s TTL, then back to the main queue, 3 attempts), and a `.dlq` per module behind `tickets.dlx`. A transient failure retries; a poison message goes straight to the dead-letter queue after its rejection is committed. Kafka has one topic, `tickets.events`, one partition, replication factor 1, keyed by order id.

## Run it

Needs Node 22 (npm 10), .NET SDK 10.0.401 (`global.json`), Docker (arm64 is fine) and python3 for the smoke script.

```bash
npm ci
docker compose up -d --wait db        # dotnet test uses Postgres on 5477 (database tickets_test)
dotnet test tests/api/Tickets.Api.Tests.csproj
docker compose up --build --wait      # db, kafka, rabbitmq, api, web
./scripts/smoke.sh                    # last line: tickets smoke passed
npx playwright install chromium       # once
npx playwright test                   # two specs against http://127.0.0.1:8097
docker compose down                   # keeps the Postgres volume; `down -v` drops it
```

| Service | Host port |
| --- | --- |
| Web (nginx, proxies `/api/`) | 8097 |
| API (`/scalar/v1`, `/api/health`) | 5217 |
| Postgres 17 | 5477 |
| Kafka 3.8.1 (KRaft, one broker) | 9127 |
| RabbitMQ 3.13 / management UI (`platform` / `platform`) | 5707 / 15707 |

Inside the Compose network the API and nginx listen on 8080 and Kafka's internal listener is 9092.

## Try it with curl

```bash
curl -s -X POST http://127.0.0.1:5217/api/tiers \
  -H 'content-type: application/json' \
  -d '{"code":"NIGHT1-FLOOR","name":"Night One","section":"Floor","venue":"Arena Norte","ticketsTotal":8,"priceCents":8000}'

curl -s -X POST http://127.0.0.1:5217/api/orders \
  -H 'content-type: application/json' \
  -d '{"tierCode":"NIGHT1-FLOOR","reference":"BUYER-1001","tickets":4}'
# 202, status Requested, totalCents 35840. Use the returned id:

curl -s http://127.0.0.1:5217/api/orders/<id>               # Sold after a few seconds, with an e-ticket number
curl -s http://127.0.0.1:5217/api/e-tickets/<id>
curl -s http://127.0.0.1:5217/api/tiers/NIGHT1-FLOOR        # ticketsLeft 4
curl -s http://127.0.0.1:5217/api/orders/<id>/events        # the Kafka log for this order

curl -s -X POST http://127.0.0.1:5217/api/orders/<id>/refund \
  -H 'content-type: application/json'                       # tickets go back on sale; the e-ticket stays
```

An order is validated at the door: buyer reference required (at most 40 characters), 1 to 6 tickets. The poison match is a case-sensitive substring: `BUYER-POISON-7` and any longer token containing `POISON` match.

## Develop with Nx

The repository is an Nx workspace (`nx.json`, plugin `@nx/dotnet`). `npx nx show projects` lists `api` (the ASP.NET Core monolith), `web` (the Angular app) and the inferred xUnit project.

```bash
docker compose up -d --wait db kafka rabbitmq   # the brokers and Postgres, not the app
npx nx serve api                                # http://localhost:5217
npx nx serve web                                # http://localhost:4297, proxies /api to the API
WEB_URL=http://localhost:4297 npx playwright test   # the same browser specs, against the dev servers
npx nx run-many -t typecheck lint -p web        # tsc --noEmit and ESLint (angular-eslint)
npx nx build web && npx nx build api            # what CI builds
```

## What is tested, and where it runs

- **xUnit against real Postgres** (`tests/api`, 26 tests): two tasks with two `DbContext`s fight for one remaining ticket (one `Held`, one `Short`, tickets left 0, no hold for the loser); empty input, over-limit input, integer overflow (4294967297 must not wrap to 1), the total with its service fee and its checked arithmetic, the poison substring; the same message delivered twice is claimed once; a release returns the tickets once; the poison rejection is committed before the dead-letter; a refund followed by the late final mark leaves the order `Refunded` and the e-ticket `Issued`. The race test was run against a read-then-write version of the hold, where it failed, and against the conditional update, where it passes.
- **`scripts/smoke.sh`** drives the composed stack with a run id on every code, so a kept volume does not collide: validation, the happy path (total with fee, e-ticket, Kafka log), three concurrent orders for one ticket (one sold, two short, no hold for the losers), the poison reference (rejected, `etickets.work.dlq` gains one message, tickets restored), and a refund that lands between the e-ticket and the final mark (order `Refunded`, e-ticket `Issued`, tickets back, second refund `409`). To make that last window wide enough to hit on purpose, Compose sets `Orders__FinalMarkDelayMs=1500`; it is a latency knob and defaults to 0.
- **Playwright** (`e2e/specs/buy-tickets.spec.ts`): adds a tier in the UI, buys tickets, sees `Sold` and the e-ticket number, reloads to prove the page reads the API and keeps nothing in `localStorage`, then refunds and sees the tickets come back. A second spec, `e2e/specs/ui-flows.spec.ts`, drives the page through the refusal messages (over-limit, empty reference, duplicate), the poison rejection, the loser of a race for the last ticket, and the Kafka event log panel. Both specs pass against the Compose stack and against the Nx dev servers.
- **CI** (`.github/workflows/ci.yml`, on push) has two jobs. `verify`: `npm ci`, Prettier check, `dotnet format whitespace --verify-no-changes`, `dotnet test` with a Postgres 17 service, `nx typecheck` and `nx lint` for web (ESLint with angular-eslint, template accessibility rules included, zero warnings allowed), and `nx build` for web and api. `e2e`: builds the Compose stack on the runner, runs `scripts/smoke.sh` and both Playwright specs against it, uploads the Playwright traces and prints the Compose logs on failure.

## API

All routes are unauthenticated JSON. Interactive docs are at `http://127.0.0.1:5217/scalar/v1` (OpenAPI at `/openapi/v1.json`); the web port proxies `/api/`, `/scalar/` and `/openapi/` too.

| Method and path | What it does | Responses |
| --- | --- | --- |
| `GET /api/health` | Liveness. | `200 Healthy` |
| `POST /api/tiers` | Opens a tier with its tickets on sale and a price in cents. | `201`, `400` invalid, `409` code exists |
| `GET /api/tiers` | Lists tiers. | `200` |
| `GET /api/tiers/{code}` | One tier with the live tickets left. | `200`, `404` |
| `POST /api/orders` | Accepts an order (1 to 6 tickets) and starts the saga. | `202` Requested, `400` invalid, `404` unknown tier, `409` duplicate reference |
| `GET /api/orders?tierCode=` | Lists the latest 100 orders, optionally for one tier. | `200` |
| `GET /api/orders/{id}` | One order and its status. | `200`, `404` |
| `POST /api/orders/{id}/refund` | Moves a held or sold order to `Refunded` and puts the tickets back. The e-ticket is not touched. | `202`, `404`, `409` wrong status |
| `GET /api/orders/{id}/events` | The Kafka event log for one order, read from the start of the topic. | `200` |
| `GET /api/e-tickets/{orderId}` | The e-ticket row. | `200`, `404` until the step has run |

Errors come back as `application/problem+json` with a `title`.

## Configuration

Compose sets these on the API container. Defaults in `apps/api/appsettings.json` point at the published host ports, so `nx serve api` works against `docker compose up -d --wait db kafka rabbitmq`.

| Setting | Compose value | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Tickets` | `Host=db;Port=5432;Database=tickets;Username=tickets;Password=tickets` | The one Postgres database. Migrations run on startup. |
| `RabbitMq__Host` / `__Port` / `__User` / `__Password` | `rabbitmq` / `5672` / `platform` / `platform` | Work queue. Virtual host `/`. |
| `Kafka__BootstrapServers` | `kafka:9092` | Event log. |
| `Orders__FinalMarkDelayMs` | `1500` | Milliseconds the final mark waits. A latency knob so the smoke can land a refund inside the window. `0` outside Compose. |
| `TICKETS_TEST_DB` (tests only) | | Postgres connection for `dotnet test`. Defaults to `localhost:5477`, database `tickets_test`. CI points it at its service container. |

## Look inside and fix things

```bash
docker compose ps                      # five services, all healthy
docker compose logs -f api             # relay, consumers, poison and retry lines
docker compose exec db psql -U tickets -d tickets -c 'select "RoutingKey","SentAt" from "OutboxMessages" order by "Id" desc limit 10'
open http://127.0.0.1:15707            # RabbitMQ management, platform / platform
```

- **Dead-lettered messages** sit in `etickets.work.dlq`. Nothing replays them; inspect them in the management UI. Retries wait in `<queue>.retry` for 2 seconds.
- **A port is already in use**: `lsof -nP -i :5217` shows who holds it. Compose publishes only on `127.0.0.1`.
- **`dotnet test` cannot connect**: start Postgres first with `docker compose up -d --wait db`.
- **The smoke cannot reach the API**: run `docker compose up --build --wait` first; override the target with `API=... RABBIT=... ./scripts/smoke.sh`.
- **Start clean**: `docker compose down -v` drops Postgres, Kafka and RabbitMQ data. Plain `down` keeps them, and the smoke is safe on kept data because every code carries a run id.
- **The Kafka log looks empty right after an order**: the relay publishes within a second or so; reload the event log.

## Known limits

- It is unauthenticated on purpose. Anything that can reach the API can read and write everything.
- It sells general-admission counts per tier. There are no numbered seats, no hold that expires while a buyer is paying, no virtual waiting room and no payment: the scarce-number mechanics are the point, not a ticketing platform.
- A refund keeps the e-ticket row, as the saga rules require. A real system would also void the ticket at the door; this one records the history and does not model entry.
- Delivery is at-least-once, not exactly-once. The inbox makes repeats harmless for the saga; the Kafka log can hold a duplicate event after a relay crash.
- One broker, one partition, replication factor 1, one API instance. The relay does not coordinate across instances (the inbox would absorb the duplicates, but the outbox is never trimmed).
- `GET .../events` scans the whole topic for each call. Fine here, not something to expose at scale.
- Lists are capped at 100 rows and not paginated.
- Dead-lettered messages are not replayed automatically.
- The browser specs drive Chromium at a desktop size only, and the `e2e` CI job needs Docker and takes a few minutes.

## Cloud mapping

This repository runs on Compose. Nothing in it calls a cloud account.

| Here | On Azure |
| --- | --- |
| Postgres 17 | Azure Database for PostgreSQL |
| Kafka 3.8 | Event Hubs |
| RabbitMQ 3.13 | Service Bus queue with its dead-letter sub-queue |
| The API and the web app | Container Apps |

## Layout

```
apps/api      ASP.NET Core monolith: Modules/{Orders,Tiers,ETickets}, Messaging, Data, Migrations
apps/web      Angular 21, styled with Tailwind 4 utilities only
tests/api     xUnit against Postgres
e2e/          Playwright
scripts/      smoke.sh
compose.yaml  Postgres, Kafka, RabbitMQ, API, web
```

Nx 23.2.1 orchestrates `nx build web`, `nx lint web`, `nx typecheck web` and `nx build api`; EF Core migrations are committed and the API applies them on startup.

## Other systems in this .NET track

- [dotnet-freight](https://github.com/joshbarros/dotnet-freight): container space on ocean sailings.
- [dotnet-clinic](https://github.com/joshbarros/dotnet-clinic): appointment slots in a provider session.
- [dotnet-ledger](https://github.com/joshbarros/dotnet-ledger): outgoing payments from an account balance.
- [dotnet-warehouse](https://github.com/joshbarros/dotnet-warehouse): warehouse stock and outbound orders.
- [dotnet-academy](https://github.com/joshbarros/dotnet-academy): course cohort seats and enrollments.
- [dotnet-compute](https://github.com/joshbarros/dotnet-compute): GPU-hour reservations in a pool.
- [dotnet-hotel](https://github.com/joshbarros/dotnet-hotel): hotel room-nights.
