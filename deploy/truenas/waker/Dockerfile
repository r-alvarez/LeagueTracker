FROM python:3.14-alpine
ENV PYTHONUNBUFFERED=1
COPY waker.py /app/waker.py
# The container runs as an unprivileged user (compose: 568:568) and COPY keeps
# the checkout's file mode - a restrictive umask on the NAS made waker.py
# unreadable (Errno 13). Make the mode explicit rather than inherited.
RUN chmod 0644 /app/waker.py
CMD ["python", "/app/waker.py"]
