"""Pydantic models must accept/reject exactly what the shared contracts/v1 schemas accept/reject on shared fixtures.

Strict JSON (duplicate keys etc.) is applied first, as at the service boundary.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from jsonschema import Draft202012Validator
from pydantic import BaseModel, ValidationError
from referencing import Registry, Resource

from tryscape_api import strict_json
from tryscape_api.contracts.models import SceneProposal, SceneProposalJobRequest, SceneSpec

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts" / "v1"
FIXTURES = CONTRACTS / "fixtures"


def _registry() -> tuple[Registry, dict[str, Any]]:
    schemas = {p.name: json.loads(p.read_text()) for p in (CONTRACTS / "schemas").glob("*.schema.json")}
    return Registry().with_resources((s["$id"], Resource.from_contents(s)) for s in schemas.values()), schemas


REGISTRY, SCHEMAS = _registry()


def schema_accepts(target: str, doc: Any) -> bool:
    file_name, _, pointer = target.partition("#")
    ref = SCHEMAS[file_name]["$id"] + ("#" + pointer if pointer else "")
    return Draft202012Validator({"$ref": ref}, registry=REGISTRY).is_valid(doc)


def model_accepts(model: type[BaseModel], raw: bytes) -> bool:
    try:
        model.model_validate(strict_json.loads(raw))
    except (strict_json.StrictJsonError, ValidationError):
        return False
    return True


CASES: list[tuple[str, type[BaseModel], str]] = [
    *[
        (str(p.relative_to(FIXTURES)), SceneSpec, "scene_spec.schema.json")
        for p in sorted(FIXTURES.glob("scenes/*/*.json"))
        if not p.name.startswith("_")
    ],
    *[
        (str(p.relative_to(FIXTURES)), SceneProposal, "scene_proposal.schema.json")
        for p in sorted(FIXTURES.glob("proposals/*.json"))
    ],
    *[
        (str(p.relative_to(FIXTURES)), SceneProposalJobRequest, "api.schema.json#/$defs/scene_proposal_job_request")
        for p in sorted(FIXTURES.glob("api/*proposal_request*.json"))
    ],
]


def test_fixture_inventory_is_nonempty() -> None:
    kinds = {model for _, model, _ in CASES}
    assert kinds == {SceneSpec, SceneProposal, SceneProposalJobRequest}
    assert len(CASES) > 50


@pytest.mark.parametrize(("rel", "model", "target"), CASES, ids=[c[0] for c in CASES])
def test_model_matches_schema(rel: str, model: type[BaseModel], target: str) -> None:
    raw = (FIXTURES / rel).read_bytes()
    try:
        schema_ok = schema_accepts(target, strict_json.loads(raw))
    except strict_json.StrictJsonError:
        schema_ok = False
    assert model_accepts(model, raw) == schema_ok
    expected_valid = "invalid_schema" not in rel and not Path(rel).name.startswith("invalid_")
    assert schema_ok == expected_valid


def test_strict_json_limits() -> None:
    for bad in (b'{"a":1,"a":1}', b"[Infinity]", b"[-NaN]", b"[9007199254740992]", b"{"):
        with pytest.raises(strict_json.StrictJsonError):
            strict_json.loads(bad)
    assert strict_json.loads(b"[9007199254740991, -9007199254740991]") == [9007199254740991, -9007199254740991]
