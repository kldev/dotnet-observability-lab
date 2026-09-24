#!/bin/sh
# Runs once, on the first start of an empty data volume.
# The lab uses one PostgreSQL instance: "orders" for the app, "rootprint" for Rootprint metadata.
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
CREATE ROLE rootprint LOGIN PASSWORD '${ROOTPRINT_DB_PASSWORD}';
CREATE DATABASE rootprint OWNER rootprint;
SQL
