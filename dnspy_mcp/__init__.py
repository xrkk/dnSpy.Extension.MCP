"""Python client for dnSpy.Extension.MCP."""

from .client import (
    DEFAULT_PROTOCOL_VERSION,
    DnSpyClient,
    DnSpyConnectionError,
    DnSpyError,
    DnSpyHttpError,
    DnSpyProtocolError,
    HttpResponse,
    ToolCallError,
)

__all__ = [
    "DEFAULT_PROTOCOL_VERSION",
    "DnSpyClient",
    "DnSpyConnectionError",
    "DnSpyError",
    "DnSpyHttpError",
    "DnSpyProtocolError",
    "HttpResponse",
    "ToolCallError",
]

__version__ = "0.1.0"
