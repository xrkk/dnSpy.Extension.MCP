"""Typed, dependency-free client for the dnSpy MCP Streamable HTTP endpoint."""

from __future__ import annotations

import json
import gzip
import os
import threading
from dataclasses import dataclass
from email.message import Message
from http.client import HTTPConnection, HTTPSConnection, HTTPResponse as StdlibHttpResponse
from typing import Any, Iterator, Mapping
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin, urlparse
from urllib.request import OpenerDirector, ProxyHandler, Request, build_opener


DEFAULT_PROTOCOL_VERSION = "2025-06-18"
DEFAULT_TIMEOUT = 40.0
_MISSING = object()


class DnSpyError(RuntimeError):
    """Base class for client errors."""


class DnSpyConnectionError(DnSpyError):
    """The HTTP endpoint could not be reached."""


class DnSpyHttpError(DnSpyError):
    """The endpoint returned a non-success HTTP status."""

    def __init__(self, response: "HttpResponse") -> None:
        self.response = response
        detail = f": {response.text.strip()}" if response.body else ""
        super().__init__(f"dnSpy MCP returned HTTP {response.status}{detail}")


class DnSpyProtocolError(DnSpyError):
    """The endpoint returned invalid JSON-RPC or a JSON-RPC error."""

    def __init__(
        self,
        message: str,
        *,
        code: int | None = None,
        data: Any = None,
        response: "HttpResponse | None" = None,
    ) -> None:
        self.code = code
        self.data = data
        self.response = response
        prefix = f"JSON-RPC {code}: " if code is not None else ""
        super().__init__(prefix + message)


class ToolCallError(DnSpyProtocolError):
    """A tools/call result used MCP's isError flag."""


@dataclass(frozen=True, slots=True)
class HttpResponse:
    """A fully buffered HTTP response, including non-2xx responses."""

    status: int
    reason: str
    headers: Mapping[str, str]
    body: bytes

    @property
    def text(self) -> str:
        return self.body.decode("utf-8", errors="replace")

    def json(self) -> Any:
        try:
            return json.loads(self.body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise DnSpyProtocolError(
                "dnSpy MCP returned a non-JSON response",
                response=self,
            ) from exc

    def header(self, name: str, default: str | None = None) -> str | None:
        wanted = name.casefold()
        for key, value in self.headers.items():
            if key.casefold() == wanted:
                return value
        return default

    def raise_for_status(self) -> None:
        if not 200 <= self.status < 300:
            raise DnSpyHttpError(self)


class _OpenResponse:
    """Context manager used by transport-limit tests that must hold an SSE socket."""

    def __init__(self, response: StdlibHttpResponse | HTTPError) -> None:
        self._response = response
        self.status = int(response.code)
        self.reason = str(getattr(response, "reason", ""))
        self.headers = _headers_to_dict(response.headers)

    def read(self, amount: int = -1) -> bytes:
        return self._response.read(amount)

    def readline(self) -> bytes:
        return self._response.readline()

    def close(self) -> None:
        self._response.close()

    def __enter__(self) -> "_OpenResponse":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def _headers_to_dict(headers: Message) -> dict[str, str]:
    result: dict[str, str] = {}
    for key, value in headers.items():
        if key in result:
            result[key] = f"{result[key]}, {value}"
        else:
            result[key] = value
    return result


class DnSpyClient:
    """Connect to dnSpy.Extension.MCP over MCP Streamable HTTP.

    The client owns at most one server-side MCP session. Create multiple instances when a
    test or application needs independent sessions. The implementation deliberately uses
    only Python's standard library, so the same package runs on the Linux host and the
    Windows acceptance VM without bootstrapping requests/httpx.
    """

    def __init__(
        self,
        base_url: str | None = None,
        *,
        token: str | None = None,
        timeout: float | None = None,
        protocol_version: str = DEFAULT_PROTOCOL_VERSION,
        client_name: str = "dnspy-mcp-python",
        client_version: str = "0.1.0",
        trust_environment_proxy: bool = False,
        opener: OpenerDirector | None = None,
    ) -> None:
        configured_url = base_url or os.getenv("DNSPY_MCP_URL", "http://localhost:15378/")
        parsed = urlparse(configured_url)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc:
            raise ValueError(f"Invalid dnSpy MCP URL: {configured_url!r}")
        self.base_url = configured_url
        self.token = token if token is not None else os.getenv("DNSPY_MCP_TOKEN")
        configured_timeout = os.getenv("DNSPY_MCP_TIMEOUT")
        self.timeout = float(timeout if timeout is not None else configured_timeout or DEFAULT_TIMEOUT)
        self.protocol_version = protocol_version
        self.client_name = client_name
        self.client_version = client_version
        self.session_id: str | None = None
        self.server_info: Mapping[str, Any] | None = None
        self.instructions: str | None = None
        self._request_id = 0
        self._id_lock = threading.Lock()
        self._http_lock = threading.Lock()
        self._http_connection: HTTPConnection | HTTPSConnection | None = None
        self._use_direct_http = opener is None and not trust_environment_proxy
        if opener is not None:
            self._opener = opener
        elif trust_environment_proxy:
            self._opener = build_opener()
        else:
            # MCP endpoints are normally loopback/LAN services. Ignoring ambient HTTP proxy
            # variables prevents a host proxy from swallowing requests to the Windows VM.
            self._opener = build_opener(ProxyHandler({}))

    @classmethod
    def connect(cls, base_url: str | None = None, **kwargs: Any) -> "DnSpyClient":
        client = cls(base_url, **kwargs)
        client.initialize()
        return client

    def __enter__(self) -> "DnSpyClient":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def _next_id(self) -> int:
        with self._id_lock:
            self._request_id += 1
            return self._request_id

    def _url(self, path: str | None) -> str:
        if path is None or path == "":
            return self.base_url
        return urljoin(self.base_url, path)

    def _headers(
        self,
        extra: Mapping[str, str] | None,
        *,
        include_session: bool,
    ) -> dict[str, str]:
        headers = {
            "Accept": "application/json, text/event-stream",
            "User-Agent": f"dnspy-mcp-client/{self.client_version}",
        }
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        if include_session and self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
            headers["MCP-Protocol-Version"] = self.protocol_version
        if extra:
            headers.update(extra)
        return headers

    def open_request(
        self,
        method: str,
        *,
        path: str | None = None,
        body: bytes | str | None = None,
        headers: Mapping[str, str] | None = None,
        include_session: bool = True,
        timeout: float | None = None,
    ) -> _OpenResponse:
        """Open an HTTP request without buffering its body.

        This is primarily useful for legacy/Streamable SSE admission tests. Normal callers
        should use :meth:`raw_request`, which always closes the response after reading it.
        """

        data = body.encode("utf-8") if isinstance(body, str) else body
        request_headers = self._headers(headers, include_session=include_session)
        if data is not None and not any(k.casefold() == "content-type" for k in request_headers):
            request_headers["Content-Type"] = "application/json"
        request = Request(self._url(path), data=data, headers=request_headers, method=method.upper())
        try:
            response = self._opener.open(request, timeout=timeout or self.timeout)
        except HTTPError as exc:
            response = exc
        except (URLError, OSError, TimeoutError) as exc:
            raise DnSpyConnectionError(f"Cannot reach dnSpy MCP at {request.full_url}: {exc}") from exc
        return _OpenResponse(response)

    def raw_request(
        self,
        method: str,
        *,
        path: str | None = None,
        body: bytes | str | None = None,
        headers: Mapping[str, str] | None = None,
        include_session: bool = True,
        timeout: float | None = None,
    ) -> HttpResponse:
        """Send arbitrary bytes and return the status, headers and body without policy.

        Acceptance tests use this API for malformed JSON, byte ceilings, authentication
        walls and HTTP admission limits. Keeping those operations in the client avoids all
        shell quoting and curl argument parsing.
        """

        if self._use_direct_http:
            return self._decode_response_content(self._direct_raw_request(
                method,
                path=path,
                body=body,
                headers=headers,
                include_session=include_session,
                timeout=timeout,
            ))

        with self.open_request(
            method,
            path=path,
            body=body,
            headers=headers,
            include_session=include_session,
            timeout=timeout,
        ) as opened:
            response = self._decode_response_content(
                HttpResponse(opened.status, opened.reason, opened.headers, opened.read())
            )
        session_id = response.header("Mcp-Session-Id")
        if session_id:
            self.session_id = session_id
        return response

    @staticmethod
    def _decode_response_content(response: HttpResponse) -> HttpResponse:
        encoding = response.header("Content-Encoding", "") or ""
        if "gzip" not in encoding.casefold():
            return response
        try:
            body = gzip.decompress(response.body)
        except (OSError, EOFError) as exc:
            raise DnSpyProtocolError("dnSpy MCP returned an invalid gzip response", response=response) from exc
        return HttpResponse(response.status, response.reason, response.headers, body)

    def _direct_raw_request(
        self,
        method: str,
        *,
        path: str | None,
        body: bytes | str | None,
        headers: Mapping[str, str] | None,
        include_session: bool,
        timeout: float | None,
    ) -> HttpResponse:
        """Use one persistent stdlib connection for normal MCP traffic.

        Windows HTTP.sys can truncate larger responses when urllib opens a fresh
        ``Connection: close`` socket for every request.  A persistent
        ``http.client`` connection avoids that transport bug while keeping the
        package dependency-free.  ``open_request`` remains urllib-based because
        admission tests and legacy SSE intentionally hold independent sockets.
        """

        data = body.encode("utf-8") if isinstance(body, str) else body
        request_headers = self._headers(headers, include_session=include_session)
        if data is not None and not any(k.casefold() == "content-type" for k in request_headers):
            request_headers["Content-Type"] = "application/json"
        parsed = urlparse(self._url(path))
        target = parsed.path or "/"
        if parsed.query:
            target += "?" + parsed.query

        with self._http_lock:
            connection = self._http_connection
            if connection is None:
                connection_type = HTTPSConnection if parsed.scheme == "https" else HTTPConnection
                connection = connection_type(parsed.hostname, parsed.port, timeout=timeout or self.timeout)
                self._http_connection = connection
            try:
                connection.request(method.upper(), target, body=data, headers=request_headers)
                opened = connection.getresponse()
                response = HttpResponse(
                    int(opened.status),
                    str(opened.reason or ""),
                    _headers_to_dict(opened.headers),
                    opened.read(),
                )
            except (OSError, TimeoutError) as exc:
                connection.close()
                self._http_connection = None
                raise DnSpyConnectionError(f"Cannot reach dnSpy MCP at {self._url(path)}: {exc}") from exc

        session_id = response.header("Mcp-Session-Id")
        if session_id:
            self.session_id = session_id
        return response

    def request_object(
        self,
        message: Mapping[str, Any],
        *,
        include_session: bool = True,
        timeout: float | None = None,
    ) -> HttpResponse:
        """Serialize and send one complete JSON-RPC object."""

        payload = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        return self.raw_request(
            "POST",
            body=payload,
            headers={"Content-Type": "application/json", "Accept-Encoding": "gzip"},
            include_session=include_session,
            timeout=timeout,
        )

    def request(
        self,
        method: str,
        params: Any = _MISSING,
        *,
        request_id: str | int | None | object = _MISSING,
        include_session: bool = True,
        timeout: float | None = None,
    ) -> Any:
        """Invoke JSON-RPC and return ``result``; raise on HTTP or JSON-RPC errors.

        Pass ``request_id=None`` to send a notification. Omitting it allocates a numeric ID.
        """

        actual_id = self._next_id() if request_id is _MISSING else request_id
        message: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if actual_id is not None:
            message["id"] = actual_id
        if params is not _MISSING:
            message["params"] = params
        response = self.request_object(
            message,
            include_session=include_session,
            timeout=timeout,
        )
        response.raise_for_status()
        if actual_id is None:
            return None
        if not response.body:
            raise DnSpyProtocolError("dnSpy MCP returned an empty JSON-RPC response", response=response)
        payload = response.json()
        if not isinstance(payload, dict):
            raise DnSpyProtocolError("dnSpy MCP response is not a JSON object", response=response)
        if payload.get("id") != actual_id:
            raise DnSpyProtocolError(
                f"response id {payload.get('id')!r} does not match request id {actual_id!r}",
                response=response,
            )
        error = payload.get("error")
        if isinstance(error, dict):
            raise DnSpyProtocolError(
                str(error.get("message", "Unknown JSON-RPC error")),
                code=error.get("code") if isinstance(error.get("code"), int) else None,
                data=error.get("data"),
                response=response,
            )
        if "result" not in payload:
            raise DnSpyProtocolError("JSON-RPC response has neither result nor error", response=response)
        return payload["result"]

    def initialize(self, *, send_initialized: bool = True) -> Mapping[str, Any]:
        """Negotiate MCP, allocate a Streamable HTTP session, and notify readiness."""

        result = self.request(
            "initialize",
            {
                "protocolVersion": self.protocol_version,
                "capabilities": {},
                "clientInfo": {"name": self.client_name, "version": self.client_version},
            },
            include_session=False,
        )
        if not isinstance(result, dict):
            raise DnSpyProtocolError("initialize result is not an object")
        negotiated = result.get("protocolVersion")
        if isinstance(negotiated, str):
            self.protocol_version = negotiated
        server_info = result.get("serverInfo")
        self.server_info = server_info if isinstance(server_info, dict) else None
        instructions = result.get("instructions")
        self.instructions = instructions if isinstance(instructions, str) else None
        if send_initialized:
            self.notify("notifications/initialized")
        return result

    def notify(self, method: str, params: Any = _MISSING) -> None:
        self.request(method, params, request_id=None)

    def list_tools(self, *, cursor: str | None = None) -> Mapping[str, Any]:
        params = {"cursor": cursor} if cursor else {}
        result = self.request("tools/list", params)
        if not isinstance(result, dict):
            raise DnSpyProtocolError("tools/list result is not an object")
        return result

    def iter_tools(self) -> Iterator[Mapping[str, Any]]:
        cursor: str | None = None
        while True:
            page = self.list_tools(cursor=cursor)
            tools = page.get("tools", [])
            if not isinstance(tools, list):
                raise DnSpyProtocolError("tools/list result.tools is not an array")
            for tool in tools:
                if isinstance(tool, dict):
                    yield tool
            next_cursor = page.get("nextCursor")
            if not isinstance(next_cursor, str) or not next_cursor:
                return
            cursor = next_cursor

    def call_tool(self, name: str, arguments: Mapping[str, Any] | None = None) -> Mapping[str, Any]:
        result = self.request("tools/call", {"name": name, "arguments": dict(arguments or {})})
        if not isinstance(result, dict):
            raise DnSpyProtocolError("tools/call result is not an object")
        if result.get("isError") is True:
            text = self._first_text(result) or f"Tool {name!r} failed"
            raise ToolCallError(text)
        return result

    def call_tool_json(self, name: str, arguments: Mapping[str, Any] | None = None) -> Any:
        """Call a tool and return structuredContent or decode its first text item as JSON."""

        result = self.call_tool(name, arguments)
        if "structuredContent" in result:
            return result["structuredContent"]
        text = self._first_text(result)
        if text is None:
            return None
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return text

    # Structured-edit convenience methods.  They deliberately preserve the caller's
    # request_id and expected_revision: reconnecting never guesses a new endpoint or silently
    # replays a mutation payload.
    def edit_begin(
        self,
        request_id: str,
        assembly_name: str,
        *,
        module_mvid: str | None = None,
        source_family_id: str | None = None,
    ) -> Mapping[str, Any]:
        arguments: dict[str, Any] = {
            "request_id": request_id,
            "assembly_name": assembly_name,
        }
        if module_mvid is not None:
            arguments["module_mvid"] = module_mvid
        if source_family_id is not None:
            arguments["source_family_id"] = source_family_id
        return self._edit_call("edit_begin", arguments)

    def edit_status(self) -> Mapping[str, Any]:
        return self._edit_call("edit_status", {})

    def edit_apply(
        self,
        request_id: str,
        transaction_id: str,
        expected_revision: int,
        operation: Mapping[str, Any],
    ) -> Mapping[str, Any]:
        return self._edit_call(
            "edit_apply",
            {
                "request_id": request_id,
                "transaction_id": transaction_id,
                "expected_revision": expected_revision,
                "operation": dict(operation),
            },
        )

    def edit_review(
        self,
        request_id: str,
        transaction_id: str,
        expected_revision: int,
        *,
        dynamic_validation: Mapping[str, Any] | None = None,
    ) -> Mapping[str, Any]:
        arguments: dict[str, Any] = {
            "request_id": request_id,
            "transaction_id": transaction_id,
            "expected_revision": expected_revision,
        }
        if dynamic_validation is not None:
            arguments["dynamic_validation"] = dict(dynamic_validation)
        return self._edit_call("edit_review", arguments)

    def edit_rollback(self, request_id: str, transaction_id: str) -> Mapping[str, Any]:
        return self._edit_call(
            "edit_rollback",
            {"request_id": request_id, "transaction_id": transaction_id},
        )

    def edit_commit(
        self,
        request_id: str,
        transaction_id: str,
        expected_revision: int,
        review_id: str,
        review_revision: int,
        confirmed_risk_ids: list[str] | tuple[str, ...],
    ) -> Mapping[str, Any]:
        return self._edit_call("edit_commit", {
            "request_id": request_id,
            "transaction_id": transaction_id,
            "expected_revision": expected_revision,
            "review_id": review_id,
            "review_revision": review_revision,
            "confirmed_risk_ids": list(confirmed_risk_ids),
        })

    def edit_history(
        self,
        *,
        lineage_id: str | None = None,
        checkpoint_id: str | None = None,
        cursor: str | None = None,
        page_size: int | None = None,
    ) -> Mapping[str, Any]:
        arguments: dict[str, Any] = {}
        for name, value in (("lineage_id", lineage_id), ("checkpoint_id", checkpoint_id),
                            ("cursor", cursor), ("page_size", page_size)):
            if value is not None:
                arguments[name] = value
        return self._edit_call("edit_history", arguments)

    def edit_undo(self, request_id: str, lineage_id: str, expected_checkpoint_id: str) -> Mapping[str, Any]:
        return self._edit_call("edit_undo", {
            "request_id": request_id, "lineage_id": lineage_id,
            "expected_checkpoint_id": expected_checkpoint_id,
        })

    def edit_redo(
        self,
        request_id: str,
        lineage_id: str,
        expected_checkpoint_id: str,
        *,
        child_checkpoint_id: str | None = None,
    ) -> Mapping[str, Any]:
        arguments = {
            "request_id": request_id, "lineage_id": lineage_id,
            "expected_checkpoint_id": expected_checkpoint_id,
        }
        if child_checkpoint_id is not None:
            arguments["child_checkpoint_id"] = child_checkpoint_id
        return self._edit_call("edit_redo", arguments)

    def edit_restore(
        self,
        request_id: str,
        lineage_id: str,
        checkpoint_id: str,
        action: str,
        *,
        replay_id: str | None = None,
        expected_live_fingerprint: str | None = None,
        confirm_validated_drift: bool | None = None,
    ) -> Mapping[str, Any]:
        arguments: dict[str, Any] = {
            "request_id": request_id, "lineage_id": lineage_id,
            "checkpoint_id": checkpoint_id, "action": action,
        }
        for name, value in (("replay_id", replay_id), ("expected_live_fingerprint", expected_live_fingerprint),
                            ("confirm_validated_drift", confirm_validated_drift)):
            if value is not None:
                arguments[name] = value
        return self._edit_call("edit_restore", arguments)

    def edit_export(
        self,
        request_id: str,
        lineage_id: str,
        checkpoint_id: str,
        *,
        output_path: str | None = None,
    ) -> Mapping[str, Any]:
        arguments = {"request_id": request_id, "lineage_id": lineage_id, "checkpoint_id": checkpoint_id}
        if output_path is not None:
            arguments["output_path"] = output_path
        return self._edit_call("edit_export", arguments)

    def edit_recover(self, request_id: str, recovery_id: str, action: str) -> Mapping[str, Any]:
        return self._edit_call("edit_recover", {
            "request_id": request_id, "recovery_id": recovery_id, "action": action,
        })

    def edit_accept_live(
        self,
        request_id: str,
        assembly_name: str,
        source_family_id: str,
        superseded_lineage_id: str,
        expected_live_fingerprint: str,
        *,
        module_mvid: str | None = None,
    ) -> Mapping[str, Any]:
        arguments = {
            "request_id": request_id, "assembly_name": assembly_name,
            "source_family_id": source_family_id, "superseded_lineage_id": superseded_lineage_id,
            "expected_live_fingerprint": expected_live_fingerprint,
            "acknowledge_new_baseline": True,
        }
        if module_mvid is not None:
            arguments["module_mvid"] = module_mvid
        return self._edit_call("edit_accept_live", arguments)

    def _edit_call(self, name: str, arguments: Mapping[str, Any]) -> Mapping[str, Any]:
        result = self.call_tool_json(name, arguments)
        if not isinstance(result, dict):
            raise DnSpyProtocolError(f"{name} result is not an object")
        return result

    @staticmethod
    def _first_text(result: Mapping[str, Any]) -> str | None:
        content = result.get("content")
        if not isinstance(content, list):
            return None
        for item in content:
            if isinstance(item, dict) and item.get("type") == "text" and isinstance(item.get("text"), str):
                return item["text"]
        return None

    def list_resources(self, *, cursor: str | None = None) -> Mapping[str, Any]:
        params = {"cursor": cursor} if cursor else {}
        result = self.request("resources/list", params)
        if not isinstance(result, dict):
            raise DnSpyProtocolError("resources/list result is not an object")
        return result

    def read_resource(self, uri: str) -> Mapping[str, Any]:
        result = self.request("resources/read", {"uri": uri})
        if not isinstance(result, dict):
            raise DnSpyProtocolError("resources/read result is not an object")
        return result

    def health(self) -> HttpResponse:
        return self.raw_request("GET", path="/health", include_session=False)

    def close(self) -> HttpResponse | None:
        """Delete the remote session. Safe to call more than once."""

        if not self.session_id:
            if self._http_connection is not None:
                self._http_connection.close()
                self._http_connection = None
            return None
        try:
            response = self.raw_request("DELETE")
        finally:
            self.session_id = None
            if self._http_connection is not None:
                self._http_connection.close()
                self._http_connection = None
        return response


class LegacySseClient(DnSpyClient):
    """dnSpy MCP client for the legacy HTTP+SSE transport.

    The server advertises a per-connection POST endpoint over the SSE stream. Requests are
    serialized by this client and their JSON-RPC replies are read from that same stream; callers
    never have to construct packets or parse SSE frames themselves.
    """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        self._sse: _OpenResponse | None = None
        self._message_path: str | None = None

    def _ensure_sse(self) -> None:
        if self._sse is not None:
            return
        opened = self.open_request(
            "GET",
            path="/sse",
            headers={"Accept": "text/event-stream"},
            include_session=False,
        )
        if opened.status != 200:
            response = HttpResponse(opened.status, opened.reason, opened.headers, opened.read())
            opened.close()
            raise DnSpyHttpError(response)
        self._sse = opened
        event, data = self._read_sse_event()
        if event != "endpoint" or not data:
            self.close()
            raise DnSpyProtocolError("legacy SSE stream did not publish an endpoint event")
        self._message_path = data
        parsed = urlparse(data)
        query = parsed.query.split("&") if parsed.query else []
        for item in query:
            if item.startswith("sessionId="):
                self.session_id = item.split("=", 1)[1]
                break

    def _read_sse_event(self) -> tuple[str | None, str]:
        if self._sse is None:
            raise DnSpyProtocolError("legacy SSE stream is not open")
        event: str | None = None
        data_lines: list[str] = []
        while True:
            raw = self._sse.readline()
            if raw == b"":
                raise DnSpyConnectionError("legacy SSE stream closed before the response arrived")
            line = raw.decode("utf-8", errors="strict").rstrip("\r\n")
            if line == "":
                if data_lines or event is not None:
                    return event, "\n".join(data_lines)
                continue
            if line.startswith(":"):
                continue
            if line.startswith("event:"):
                event = line[6:].lstrip()
            elif line.startswith("data:"):
                data_lines.append(line[5:].lstrip())

    def request(
        self,
        method: str,
        params: Any = _MISSING,
        *,
        request_id: str | int | None | object = _MISSING,
        include_session: bool = True,
        timeout: float | None = None,
    ) -> Any:
        del include_session
        self._ensure_sse()
        actual_id = self._next_id() if request_id is _MISSING else request_id
        message: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if actual_id is not None:
            message["id"] = actual_id
        if params is not _MISSING:
            message["params"] = params
        response = self.request_object(
            message,
            include_session=False,
            timeout=timeout,
        ) if self._message_path is None else self.raw_request(
            "POST",
            path=self._message_path,
            body=json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            include_session=False,
            timeout=timeout,
        )
        response.raise_for_status()
        if response.status != 202:
            raise DnSpyProtocolError(f"legacy SSE POST returned HTTP {response.status}", response=response)
        if actual_id is None:
            return None
        while True:
            event, data = self._read_sse_event()
            if event != "message":
                continue
            try:
                payload = json.loads(data)
            except json.JSONDecodeError as exc:
                raise DnSpyProtocolError("legacy SSE returned invalid JSON") from exc
            if not isinstance(payload, dict):
                continue
            if payload.get("id") != actual_id:
                raise DnSpyProtocolError(
                    f"response id {payload.get('id')!r} does not match request id {actual_id!r}"
                )
            error = payload.get("error")
            if isinstance(error, dict):
                raise DnSpyProtocolError(
                    str(error.get("message", "Unknown JSON-RPC error")),
                    code=error.get("code") if isinstance(error.get("code"), int) else None,
                    data=error.get("data"),
                )
            if "result" not in payload:
                raise DnSpyProtocolError("legacy SSE response has neither result nor error")
            return payload["result"]

    def close(self) -> None:
        if self._sse is not None:
            self._sse.close()
        self._sse = None
        self._message_path = None
        self.session_id = None
