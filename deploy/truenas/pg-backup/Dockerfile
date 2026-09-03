FROM postgres:18-alpine
COPY backup.sh /app/backup.sh
# Same lesson as the waker: the container runs as 568:568 and COPY keeps the
# checkout's mode, which a restrictive umask on the NAS has made unreadable.
RUN chmod 0755 /app/backup.sh
ENTRYPOINT ["/app/backup.sh"]
