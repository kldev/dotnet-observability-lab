#!/bin/sh
# Creates the log bucket in RustFS via the plain S3 API (CreateBucket, SigV4-signed by curl).
# Retries while RustFS is still starting (connection refused / 503).
set -eu
for attempt in $(seq 1 60); do
  code=$(curl -s -o /tmp/out -w '%{http_code}' -X PUT \
    --aws-sigv4 "aws:amz:${S3_REGION}:s3" --user "${S3_ACCESS_KEY}:${S3_SECRET_KEY}" \
    "${S3_ENDPOINT}/${S3_BUCKET}" || true)
  case "$code" in
    200) echo "Bucket ${S3_BUCKET} created"; exit 0 ;;
    409) echo "Bucket ${S3_BUCKET} already exists"; exit 0 ;;
    000|502|503) echo "RustFS not ready yet (HTTP $code, attempt $attempt)"; sleep 2 ;;
    *) echo "CreateBucket failed with HTTP $code"; cat /tmp/out; exit 1 ;;
  esac
done
echo "RustFS did not become ready"; exit 1
