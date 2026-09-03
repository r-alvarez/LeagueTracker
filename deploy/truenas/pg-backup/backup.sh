#!/bin/sh
# A logical dump of the tracker database on a schedule, into the dataset the
# ZFS snapshots and off-pool replication already cover. The snapshot of the
# live cluster folder is crash-consistent and restores by itself; the dump is
# the copy that restores anywhere (another host, another major version) with
# one pg_restore. pg_dump reads PGHOST/PGDATABASE/PGUSER/PGPASSWORD.
set -eu
: "${BACKUP_DIR:=/backups}"
: "${KEEP_DAYS:=14}"
: "${INTERVAL_SECONDS:=86400}"
mkdir -p "$BACKUP_DIR"
while :; do
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    target="$BACKUP_DIR/leaguetracker-$stamp.dump"
    if pg_dump --format=custom --file="$target.partial" && mv "$target.partial" "$target"; then
        echo "backup: $target ($(du -h "$target" | cut -f1))"
        find "$BACKUP_DIR" -name 'leaguetracker-*.dump' -mtime +"$KEEP_DAYS" -delete
    else
        echo "backup: pg_dump failed - the previous dumps are kept" >&2
        rm -f "$target.partial"
    fi
    sleep "$INTERVAL_SECONDS"
done
