# Tryscape contracts v1

Shared, versioned wire contracts extracted from `docs/technical-spec.md` S06, S07, S09, S10.3 and S13. C# (`Tryscape.Core`/`Infrastructure`) and Python (`services/api`) must consume these files; neither side may define a second wire shape.

```
schemas/   JSON Schema 2020-12 (shape only)
fixtures/  shared valid/invalid examples and independently derived expectations
```

Check with `uv run scripts/check_contracts.py` from the repository root.

## Layers

1. **Strict JSON** — duplicate keys, `NaN`/`Infinity` and integers beyond ±(2^53−1) are rejected before schema validation. Standard JSON parsers (including Pydantic's and Newtonsoft's defaults) accept duplicate keys, so each runtime must enforce this explicitly at its boundary.
2. **Schema** — field names, types, bounds, enums, `additionalProperties: false`.
3. **Semantic** — catalog, containment, overlap and route rules. Owned by `Tryscape.Core`. `scripts/check_contracts.py` has a *test-only* reference validator so fixture codes stay honest; it is not product authority.

## Fixture conventions

| Location | Meaning |
| --- | --- |
| `scenes/valid/` | Shape-valid and semantically valid SceneSpecs. `f01`–`f04` are demo-scope D06 fixtures; `boundary_*` cover S08.2 tangency/wall rules. |
| `scenes/invalid_schema/` | Must fail strict JSON or schema validation. |
| `scenes/invalid_semantic/` | Shape-valid but must be rejected by semantic validation with the code in `_expected_codes.json` (other codes may also be reported). |
| `evaluation/expectations.json` | Expected outcomes transcribed from demo-scope D06 and hand-derived boundary cases, each with a written derivation. Never regenerate these from an evaluator's output. |
| `*/valid_*`, `*/invalid_*` | Expected validity by filename prefix. |
| `results/shape_example_*` | Shape examples only. Their `scene_hash` is a placeholder (`000…`), **not** a canonical hash; canonical hash fixtures are added once the S09.1 serializer exists. |

## Clarifications made during extraction (2026-11-04)

These resolve gaps in the spec without changing its meaning. Revisit through the S24 change process if wrong.

| Topic | Decision | Reason |
| --- | --- | --- |
| UUID case | Lowercase only. | Canonical scene bytes (S09.1) must not vary by case. |
| Coordinates | Integer 0–8000 mm. | R0 rooms are ≤ 8000 mm; rejects huge integers at the shape layer. |
| Assumption `source` | `template`, `scenario_model`, `user_request`, `example_default`. | S06.2 names the field but not its enum; S06.4 uses the first two. |
| `measurement_sources.field_path` | `room.width_mm`, `room.depth_mm` only. | Only room dimensions are user-confirmable in R0. |
| `EvaluationResult.validation_errors` | Added; required; non-empty only for `invalid_input`, and `scene_hash` is then `null`. | S09 requires invalid-input results to reference validation errors instead of a fabricated hash. |
| Violation `entry_fraction` | For blockers: first parameter where the centre touches the closed expanded rectangle. For walls: last parameter still inside the permitted rectangle. | Matches S08.2 (tangent blocker = blocked; exact wall margin = accepted). |
| Tie at equal fraction | `room_boundary` sorts before blockers; blockers by ID ordinal. | S08.2 defines blocker tie-breaks only. |
| Violation at segment 0, fraction 0 | `rule_code = invalid_start`. | S09 lists `invalid_start`; S08.2 says initial overlap prevents evaluation. |
| 50 mm snapping | A `move_entity` command rule, not a SceneSpec validity rule. | With a 25 mm margin, exact tangency is unreachable on a 50 mm grid; boundary fixtures must stay expressible. |
| Blocker/payload overlap | Positive-area overlap is invalid; edge contact is allowed (but will evaluate as blocked/invalid_start). | S07.1 "cannot overlap payload start". |
| Exactly one payload | Semantic rule `payload_count_invalid`. | S02.2: one carried payload in R0. |
| Jobs scope | R0 rows are anonymous-session scoped; the owner/session CHECK constraint arrives with the R1 expand migration. | S14.2 forbids R0 dependence on account tables. |

## Versioning

`schema_version` is exactly `1.0.0`. Breaking changes require a new major directory (`contracts/v2/`), consumer updates and migration fixtures in the same review (S20).
