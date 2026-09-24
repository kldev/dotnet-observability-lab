# .NET 10 Observability Lab

Mały, lokalny playground do nauki **observability i DevOps** na .NET 10:
logi, metryki, trace'y, Prometheus, Grafana, OpenTelemetry, PostgreSQL, Docker Compose
oraz przechowywanie logów i trace'ów w S3 (RustFS) przez Rootprint.

Domena (`Orders`) i API są celowo banalne — służą tylko do generowania ruchu.

```text
                    ┌──────────── /metrics (scrape co 5 s) ───────────┐
                    │                                                 ▼
 k6 / curl ──▶  app (.NET 10) ──SQL──▶ PostgreSQL              Prometheus ──▶ Grafana
                    │                                                 ▲ exemplars (trace_id)
                    └── OTLP (logi + trace'y) ──▶ otel-collector       │
                                                     │                 │ "Open trace in Rootprint"
                                                     ▼                 │
                                                 Rootprint  ◀──────────┘
                                                     │
                                                 Quickwit ── S3 API ──▶ RustFS (bucket observability-logs)
```

Trace w aplikacji: **HTTP request → `Mediator <Message>` → `<Handler>` → PostgreSQL (span z treścią SQL)**.

---

## 1. Wymagania

| Narzędzie | Wersja | Do czego |
| --- | --- | --- |
| Docker + Docker Compose v2 | 24+ | całe środowisko |
| .NET SDK | 10.0 | uruchomienie lokalne i testy |
| k6 | opcjonalnie | generowanie ruchu (`k6/`) |

Testy integracyjne uruchamiają PostgreSQL przez Testcontainers — potrzebny działający Docker.

## 2. Uruchomienie Docker Compose (zalecane)

```bash
cp .env.example .env        # lokalne hasła/klucze; .env jest w .gitignore
docker compose up -d        # pierwsze uruchomienie buduje obraz aplikacji
docker compose ps           # wszystko "Up"/"healthy", rustfs-init i rootprint-bootstrap "Exited (0)"
```

Co się dzieje przy starcie:

1. `rustfs` startuje, `rustfs-init` zakłada bucket `S3_BUCKET` (CreateBucket przez S3 API, curl `--aws-sigv4`).
2. `quickwit` trzyma metastore i indeksy w `s3://$S3_BUCKET/quickwit/indexes` (RustFS).
3. `rootprint` startuje na Postgresie (osobna baza `rootprint` w tym samym serwerze) i Quickwicie.
4. `rootprint-bootstrap` tworzy konto admina i klucz ingest, zapisuje klucz do wolumenu.
5. `otel-collector` czyta klucz z wolumenu i wysyła logi + trace'y do Rootprint.
6. `app` tworzy tabelę `orders` i zaczyna działać.

Limity kontenera aplikacji: `cpus: 2`, `mem_limit: 512m` — względem nich liczone są **CPU %** i **Memory %** na dashboardzie.

## 3. Uruchomienie lokalne (`dotnet run`)

Infrastruktura w Dockerze (z portami PostgreSQL i OTLP wystawionymi na host), aplikacja z IDE / terminala:

```bash
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d
docker compose stop app                     # opcjonalnie - żeby nie było dwóch instancji

set -a; source .env; set +a
dotnet user-secrets --project src/ObservabilityLab set "ConnectionStrings:Orders" \
  "Host=localhost;Port=5432;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD"

dotnet run --project src/ObservabilityLab   # http://localhost:5253, środowisko Development
```

Lokalna instancja wysyła OTLP na `localhost:4317`, a Prometheus scrapuje ją jako job
`observability-lab-local` (`host.docker.internal:5253`) — na dashboardzie wybierz go w zmiennej **App**.
Profil nasłuchuje na `0.0.0.0:5253`, żeby Prometheus w kontenerze mógł się do niej dostać
(jeśli firewall blokuje ruch z sieci Dockera do hosta, target będzie `down`).

## 4-7. Adresy

| Usługa | Adres | Login |
| --- | --- | --- |
| Aplikacja – API docs (Scalar) | http://localhost:8080/docs | – |
| Aplikacja – OpenAPI | http://localhost:8080/openapi/v1.json | – |
| Aplikacja – metryki | http://localhost:8080/metrics | – |
| **Grafana** | http://localhost:3000 | `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` |
| **Prometheus** | http://localhost:9090 (targets: `/targets`) | – |
| **Rootprint** (logi + trace'y) | http://localhost:8282 | `ROOTPRINT_ADMIN_EMAIL` / `ROOTPRINT_ADMIN_PASSWORD` |
| **RustFS** – konsola | http://localhost:9001 | `S3_ACCESS_KEY` / `S3_SECRET_KEY` |
| **RustFS** – S3 API | http://localhost:9000 | jw. |
| Quickwit UI (tylko dev override) | http://localhost:7280/ui | – |
| PostgreSQL (tylko dev override) | localhost:5432 | `POSTGRES_USER` / `POSTGRES_PASSWORD` |

### Endpointy aplikacji

| Metoda | Ścieżka | Opis |
| --- | --- | --- |
| POST | `/api/orders` | utworzenie orderu (`{"customerName":"Jan","totalAmount":12.5}`) → 201 |
| GET | `/api/orders/{orderId}` | pobranie orderu → 200 / 404 |
| GET | `/api/orders?status=Paid&page=1&pageSize=20` | najnowsze ordery, stronicowane (`items`, `page`, `pageSize`, `hasMore`) → 200 |
| PUT | `/api/orders/{orderId}/status` | zmiana statusu (`{"status":"Completed"}`) → 200 / 404 |
| GET | `/health` | liveness (tylko proces) |
| GET | `/health/ready` | readiness: aplikacja + PostgreSQL (używany przez Docker healthcheck) |
| GET | `/metrics` | Prometheus / OpenMetrics (z exemplarami) |
| GET | `/diagnostics/problem/*` | celowe problemy (sekcja 9) |
| GET/PUT | `/diagnostics/random-problems` | tryb losowych problemów (sekcja 9) |

Statusy: `Created`, `Paid`, `Cancelled`, `Completed`. Błędy zwracane są jako ProblemDetails z polem `traceId`.

## 8. Jak zobaczyć logi

- **Rootprint** → http://localhost:8282 → eksplorator logów (indeks `otel-logs-v0_9`). Przykładowe zapytania:
  - `severity_text:Error`
  - `trace_id:<trace id>` — wszystkie logi jednego requestu
  - `attributes.OrderId:<order id>`
  - `attributes.RequestId:"0HNO...:00000001"`
- **stdout kontenera** (JSON, z `TraceId`, `SpanId`, `RequestId`, `OrderId` w `State`/`Scopes`):
  ```bash
  docker compose logs -f app
  docker compose logs app | grep '"LogLevel":"Error"'
  ```
- **S3 / RustFS** – logi i trace'y fizycznie leżą w buckecie jako splity Quickwita:
  konsola RustFS → bucket `observability-logs` → `quickwit/indexes/otel-logs-v0_9/*.split`.

Każdy wpis ma: timestamp, poziom, treść, `trace_id`/`span_id`, `RequestId`, `RequestPath`
oraz pola z szablonu wiadomości (np. `OrderId`, `CustomerName`, `OrderStatus`).

## 9. Jak wygenerować problemy

### Pojedyncze problemy (Development lub `DIAGNOSTICS_ENABLED=true` — domyślnie włączone w `.env.example`)

```bash
B=http://localhost:8080/diagnostics/problem
curl "$B/slow?ms=3000"            # wolny request
curl "$B/bad-request"             # HTTP 400
curl "$B/not-found"               # HTTP 404
curl "$B/error"                   # HTTP 500
curl "$B/exception"               # nieobsłużony wyjątek -> 500 + log z exception
curl "$B/slow-db?seconds=3"       # wolne zapytanie: SELECT pg_sleep(3)
curl "$B/db-error"                # błąd PostgreSQL (nieistniejąca tabela)
curl "$B/cpu?seconds=30"          # obciążenie CPU (domyślnie wszystkie rdzenie kontenera)
curl "$B/memory?mb=150"           # alokacja i trzymanie pamięci (kumuluje się!)
curl "$B/memory/release"          # zwolnienie pamięci
```

Endpointy diagnostyczne są celowo poza dokumentem OpenAPI/Scalar (to narzędzia labu, nie API) — wywołuj je curl/k6/przeglądarką.

### Random problems

Konfiguracja startowa w `.env` (prawdopodobieństwa 0..1, dotyczą requestów `/api/orders`, domyślnie wyłączone):

```text
RANDOM_PROBLEMS_ENABLED=true
RANDOM_ERROR_RATE=0.05          # HTTP 500
RANDOM_EXCEPTION_RATE=0.02      # wyjątek
RANDOM_SLOW_REQUEST_RATE=0.05   # opóźnienie 0.5-3 s
RANDOM_DB_ERROR_RATE=0.03       # błędne zapytanie SQL
RANDOM_SLOW_DB_RATE=0.05        # pg_sleep 0.5-2.5 s przed właściwym zapytaniem
RANDOM_VERBOSE_LOG_RATE=0.02    # seria 20 dodatkowych logów
```

(`docker compose up -d` po zmianie `.env`.) Albo w locie, bez restartu:

```bash
curl -X PUT localhost:8080/diagnostics/random-problems -H 'content-type: application/json' \
  -d '{"enabled":true,"errorRate":0.05,"slowRequestRate":0.1,"dbErrorRate":0.03,"slowDbRate":0.05}'
curl -X PUT localhost:8080/diagnostics/random-problems -H 'content-type: application/json' -d '{"enabled":false}'
```

### Ruch z k6

```bash
k6 run k6/orders.js                          # zwykły ruch (create/get/list/status + trochę 400/404), 5 min
k6 run -e VUS=30 -e DURATION=10m k6/orders.js
k6 run k6/problems.js                        # włącza random problems + co 10 s losowy /diagnostics/problem/*,
                                             # na koniec wyłącza random problems i zwalnia pamięć
```

`BASE_URL` domyślnie `http://localhost:8080` (dla `dotnet run`: `-e BASE_URL=http://localhost:5253`).

## 10. Jak zobaczyć trace (Grafana → trace → log)

1. Grafana → dashboard **Observability Lab (.NET 10)** (strona domowa Grafany).
2. Zauważ anomalię (skok P95, 5xx, *DB failures*, CPU).
3. Na wykresie **Latency P50/P95/P99** lub **DB latency** kropki to **exemplary** – najedź na kropkę
   → **Open trace in Rootprint** (trzeba być zalogowanym w Rootprint).
4. Rootprint pokazuje waterfall: `PUT /api/orders/{orderId}/status` → `Mediator ChangeOrderStatus`
   → `ChangeOrderStatusHandler` → `SELECT pg_sleep(@seconds)` / `UPDATE orders ...` z czasem każdego spanu.
5. Logi tego requestu: w Rootprint → logi → `trace_id:<id>`.

Alternatywnie: Rootprint → **Traces** (filtr po serwisie `observability-lab`, sortowanie po czasie / błędach)
albo `traceId` z odpowiedzi ProblemDetails (`00-<traceId>-<spanId>-01`).

## 11. Jak zobaczyć metryki

- **Grafana** – dashboard w sekcjach: *System / Runtime*, *HTTP*, *PostgreSQL*, *Application (Orders)*,
  *Where to look next*. Dashboard i datasource są provisionowane z `deploy/grafana/` – nic nie trzeba klikać.
- **Prometheus** – http://localhost:9090, np.:

```promql
# CPU % (względem rdzeni dostępnych dla kontenera)
100 * sum(rate(dotnet_process_cpu_time_seconds_total[1m])) / max(dotnet_process_cpu_count)

# Memory % (working set / limit pamięci kontenera z cgroup)
100 * max(dotnet_process_memory_working_set_bytes) / max(lab_process_memory_limit_bytes)

# Requests/sec bez scrape'ów i health checków
sum(rate(http_server_request_duration_seconds_count{http_route!~"/health.*|/metrics"}[1m]))

# P95
histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{http_route!~"/health.*|/metrics"}[1m])))

# 5xx per route
sum by (http_route) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))
```

Źródła metryk: ASP.NET Core (`http_server_*`, `kestrel_*`, `aspnetcore_diagnostics_exceptions_total`),
runtime .NET (`dotnet_*`), Npgsql (`db_client_*`), aplikacja (`lab_orders_*`, `lab_problems_injected_total`,
`lab_health_status`, `lab_process_memory_limit_bytes`).

## 12. Zatrzymanie środowiska

```bash
docker compose down          # kontenery stop + usunięcie, dane (wolumeny) zostają
```

## 13. Czyszczenie danych

```bash
docker compose down -v       # usuwa też wolumeny: PostgreSQL, Prometheus, Grafana, RustFS (logi/trace'y), klucz Rootprint
docker image rm observability-lab:local
```

---

## Testy i jakość kodu

```bash
dotnet test                      # testy jednostkowe + integracyjne (Testcontainers PostgreSQL)
dotnet tool restore
dotnet csharpier check .         # formatowanie (CSharpier)
dotnet csharpier format .        # automatyczne formatowanie
```

**Git hooks (Husky.Net):** pierwszy `dotnet restore`/`build` instaluje hooki automatycznie
(`core.hooksPath=.husky`). Hook `pre-commit` uruchamia `dotnet csharpier check` na stage'owanych plikach `*.cs`
(zadania w `.husky/task-runner.json`) i blokuje commit, jeśli formatowanie się nie zgadza.
Ręcznie: `dotnet husky install`, `dotnet husky run --group pre-commit`. Wyłączenie instalacji hooków: `HUSKY=0`
(ustawione w Dockerfile).

## Struktura

```text
src/ObservabilityLab/
  Program.cs                   konfiguracja: Npgsql, Mediator, OpenTelemetry, logowanie, health, OpenAPI
  Api/                         stałe tras, tagi OpenAPI, wspólne kody błędów, SliceResponse
  Endpoints/MapEndpoints.cs    jedno miejsce rejestracji wszystkich endpointów
  Endpoints/Orders/            Endpoint.cs (grupa + tag) + Maps/Map<Verb>.cs (jedna operacja = jeden plik), OrderResponse
  Orders/                      model, komendy/zapytania + handlery Mediatora (Dapper)
  Diagnostics/                 celowe problemy + random problems
  Telemetry/                   ActivitySource/Meter, Mediator tracing behavior, health -> metryka
tests/ObservabilityLab.Tests/  testy HTTP (WebApplicationFactory + Testcontainers) i random problems
deploy/                        konfiguracje: grafana, prometheus, otel-collector, rootprint (+quickwit), rustfs, postgres
k6/                            skrypty ruchu
```

## Decyzje i uproszczenia

- **Jeden projekt aplikacji**, bez warstw, repozytoriów i DDD. Handlery Mediatora używają `NpgsqlDataSource` + Dapper bezpośrednio.
- **Mediator** (source generator, `martinothamar/Mediator`) + jeden pipeline behavior dodający span `Mediator <Message>`.
- **Rootprint** przechowuje logi *i* trace'y (OTLP), więc nie ma osobnego Jaeger/Tempo.
  Rootprint to UI + API nad **Quickwit**, a Quickwit trzyma dane w S3 (RustFS).
- **OpenTelemetry Collector** jest między aplikacją a Rootprint, bo Rootprint wymaga klucza ingest,
  który powstaje dopiero przy pierwszym starcie (`rootprint-bootstrap`). Collector dodatkowo batchuje i ponawia wysyłkę.
- **Jeden PostgreSQL** dla aplikacji (`orders`) i dla metadanych Rootprint (`rootprint`).
- `curlimages/curl` wystarcza do utworzenia bucketu (S3 SigV4) i bootstrapu Rootprint — bez dodatkowych CLI.
- Endpointy diagnostyczne to celowo `GET` (łatwe do wywołania z przeglądarki/k6), mapowane tylko gdy są włączone i wyłączone z dokumentu OpenAPI.
- Kontrakt HTTP wg standardu .NET 10: trasy jako stałe (`ApiRoutes`), `/api/<zasoby>` z `{orderId:guid}`, `TypedResults`,
  `WithName`/`WithSummary`/`Produces`, tagi z opisami, OpenAPI (`/openapi/v1.json`) + Scalar, walidacja wbudowana w .NET 10 (`AddValidation`).
  Bez wersjonowania — jedyny konsument jest wdrażany razem z API (dokument i tak nazywa się `v1`).
- Health checki z Dockera i `/metrics` są wyłączone z tracingu (szum); stan health jest metryką `lab_health_status`.
- Brak auth, Kubernetes, kolejek itp. — to lokalny playground.
