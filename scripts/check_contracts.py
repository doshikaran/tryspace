# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "jsonschema==4.23.0",
#   "rfc3339-validator==0.1.4",
# ]
# ///
"""Validate Tryscape v1 contracts and shared fixtures.

Run from the repository root:

    uv run scripts/check_contracts.py

Checks performed (exit status 1 on any failure):
  1. Every schema is valid JSON Schema 2020-12.
  2. Every fixture parses under strict JSON rules (duplicate keys, NaN/Infinity and
     integers beyond +/-(2^53-1) are rejected) and matches its expected validity.
  3. Scene fixtures pass or fail a *test-only* reference semantic validator with the
     expected codes.
  4. A *test-only* exact-rational reference evaluator reproduces the independently
     derived F01-F04 / boundary expectations transcribed from demo-scope D06 and S08.2,
     and its result is invariant to entity ordering.

The reference validator/evaluator here exist only to cross-check fixtures. They are not
the product evaluator: Tryscape.Core (C#) owns that authority, and its own tests must
consume the same fixtures.
"""

from __future__ import annotations

import json
import sys
from collections.abc import Iterator
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path
from typing import Any

try:
    from jsonschema import Draft202012Validator, FormatChecker
    from referencing import Registry, Resource
except ImportError:  # pragma: no cover - guidance for bare `python` invocation
    sys.stderr.write("Missing dependencies. Run with: uv run scripts/check_contracts.py\n")
    raise SystemExit(2) from None

ROOT = Path(__file__).resolve().parent.parent
CONTRACTS = ROOT / "contracts" / "v1"
SCHEMAS = CONTRACTS / "schemas"
FIXTURES = CONTRACTS / "fixtures"
MAX_SAFE_INT = 2**53 - 1
MARGIN_MM = 25


# --------------------------------------------------------------------------- strict JSON


class StrictJsonError(ValueError):
    pass


def _reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    out: dict[str, Any] = {}
    for key, value in pairs:
        if key in out:
            raise StrictJsonError(f"duplicate key {key!r}")
        out[key] = value
    return out


def _reject_constant(name: str) -> Any:
    raise StrictJsonError(f"non-finite number {name}")


def _bounded_int(text: str) -> int:
    value = int(text)
    if abs(value) > MAX_SAFE_INT:
        raise StrictJsonError(f"integer out of range: {text}")
    return value


def load_strict(path: Path) -> Any:
    return json.loads(
        path.read_text(encoding="utf-8"),
        object_pairs_hook=_reject_duplicates,
        parse_constant=_reject_constant,
        parse_int=_bounded_int,
    )


# --------------------------------------------------------------------------- schemas


def build_registry() -> tuple[Registry, dict[str, dict[str, Any]]]:
    schemas: dict[str, dict[str, Any]] = {}
    resources = []
    for path in sorted(SCHEMAS.glob("*.schema.json")):
        schema = load_strict(path)
        Draft202012Validator.check_schema(schema)
        schemas[path.name] = schema
        resources.append((schema["$id"], Resource.from_contents(schema)))
    return Registry().with_resources(resources), schemas


def validator_for(registry: Registry, schemas: dict[str, dict[str, Any]], target: str) -> Draft202012Validator:
    file_name, _, pointer = target.partition("#")
    base = schemas[file_name]
    schema = {"$ref": base["$id"] + ("#" + pointer if pointer else "")}
    return Draft202012Validator(schema, registry=registry, format_checker=FormatChecker())


# Directory/prefix -> schema target. Every fixture JSON must be covered.
def schema_target(rel: str) -> str | None:
    if rel.startswith("scenes/"):
        return "scene_spec.schema.json"
    if rel.startswith("catalog/"):
        return "catalog_manifest.schema.json"
    if rel.startswith("proposals/"):
        return "scene_proposal.schema.json"
    if rel.startswith("results/"):
        return "evaluation_result.schema.json"
    if rel.startswith("commands/"):
        name = Path(rel).name
        return "command.schema.json#/$defs/" + ("command_result" if "_result_" in name else "command_envelope")
    if rel.startswith("api/"):
        name = Path(rel).name
        for marker, definition in (
            ("proposal_request", "scene_proposal_job_request"),
            ("job_receipt", "job_receipt"),
            ("_job_", "job"),
            ("error", "error_envelope"),
            ("session_response", "anonymous_session_response"),
        ):
            if marker in name:
                return "api.schema.json#/$defs/" + definition
    return None


def expected_valid(rel: str) -> bool:
    if rel.startswith("scenes/invalid_schema/"):
        return False
    if rel.startswith("scenes/"):
        return True  # valid + semantic-invalid fixtures are shape-valid
    name = Path(rel).name
    if name.startswith("invalid_"):
        return False
    if name.startswith(("valid_", "shape_example_")) or rel.startswith("catalog/"):
        return True
    raise ValueError(f"fixture name does not declare validity: {rel}")


# --------------------------------------------------------------------------- reference model


@dataclass(frozen=True)
class Rect:
    x0: Fraction
    z0: Fraction
    x1: Fraction
    z1: Fraction

    def overlaps_area(self, other: Rect) -> bool:
        return self.x0 < other.x1 and other.x0 < self.x1 and self.z0 < other.z1 and other.z0 < self.z1

    def contains_rect(self, other: Rect) -> bool:
        return self.x0 <= other.x0 and other.x1 <= self.x1 and self.z0 <= other.z0 and other.z1 <= self.z1

    def contains_point(self, x: Fraction, z: Fraction) -> bool:
        return self.x0 <= x <= self.x1 and self.z0 <= z <= self.z1


def half_extents(entry: dict[str, Any], yaw: int) -> tuple[Fraction, Fraction]:
    w, d = entry["footprint_width_mm"], entry["footprint_depth_mm"]
    if yaw in (90, 270):
        w, d = d, w
    return Fraction(w, 2), Fraction(d, 2)


def footprint(entity: dict[str, Any], catalog: dict[str, dict[str, Any]]) -> Rect:
    pose = entity["pose"]
    hx, hz = half_extents(catalog[entity["catalog_id"]], pose["yaw_deg"])
    return Rect(pose["x_mm"] - hx, pose["z_mm"] - hz, pose["x_mm"] + hx, pose["z_mm"] + hz)


def semantic_errors(scene: dict[str, Any], catalog: dict[str, dict[str, Any]]) -> set[str]:
    errs: set[str] = set()
    width, depth = scene["room"]["width_mm"], scene["room"]["depth_mm"]
    room = Rect(Fraction(0), Fraction(0), Fraction(width), Fraction(depth))
    ids = [e["id"] for e in scene["entities"]]
    if len(ids) != len(set(ids)):
        errs.add("duplicate_entity_id")
    route_ids = [r["id"] for r in scene["routes"]]
    if len(route_ids) != len(set(route_ids)):
        errs.add("duplicate_route_id")
    if scene["active_route_id"] not in route_ids:
        errs.add("unknown_active_route")

    known = [e for e in scene["entities"] if e["catalog_id"] in catalog]
    if len(known) != len(scene["entities"]):
        errs.add("unknown_catalog_id")
    for e in known:
        entry = catalog[e["catalog_id"]]
        if e["role"] not in entry["allowed_roles"]:
            errs.add("role_not_allowed")
        if e["pose"]["yaw_deg"] not in entry["editable_pose_constraints"]["allowed_yaw_deg"]:
            errs.add("yaw_not_allowed")
        if not room.contains_rect(footprint(e, catalog)):
            errs.add("entity_outside_room")

    payloads = [e for e in known if e["role"] == "payload"]
    blockers = [e for e in known if e["role"] == "blocker"]
    if len([e for e in scene["entities"] if e["role"] == "payload"]) != 1:
        errs.add("payload_count_invalid")
    for p in payloads:
        pf = footprint(p, catalog)
        hx, hz = half_extents(catalog[p["catalog_id"]], p["pose"]["yaw_deg"])
        permitted = Rect(hx + MARGIN_MM, hz + MARGIN_MM, width - hx - MARGIN_MM, depth - hz - MARGIN_MM)
        if not permitted.contains_point(Fraction(p["pose"]["x_mm"]), Fraction(p["pose"]["z_mm"])):
            errs.add("payload_outside_room_margin")
        for b in blockers:
            if footprint(b, catalog).overlaps_area(pf):
                errs.add("blocker_overlaps_payload")
    for i, a in enumerate(blockers):
        for b in blockers[i + 1 :]:
            if footprint(a, catalog).overlaps_area(footprint(b, catalog)):
                errs.add("blocker_overlaps_blocker")

    by_id = {e["id"]: e for e in scene["entities"]}
    destinations = set()
    for r in scene["routes"]:
        payload = by_id.get(r["payload_entity_id"])
        if payload is None or payload["role"] != "payload":
            errs.add("route_payload_invalid")
        wps = [(w["x_mm"], w["z_mm"]) for w in r["waypoints"]]
        if payload is not None and wps[0] != (payload["pose"]["x_mm"], payload["pose"]["z_mm"]):
            errs.add("route_start_mismatch")
        for (ax, az), (bx, bz) in zip(wps, wps[1:], strict=False):
            if (ax, az) == (bx, bz):
                errs.add("route_segment_zero_length")
            elif ax != bx and az != bz:
                errs.add("route_segment_not_axis_aligned")
        destinations.add(wps[-1])
    if len(destinations) > 1:
        errs.add("route_destination_mismatch")

    if scene["provenance"] == "user_measured":
        confirmed = {m["field_path"] for m in scene["measurement_sources"]}
        if not {"room.width_mm", "room.depth_mm"} <= confirmed:
            errs.add("measurement_sources_missing")
    return errs


def _segment_rect_entry(
    a: tuple[Fraction, Fraction], b: tuple[Fraction, Fraction], r: Rect
) -> Fraction | None:
    """Smallest t in [0,1] where a+(b-a)t lies in closed rect r, else None (slab method)."""
    lo, hi = Fraction(0), Fraction(1)
    for p0, p1, r0, r1 in ((a[0], b[0], r.x0, r.x1), (a[1], b[1], r.z0, r.z1)):
        d = p1 - p0
        if d == 0:
            if not (r0 <= p0 <= r1):
                return None
            continue
        t0, t1 = (r0 - p0) / d, (r1 - p0) / d
        if t0 > t1:
            t0, t1 = t1, t0
        lo, hi = max(lo, t0), min(hi, t1)
        if lo > hi:
            return None
    return lo


def _segment_room_exit(a: tuple[Fraction, Fraction], b: tuple[Fraction, Fraction], r: Rect) -> Fraction | None:
    """Largest t such that [0,t] stays inside closed rect r, if the segment leaves it; else None."""
    if r.contains_point(*b):
        return None  # convex region, start known inside
    t_exit = Fraction(1)
    for p0, p1, r0, r1 in ((a[0], b[0], r.x0, r.x1), (a[1], b[1], r.z0, r.z1)):
        d = p1 - p0
        if d > 0 and p1 > r1:
            t_exit = min(t_exit, (r1 - p0) / d)
        elif d < 0 and p1 < r0:
            t_exit = min(t_exit, (r0 - p0) / d)
    return t_exit


def reference_evaluate(scene: dict[str, Any], route_id: str, catalog: dict[str, dict[str, Any]]) -> dict[str, Any]:
    route = next(r for r in scene["routes"] if r["id"] == route_id)
    by_id = {e["id"]: e for e in scene["entities"]}
    payload = by_id[route["payload_entity_id"]]
    hx, hz = half_extents(catalog[payload["catalog_id"]], payload["pose"]["yaw_deg"])
    width, depth = scene["room"]["width_mm"], scene["room"]["depth_mm"]
    permitted = Rect(hx + MARGIN_MM, hz + MARGIN_MM, width - hx - MARGIN_MM, depth - hz - MARGIN_MM)
    blockers = [
        e
        for e in scene["entities"]
        if e["role"] == "blocker" and catalog[e["catalog_id"]]["collision_participation"] == "floor_obstruction"
    ]
    wps = [(Fraction(w["x_mm"]), Fraction(w["z_mm"])) for w in route["waypoints"]]
    length = sum(abs(b[0] - a[0]) + abs(b[1] - a[1]) for a, b in zip(wps, wps[1:], strict=False))

    for index, (a, b) in enumerate(zip(wps, wps[1:], strict=False)):
        # candidates: (fraction, rule order, entity id, rule_code); room sorts before blockers on ties.
        candidates: list[tuple[Fraction, int, str, str]] = []
        t_room = _segment_room_exit(a, b, permitted)
        if t_room is not None:
            candidates.append((t_room, 0, "", "room_boundary"))
        for blk in blockers:
            f = footprint(blk, catalog)
            expanded = Rect(f.x0 - hx - MARGIN_MM, f.z0 - hz - MARGIN_MM, f.x1 + hx + MARGIN_MM, f.z1 + hz + MARGIN_MM)
            t = _segment_rect_entry(a, b, expanded)
            if t is not None:
                candidates.append((t, 1, blk["id"], "blocker_intersection"))
        if candidates:
            t, _, entity_id, rule = min(candidates)
            cx, cz = a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t
            if index == 0 and t == 0:
                rule = "invalid_start"
            return {
                "outcome": "blocked_in_model",
                "route_length_mm": int(length),
                "first_violation": {
                    "rule_code": rule,
                    "entity_ids": [entity_id] if entity_id else [],
                    "segment_index": index,
                    "entry_fraction": {"numerator": t.numerator, "denominator": t.denominator},
                    "contact_center": {
                        "x_mm": {"numerator": cx.numerator, "denominator": cx.denominator},
                        "z_mm": {"numerator": cz.numerator, "denominator": cz.denominator},
                    },
                },
            }
    return {"outcome": "clear_in_model", "route_length_mm": int(length), "first_violation": None}


# --------------------------------------------------------------------------- runner


class Report:
    def __init__(self) -> None:
        self.failures: list[str] = []
        self.passed = 0

    def check(self, ok: bool, label: str) -> None:
        if ok:
            self.passed += 1
        else:
            self.failures.append(label)


def fixture_files() -> Iterator[tuple[str, Path]]:
    for path in sorted(FIXTURES.rglob("*.json")):
        rel = path.relative_to(FIXTURES).as_posix()
        if path.name.startswith("_") or rel.startswith("evaluation/"):
            continue  # metadata/expectation files, loaded explicitly below
        yield rel, path


def main() -> int:
    report = Report()
    registry, schemas = build_registry()
    report.check(True, "schemas")
    catalog_doc = load_strict(FIXTURES / "catalog" / "moving_day_1.0.0.json")
    catalog = {e["catalog_id"]: e for e in catalog_doc["entries"]}

    for rel, path in fixture_files():
        target = schema_target(rel)
        if target is None:
            report.check(False, f"{rel}: no schema mapping (orphan fixture)")
            continue
        want_valid = expected_valid(rel)
        try:
            doc = load_strict(path)
        except StrictJsonError as exc:
            report.check(not want_valid, f"{rel}: strict JSON rejected unexpectedly ({exc})")
            continue
        errors = sorted(validator_for(registry, schemas, target).iter_errors(doc), key=lambda e: e.json_path)
        if want_valid:
            report.check(not errors, f"{rel}: expected valid, got {[e.json_path + ': ' + e.message[:120] for e in errors[:3]]}")
        else:
            report.check(bool(errors), f"{rel}: expected schema rejection but it validated")

    # Semantic layer
    for rel in sorted(p.relative_to(FIXTURES).as_posix() for p in (FIXTURES / "scenes" / "valid").glob("*.json")):
        errs = semantic_errors(load_strict(FIXTURES / rel), catalog)
        report.check(not errs, f"{rel}: expected semantically valid, got {sorted(errs)}")
    expected_codes = load_strict(FIXTURES / "scenes" / "invalid_semantic" / "_expected_codes.json")
    listed = {c["file"] for c in expected_codes}
    present = {p.name for p in (FIXTURES / "scenes" / "invalid_semantic").glob("*.json") if not p.name.startswith("_")}
    report.check(listed == present, f"invalid_semantic expected-code list mismatch: {sorted(listed ^ present)}")
    for case in expected_codes:
        errs = semantic_errors(load_strict(FIXTURES / "scenes" / "invalid_semantic" / case["file"]), catalog)
        report.check(case["expected_code"] in errs, f"{case['file']}: expected {case['expected_code']}, got {sorted(errs)}")
    for rel in ("proposals/valid_ready.json", "commands/valid_replace_scene.json"):
        doc = load_strict(FIXTURES / rel)
        scene = doc["scene"] if "scene" in doc and doc.get("scene") else doc["payload"]["scene"]
        errs = semantic_errors(scene, catalog)
        report.check(not errs, f"{rel}: embedded scene semantically invalid {sorted(errs)}")

    # Reference evaluator vs independently derived expectations
    expectations = load_strict(FIXTURES / "evaluation" / "expectations.json")
    report.check(expectations["model_margin_mm"] == MARGIN_MM, "expectations margin mismatch")
    for case in expectations["cases"]:
        scene = load_strict(FIXTURES / case["scene"])
        got = reference_evaluate(scene, case["route_id"], catalog)
        want = {k: case[k] for k in ("outcome", "route_length_mm", "first_violation")}
        report.check(got == want, f"{case['id']}: reference evaluator {got} != expected {want}")
        reordered = dict(scene, entities=list(reversed(scene["entities"])))
        report.check(
            reference_evaluate(reordered, case["route_id"], catalog) == got,
            f"{case['id']}: result depends on entity ordering",
        )

    for failure in report.failures:
        print(f"FAIL {failure}")
    print(f"check_contracts: {report.passed} passed, {len(report.failures)} failed")
    return 1 if report.failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
