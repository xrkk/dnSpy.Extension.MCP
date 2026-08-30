"""Raw HTTP adapter used by the PowerShell acceptance harness."""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

from .client import DnSpyClient, DnSpyConnectionError


def _parse_headers(values: list[str]) -> dict[str, str]:
    headers: dict[str, str] = {}
    for value in values:
        name, separator, content = value.partition(":")
        if not separator or not name.strip():
            raise ValueError(f"Invalid header {value!r}; expected 'Name: value'")
        headers[name.strip()] = content.lstrip()
    return headers


def _render_headers(status: int, reason: str, headers: dict[str, str], body: bytes) -> bytes:
    lines = [f"HTTP/1.1 {status} {reason}"]
    lines.extend(f"{key}: {value}" for key, value in headers.items())
    return ("\r\n".join(lines) + "\r\n\r\n").encode("utf-8") + body


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Send one raw request through dnspy_mcp.DnSpyClient")
    parser.add_argument("--url", default=os.getenv("DNSPY_MCP_URL", "http://localhost:15378/"))
    parser.add_argument("--path")
    parser.add_argument("--method", default="POST")
    body = parser.add_mutually_exclusive_group()
    body.add_argument("--body-file", type=Path)
    body.add_argument("--body-utf8")
    parser.add_argument("--header", action="append", default=[])
    parser.add_argument("--token", default=os.getenv("DNSPY_MCP_TOKEN"))
    parser.add_argument("--timeout", type=float, default=40.0)
    parser.add_argument("--hold-seconds", type=float, default=0.0)
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--format",
        choices=("body", "status", "curl", "headers", "envelope"),
        default="body",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        headers = _parse_headers(args.header)
    except ValueError as exc:
        print(exc, file=sys.stderr)
        return 2
    if args.body_file:
        body = args.body_file.read_bytes()
    elif args.body_utf8 is not None:
        body = args.body_utf8.encode("utf-8")
    else:
        body = None
    client = DnSpyClient(args.url, token=args.token, timeout=args.timeout)
    try:
        if args.hold_seconds > 0:
            with client.open_request(
                args.method,
                path=args.path,
                body=body,
                headers=headers,
                include_session=False,
            ) as opened:
                status, reason, response_headers = opened.status, opened.reason, dict(opened.headers)
                response_body = b""
                time.sleep(args.hold_seconds)
        else:
            response = client.raw_request(
                args.method,
                path=args.path,
                body=body,
                headers=headers,
                include_session=False,
            )
            status, reason = response.status, response.reason
            response_headers, response_body = dict(response.headers), response.body
    except DnSpyConnectionError as exc:
        # curl's status formatter reports transport failures as 000 while still allowing
        # PowerShell harness code to decide whether to start/restart dnSpy. A non-zero native
        # exit becomes a terminating NativeCommandError under the harness's Stop policy and
        # would abort before Ensure-CanonicalDnSpy gets that chance.
        if args.format in {"status", "curl"}:
            rendered = b"000" if args.format == "status" else b"\n000"
            if args.output:
                args.output.write_bytes(rendered)
            else:
                sys.stdout.buffer.write(rendered)
                sys.stdout.buffer.flush()
            return 0
        print(str(exc), file=sys.stderr)
        return 3

    if args.format == "status":
        rendered = str(status).encode("ascii")
    elif args.format == "curl":
        rendered = response_body + b"\n" + str(status).encode("ascii")
    elif args.format == "headers":
        rendered = _render_headers(status, reason, response_headers, response_body)
    elif args.format == "envelope":
        rendered = json.dumps(
            {
                "status": status,
                "reason": reason,
                "headers": response_headers,
                "body": response_body.decode("utf-8", errors="replace"),
            },
            ensure_ascii=False,
            separators=(",", ":"),
        ).encode("utf-8")
    else:
        rendered = response_body
    if args.output:
        args.output.write_bytes(rendered)
    else:
        sys.stdout.buffer.write(rendered)
        sys.stdout.buffer.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
