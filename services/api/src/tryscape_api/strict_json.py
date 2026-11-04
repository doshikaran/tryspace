"""Strict JSON decoding for untrusted bodies (technical-spec S06): duplicate keys, non-finite numbers and
integers beyond +/-(2^53-1) are rejected. Default parsers silently accept these."""

from __future__ import annotations

import json
from typing import Any

MAX_SAFE_INT = 2**53 - 1


class StrictJsonError(ValueError):
    """Raised for input that violates strict JSON rules. Message is safe to log (no content)."""


def _pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    out: dict[str, Any] = {}
    for key, value in pairs:
        if key in out:
            raise StrictJsonError("duplicate_key")
        out[key] = value
    return out


def _constant(_: str) -> Any:
    raise StrictJsonError("non_finite_number")


def _int(text: str) -> int:
    if len(text) > 17:
        raise StrictJsonError("integer_out_of_range")
    value = int(text)
    if abs(value) > MAX_SAFE_INT:
        raise StrictJsonError("integer_out_of_range")
    return value


def loads(data: bytes | str) -> Any:
    try:
        return json.loads(data, object_pairs_hook=_pairs, parse_constant=_constant, parse_int=_int)
    except StrictJsonError:
        raise
    except (ValueError, UnicodeDecodeError) as exc:
        raise StrictJsonError("malformed_json") from exc
