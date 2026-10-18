# -*- coding: utf-8 -*-
"""Сеть стенда до серверов конфигураций без правки хоста.

В пространстве стенда интерфейс-заглушка agnet0: маршрут по умолчанию в никуда и адреса Endpoint конфигураций
стенда из диапазона для документации. Датаграммы между этими адресами и настоящими серверами передаёт
ретранслятор: его сокеты к серверам открыты в пространстве хоста, сокеты для туннеля - в пространстве стенда.
Служебный порт сервера (`# AmneziaGeo Services`, TCP) передаётся так же. Интернет у стенда есть только через туннель.

python3 kit/net.py add <имя> <адрес:порт>  - конфигурация из stdin: Endpoint заменяется адресом стенда,
                                             настоящий записывается в local/relay.json
python3 kit/net.py up | down              - поднять или погасить сеть и ретранслятор
python3 kit/net.py relay <pid> <таблица>  - сам ретранслятор (запускает up от root)
"""
import json
import os
import re
import select
import socket
import subprocess
import sys
import threading

KIT = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.dirname(KIT)
LOCAL = os.path.join(BASE, "local")
TABLE = os.path.join(LOCAL, "relay.json")
RUN = "/var/tmp/agdocs/run"
PID = os.path.join(RUN, "relay.pid")
DEVICE = "agnet0"


def holder():
    with open(os.path.join(RUN, "ns.pid")) as source:
        return source.read().strip()


def table():
    if not os.path.exists(TABLE):
        return []
    with open(TABLE) as source:
        return json.load(source)


def add(name, listen):
    text = sys.stdin.read()
    match = re.search(r"^\s*Endpoint\s*=\s*(\S+)\s*$", text, re.M)
    if not match:
        raise SystemExit("no Endpoint in the configuration")
    server = match.group(1)
    if ":" not in server or server.startswith("["):
        raise SystemExit("Endpoint is not host:port")
    host, port = server.rsplit(":", 1)
    address = socket.getaddrinfo(host, int(port), socket.AF_INET, socket.SOCK_DGRAM)[0][4][0]
    os.makedirs(os.path.join(LOCAL, "configs"), mode=0o700, exist_ok=True)
    path = os.path.join(LOCAL, "configs", name + ".conf")
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w") as target:
        target.write(text[:match.start(1)] + listen + text[match.end(1):])
    rows = [row for row in table() if row["name"] != name]
    row = {"name": name, "listen": listen, "server": "%s:%s" % (address, port)}
    services = re.search(r"^\s*#\s*AmneziaGeo Services\s*=\s*(\d+)\s*$", text, re.M)
    if services:
        row["services"] = int(services.group(1))
    rows.append(row)
    descriptor = os.open(TABLE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w") as target:
        json.dump(rows, target, indent=2)
    print("%s: Endpoint %s, relayed" % (name, listen))


def inside(*command):
    return subprocess.run(["sudo", "-n", "nsenter", "-t", holder(), "-n", "--", *command], capture_output=True, text=True)


def up():
    rows = table()
    if not rows:
        raise SystemExit("no relay table: python3 kit/net.py add ...")
    inside("ip", "link", "add", DEVICE, "type", "dummy")
    inside("ip", "link", "set", DEVICE, "up")
    inside("ip", "addr", "add", "203.0.113.1/24", "dev", DEVICE)
    for row in rows:
        inside("ip", "addr", "add", row["listen"].rsplit(":", 1)[0] + "/32", "dev", DEVICE)
    inside("ip", "route", "replace", "default", "via", "203.0.113.254", "dev", DEVICE)
    down_relay()
    log = open(os.path.join(RUN, "relay.log"), "w")
    process = subprocess.Popen(["sudo", "-n", "python3", os.path.abspath(__file__), "relay", holder(), TABLE],
                               stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
    with open(PID, "w") as target:
        target.write(str(process.pid))
    print("net up: %s" % ", ".join(row["listen"] for row in rows))


def down_relay():
    if not os.path.exists(PID):
        return
    with open(PID) as source:
        pid = source.read().strip()
    children = subprocess.run(["pgrep", "-P", pid], capture_output=True, text=True).stdout.split()
    for target in children + [pid]:
        subprocess.run(["sudo", "-n", "kill", target], capture_output=True)
    os.remove(PID)


def down():
    down_relay()
    print("net down")


def buffered(sock):
    """Большие буферы: всплеск датаграмм туннеля не теряется, пока цикл занят."""
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 << 20)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4 << 20)
    return sock


def stand_sockets(pid, rows):
    """Сокеты в пространстве стенда: поток входит в него сам, остальной процесс остаётся в пространстве хоста."""
    made = {}

    def make():
        with open("/proc/%s/ns/net" % pid) as space:
            os.setns(space.fileno(), os.CLONE_NEWNET)
        for row in rows:
            host, port = row["listen"].rsplit(":", 1)
            tunnel = buffered(socket.socket(socket.AF_INET, socket.SOCK_DGRAM))
            tunnel.bind((host, int(port)))
            made[row["name"]] = [tunnel, None]
            if "services" in row:
                listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                listener.bind((host, row["services"]))
                listener.listen(16)
                made[row["name"]][1] = listener

    thread = threading.Thread(target=make)
    thread.start()
    thread.join()
    return made


def pump(source, target):
    try:
        while True:
            data = source.recv(65536)
            if not data:
                break
            target.sendall(data)
    except OSError:
        pass
    for sock in (source, target):
        try:
            sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass


def serve(listener, server):
    """Служебный порт сервера (TCP): соединение из стенда - новое соединение с сервером из пространства хоста."""
    while True:
        inner, _ = listener.accept()
        try:
            outer = socket.create_connection(server, timeout=10)
            outer.settimeout(None)
        except OSError as error:
            print("services: %s" % error, flush=True)
            inner.close()
            continue
        threading.Thread(target=pump, args=(inner, outer), daemon=True).start()
        threading.Thread(target=pump, args=(outer, inner), daemon=True).start()


def relay(pid, path):
    with open(path) as source:
        rows = json.load(source)
    made = stand_sockets(pid, rows)
    pairs = {}
    for row in rows:
        host, port = row["server"].rsplit(":", 1)
        server = buffered(socket.socket(socket.AF_INET, socket.SOCK_DGRAM))
        server.connect((host, int(port)))
        tunnel, listener = made[row["name"]]
        pair = {"tunnel": tunnel, "server": server, "peer": None}
        pairs[tunnel] = pair
        pairs[server] = pair
        if listener is not None:
            threading.Thread(target=serve, args=(listener, (host, row["services"])), daemon=True).start()
    print("relaying %d" % len(rows), flush=True)
    while True:
        ready, _, _ = select.select(list(pairs), [], [])
        for sock in ready:
            pair = pairs[sock]
            try:
                data, peer = sock.recvfrom(65535)
                if sock is pair["tunnel"]:
                    pair["peer"] = peer
                    pair["server"].send(data)
                elif pair["peer"] is not None:
                    pair["tunnel"].sendto(data, pair["peer"])
            except OSError as error:
                print("relay: %s" % error, flush=True)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "add" and len(sys.argv) == 4:
        add(sys.argv[2], sys.argv[3])
    elif command == "up":
        up()
    elif command == "down":
        down()
    elif command == "relay" and len(sys.argv) == 4:
        relay(sys.argv[2], sys.argv[3])
    else:
        raise SystemExit(__doc__)
