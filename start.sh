#!/bin/bash

# Start Script - .NET 10 Observability Lab
# ==============================

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Prefer the Compose v2 plugin, fall back to the standalone binary.
if docker compose version >/dev/null 2>&1; then
    COMPOSE=(docker compose)
else
    COMPOSE=(docker-compose)
fi

COMPOSE_FILES=(-f docker-compose.yml)

usage() {
    echo "Usage: ./start.sh [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --build, --b        Force rebuild of the app image"
    echo "  --dev, --expose     Also expose PostgreSQL (5432), OTLP (4317/4318) and Quickwit (7280) on the host"
    echo "                      (docker-compose.dev.yml, app in Development; needed for 'dotnet run')"
    echo "  --foreground, -f    Run in foreground (see logs)"
    echo "  --logs, --l [svc]   Follow logs (default: app)"
    echo "  --status, --ps      Show containers"
    echo "  --traffic           Generate traffic with k6 (k6/orders.js)"
    echo "  --problems          Generate traffic with random problems ON (k6/problems.js)"
    echo "  --sa                Stop only the app (e.g. before 'dotnet run')"
    echo "  --stop              Stop and remove containers (data is kept)"
    echo "  --clean, --c        Remove containers AND volumes (all data)"
    echo "  --help, -h          Show this help"
    echo ""
    echo "Examples:"
    echo "  ./start.sh                  # start everything"
    echo "  ./start.sh --b --expose     # rebuild app, publish dev ports"
    echo "  ./start.sh --logs rootprint"
}

# Values used in the summary below (fall back to the compose defaults).
load_env() {
    if [ ! -f .env ]; then
        echo "No .env found - creating it from .env.example (local lab values)."
        cp .env.example .env
        echo ""
    fi
    set -a
    # shellcheck disable=SC1091
    source .env
    set +a
}

# Parse arguments
BUILD_ARG=""
DETACH_ARG="-d"
EXPOSE=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --b|--build)
            BUILD_ARG="--build"
            shift
            ;;
        --dev|--expose)
            EXPOSE=true
            COMPOSE_FILES+=(-f docker-compose.dev.yml)
            shift
            ;;
        --foreground|-f)
            DETACH_ARG=""
            shift
            ;;
        --l|--logs)
            SERVICE="${2:-app}"
            "${COMPOSE[@]}" logs -f "$SERVICE"
            exit 0
            ;;
        --ps|--status)
            "${COMPOSE[@]}" ps -a
            exit 0
            ;;
        --traffic)
            k6 run "$SCRIPT_DIR/k6/orders.js"
            exit 0
            ;;
        --problems)
            k6 run "$SCRIPT_DIR/k6/problems.js"
            exit 0
            ;;
        --sa)
            "${COMPOSE[@]}" stop app
            exit 0
            ;;
        --stop)
            "${COMPOSE[@]}" -f docker-compose.yml -f docker-compose.dev.yml down
            exit 0
            ;;
        --c|--clean)
            echo "Cleaning up volumes and containers..."
            "${COMPOSE[@]}" -f docker-compose.yml -f docker-compose.dev.yml down -v --remove-orphans
            echo "Clean complete."
            exit 0
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            echo ""
            usage
            exit 1
            ;;
    esac
done

echo "=========================================="
echo "  .NET 10 Observability Lab"
echo "=========================================="
echo ""

load_env

echo "Starting services..."
[ "$EXPOSE" = true ] && echo "  (dev mode: PostgreSQL, OTLP and Quickwit exposed on the host)"
echo ""

"${COMPOSE[@]}" "${COMPOSE_FILES[@]}" up $BUILD_ARG $DETACH_ARG

if [ -n "$DETACH_ARG" ]; then
    APP="http://localhost:${APP_PORT:-8080}"
    echo ""
    echo "=========================================="
    echo "  Services started successfully!"
    echo "=========================================="
    echo ""
    echo "Endpoints:"
    echo "  App:          $APP"
    echo "     Docs:      $APP/docs"
    echo "     OpenAPI:   $APP/openapi/v1.json"
    echo "     Metrics:   $APP/metrics"
    echo "     Orders:    $APP/api/orders"
    echo ""
    echo "  Grafana:      http://localhost:${GRAFANA_PORT:-3000}  (user: ${GRAFANA_ADMIN_USER:-admin})"
    echo "  Prometheus:   http://localhost:${PROMETHEUS_PORT:-9090}"
    echo "  Rootprint:    http://localhost:${ROOTPRINT_PORT:-8282}  (user: ${ROOTPRINT_ADMIN_EMAIL:-admin@lab.local})"
    echo "  RustFS:       http://localhost:${RUSTFS_CONSOLE_PORT:-9001}  (console, user: $S3_ACCESS_KEY)"
    echo "                http://localhost:${RUSTFS_S3_PORT:-9000}  (S3 API, bucket: ${S3_BUCKET:-observability-logs})"
    if [ "$EXPOSE" = true ]; then
        echo ""
        echo "  Exposed (dev):"
        echo "    PostgreSQL: localhost:5432  (db: ${POSTGRES_DB:-orders}, user: ${POSTGRES_USER:-lab})"
        echo "    OTLP:       localhost:4317 (gRPC), localhost:4318 (HTTP)"
        echo "    Quickwit:   http://localhost:7280/ui"
    fi
    echo ""
    echo "  Problems:     $APP/diagnostics/problem/{slow,bad-request,not-found,error,exception,slow-db,db-error,cpu,memory}"
    echo "  Random:       $APP/diagnostics/random-problems"
    echo ""
    echo "  Health checks:"
    echo "    - Live:     $APP/health"
    echo "    - Ready:    $APP/health/ready"
    echo ""
    echo "Commands:"
    echo "  View logs:      ./start.sh --logs [service]"
    echo "  Traffic (k6):   ./start.sh --traffic | --problems"
    echo "  Stop services:  ./start.sh --stop"
    echo "  Clean all:      ./start.sh --clean"
    echo ""
fi
