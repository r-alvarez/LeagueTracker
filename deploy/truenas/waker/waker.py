"""Wake-on-LAN waker for the render PC.

Polls every tracker's render queue and broadcasts a WoL magic packet while
any job is waiting. The render agent is pull-based, so a sleeping PC cannot
discover its own work - this loop is what summons it. Magic packets are
harmless no-ops on a machine that is already awake, so no "is it awake"
probe is needed (Windows firewalls routinely eat ICMP, making ping a liar).
"""

import json
import os
import socket
import time
import urllib.request

TRACKERS = [u.strip().rstrip("/") for u in os.environ.get("TRACKER_URLS", "").split(",") if u.strip()]
MAC = os.environ["PC_MAC"].replace(":", "").replace("-", "").lower()
BROADCAST = os.environ.get("WOL_BROADCAST", "255.255.255.255")
POLL_SECONDS = int(os.environ.get("POLL_SECONDS", "60"))

# "rendering" means the PC holds a lease, so it is awake and working;
# "done"/"failed"/"no-events" need nothing. Only these two mean idle work.
WAKE_STATUSES = {"pending", "partial"}


def log(msg: str) -> None:
    print(time.strftime("%H:%M:%S"), msg, flush=True)


def pending_jobs(base_url: str) -> list[str]:
    req = urllib.request.Request(f"{base_url}/api/render/queue", headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        body = resp.read()
    # A Cloudflare Access sign-in page (HTML, not JSON) lands here too and
    # raises - meaning DNS resolved the tracker via the internet instead of
    # the LAN's split-horizon view. The caller logs it; fix the NAS DNS.
    rows = json.loads(body)
    return [
        str(row.get("matchId") or row.get("MatchId") or "?")
        for row in rows
        if str(row.get("status") or row.get("Status") or "").lower() in WAKE_STATUSES
    ]


def send_magic_packet() -> None:
    packet = bytes.fromhex("ff" * 6 + MAC * 16)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        s.sendto(packet, (BROADCAST, 9))


def main() -> None:
    if not TRACKERS:
        raise SystemExit("TRACKER_URLS is empty - nothing to watch")
    log(f"watching {len(TRACKERS)} tracker(s), waking {MAC} via {BROADCAST}:9 every {POLL_SECONDS}s while work waits")

    was_waking = False
    unreachable: set[str] = set()
    while True:
        jobs: list[str] = []
        for url in TRACKERS:
            try:
                jobs += pending_jobs(url)
            except Exception as ex:  # noqa: BLE001 - one line per outage, not per poll
                if url not in unreachable:
                    unreachable.add(url)
                    log(f"cannot read queue at {url}: {ex} (not repeated until it recovers)")
                continue
            unreachable.discard(url)

        if jobs:
            send_magic_packet()
            if not was_waking:
                log(f"{len(jobs)} job(s) waiting ({', '.join(sorted(set(jobs))[:5])}) - sending wake packets")
            was_waking = True
        elif was_waking:
            log("queue drained - going quiet")
            was_waking = False

        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    main()
