"""Human-oriented command line interface for the dnSpy MCP client."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Any

from .client import DnSpyClient, DnSpyError, DnSpyHttpError


def _json(value: Any) -> None:
    print(json.dumps(value, ensure_ascii=False, indent=2))


def _arguments(args: argparse.Namespace) -> dict[str, Any]:
    if args.arguments_file:
        value = json.loads(args.arguments_file.read_text(encoding="utf-8"))
    else:
        value = json.loads(args.arguments)
    if not isinstance(value, dict):
        raise ValueError("tool arguments must be a JSON object")
    return value


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Python client for dnSpy.Extension.MCP")
    parser.add_argument("--url", default=os.getenv("DNSPY_MCP_URL", "http://localhost:15378/"))
    parser.add_argument("--token", default=os.getenv("DNSPY_MCP_TOKEN"))
    parser.add_argument("--timeout", type=float, default=float(os.getenv("DNSPY_MCP_TIMEOUT", "40")))
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("health")
    sub.add_parser("tools")
    resources = sub.add_parser("resources")
    resources.add_argument("--read")
    call = sub.add_parser("call")
    call.add_argument("name")
    group = call.add_mutually_exclusive_group()
    group.add_argument("--arguments", default="{}")
    group.add_argument("--arguments-file", type=Path)
    limit = sub.add_parser("session-limit", help="open N simultaneous sessions and print HTTP statuses")
    limit.add_argument("--count", type=int, default=17)
    return parser


def _session_limit(args: argparse.Namespace) -> int:
    clients: list[DnSpyClient] = []
    statuses: list[int] = []
    try:
        for _ in range(args.count):
            client = DnSpyClient(args.url, token=args.token, timeout=args.timeout, client_name="acc-004")
            clients.append(client)
            try:
                client.initialize(send_initialized=False)
            except DnSpyHttpError as exc:
                statuses.append(exc.response.status)
            else:
                statuses.append(200)
        _json(statuses)
        return 0
    finally:
        for client in clients:
            try:
                client.close()
            except DnSpyError:
                pass


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.command == "session-limit":
        return _session_limit(args)
    client = DnSpyClient(args.url, token=args.token, timeout=args.timeout)
    try:
        if args.command == "health":
            response = client.health()
            response.raise_for_status()
            _json(response.json())
            return 0
        client.initialize()
        if args.command == "tools":
            _json(list(client.iter_tools()))
        elif args.command == "resources":
            _json(client.read_resource(args.read) if args.read else client.list_resources())
        elif args.command == "call":
            _json(client.call_tool_json(args.name, _arguments(args)))
        return 0
    except (DnSpyError, ValueError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    finally:
        try:
            client.close()
        except DnSpyError:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
