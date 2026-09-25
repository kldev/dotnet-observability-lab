#!/bin/bash

# Runs every k6 traffic script in the background at once.
# ==============================
#
#   ./traffic.sh [start]          # orders + problems + messages, 5 min each (k6 defaults)
#   DURATION=30m VUS=20 ./traffic.sh
#   ./traffic.sh start orders messages
#   ./traffic.sh status | logs [name] | stop
#
# Environment (BASE_URL, DURATION, VUS, ERROR_RATE, ...) is passed to k6 as -e.
# Logs and PIDs go to k6/results/ (gitignored).

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
K6_DIR="$SCRIPT_DIR/k6"
RUN_DIR="$K6_DIR/results"
ALL_SCRIPTS=(orders problems messages)
K6_VARS=(BASE_URL DURATION VUS EMAIL_VUS ERROR_RATE EXCEPTION_RATE SLOW_RATE DB_ERROR_RATE SLOW_DB_RATE VERBOSE_LOG_RATE)

usage() {
    echo "Usage: ./traffic.sh [start [script...]] | status | logs [script] | stop"
    echo ""
    echo "  start     Run k6 scripts in the background (default: ${ALL_SCRIPTS[*]})"
    echo "  status    Show which scripts are still running"
    echo "  logs      Follow the log of one script (default: all)"
    echo "  stop      Stop them gracefully - k6 runs teardown (problems.js switches random problems OFF)"
}

pid_of() {
    local file="$RUN_DIR/$1.pid"
    [ -f "$file" ] && kill -0 "$(cat "$file")" 2>/dev/null && cat "$file"
}

start() {
    command -v k6 >/dev/null || { echo "k6 not installed"; exit 1; }
    mkdir -p "$RUN_DIR"

    local scripts=("$@")
    [ ${#scripts[@]} -eq 0 ] && scripts=("${ALL_SCRIPTS[@]}")

    local args=()
    for name in "${K6_VARS[@]}"; do
        if [ -n "${!name}" ]; then
            args+=(-e "$name=${!name}")
        fi
    done

    for script in "${scripts[@]}"; do
        [ -f "$K6_DIR/$script.js" ] || { echo "No such script: k6/$script.js"; exit 1; }
        if pid=$(pid_of "$script"); then
            echo "  $script already running (pid $pid)"
            continue
        fi
        # nohup: survives closing the terminal. k6 still reacts to SIGINT from `stop`.
        nohup k6 run --no-color "${args[@]}" "$K6_DIR/$script.js" > "$RUN_DIR/$script.log" 2>&1 &
        echo $! > "$RUN_DIR/$script.pid"
        echo "  $script started (pid $!, log k6/results/$script.log)"
    done
}

status() {
    for script in "${ALL_SCRIPTS[@]}"; do
        if pid=$(pid_of "$script"); then
            echo "  $script: running (pid $pid) - $(grep '^running (' "$RUN_DIR/$script.log" | tail -n 1)"
        elif [ -f "$RUN_DIR/$script.log" ]; then
            echo "  $script: finished - see k6/results/$script.log"
        else
            echo "  $script: not started"
        fi
    done
}

stop() {
    local stopped=()
    for script in "${ALL_SCRIPTS[@]}"; do
        if pid=$(pid_of "$script"); then
            # SIGINT, not SIGTERM/KILL: k6 stops the scenarios and still runs teardown().
            kill -INT "$pid"
            stopped+=("$script:$pid")
        fi
    done
    [ ${#stopped[@]} -eq 0 ] && { echo "Nothing running."; return; }

    for entry in "${stopped[@]}"; do
        local script=${entry%%:*} pid=${entry#*:}
        for _ in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
        if kill -0 "$pid" 2>/dev/null; then
            kill -KILL "$pid"
            echo "  $script killed (teardown did not finish in 30 s)"
        else
            echo "  $script stopped"
        fi
        rm -f "$RUN_DIR/$script.pid"
    done
}

logs() {
    if [ -n "$1" ]; then
        tail -f "$RUN_DIR/$1.log"
    else
        tail -f "$RUN_DIR"/*.log
    fi
}

command=${1:-start}
[ $# -gt 0 ] && shift

case $command in
    start) start "$@"; echo ""; echo "Status: ./traffic.sh status | Logs: ./traffic.sh logs [name] | Stop: ./traffic.sh stop" ;;
    status) status ;;
    logs) logs "$1" ;;
    stop) stop ;;
    -h|--help|help) usage ;;
    *) usage; exit 1 ;;
esac
