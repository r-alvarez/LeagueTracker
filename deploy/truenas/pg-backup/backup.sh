#!/bin/sh
# The ZFS snapshot of the cluster folder restores only onto the same major
# version at the same path; this dump is the copy that restores anywhere.
# pg_dump reads PGHOST/PGDATABASE/PGUSER/PGPASSWORD.
set -eu
: "${BACKUP_DIR:=/backups}"
: "${KEEP_DAYS:=14}"
: "${BACKUP_AT:=02:00}"

# Epoch arithmetic is UTC by definition, so the wall-clock target needs no
# date parsing (busybox date -d is not one to lean on). Leading zeros are
# stripped: "08" is an invalid octal literal to the shell.
hour=${BACKUP_AT%%:*}
minute=${BACKUP_AT#*:}
run_at_seconds=$(( ${hour#0} * 3600 + ${minute#0} * 60 ))

seconds_until_next_run() {
    now=$(date -u +%s)
    target=$(( now - now % 86400 + run_at_seconds ))
    [ "$target" -gt "$now" ] || target=$(( target + 86400 ))
    echo $(( target - now ))
}

dump_once() {
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    target="$BACKUP_DIR/leaguetracker-$stamp.dump"
    if pg_dump --format=custom --file="$target.partial" && mv "$target.partial" "$target"; then
        echo "backup: $target ($(du -h "$target" | cut -f1))"
        find "$BACKUP_DIR" -name 'leaguetracker-*.dump' -mtime +"$KEEP_DAYS" -delete
    else
        echo "backup: pg_dump failed - the previous dumps are kept" >&2
        rm -f "$target.partial"
    fi
}

mkdir -p "$BACKUP_DIR"
while :; do
    wait=$(seconds_until_next_run)
    echo "backup: next run at ${BACKUP_AT}Z, in ${wait}s"
    sleep "$wait"
    dump_once
done
