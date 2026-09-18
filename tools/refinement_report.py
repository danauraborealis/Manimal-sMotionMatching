"""Summarize solver interventions without treating them as a naturalness score.

Accepts a puppet capture or fleet JSONL. Use placement_diagnostics.py for geometry
and arm_sync_report.py for commanded/observed arm phase; this report exposes the
new solver's coverage, residual target error, and fallback interventions.
Clearance limits are applied only when capture metadata identifies a supported
policy; captures without usable policy metadata remain explicitly unassessed.
"""
import argparse
import json
import math
from collections import Counter, defaultdict
from pathlib import Path


CLEARANCE_POLICY_FIELD = "clearancePolicy"
CURRENT_CLEARANCE_POLICY_ID = "adaptive-baseline-v2"
LEGACY_CLEARANCE_POLICY_ID = "legacy-cap-006-baseline-050"
CLEARANCE_POLICIES = {
    LEGACY_CLEARANCE_POLICY_ID: {
        "maximum_required_clearance_m": 0.06,
        "required_clearance_baseline_fraction": 0.5,
    },
    CURRENT_CLEARANCE_POLICY_ID: {
        "maximum_required_clearance_m": 0.11,
        "required_clearance_baseline_fraction": 0.75,
    },
}
FLEET_CLEARANCE_TOLERANCE_M = 0.001
PUPPET_CLEARANCE_TOLERANCE_M = 1e-5
_MISSING = object()


def distribution(values):
    values = sorted(v for v in values if isinstance(v, (int, float)) and math.isfinite(v))
    return {"count": len(values), **{
        name: values[round((len(values) - 1) * fraction)] if values else None
        for name, fraction in (("median", .5), ("p90", .9), ("p99", .99), ("maximum", 1))}}


def _finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _unknown_clearance_policy(reason):
    return {
        "id": "unknown",
        "applicable": False,
        "reason": reason,
        "origin": "unknown",
    }


def _policy_parameters(metadata):
    maximum = metadata.get("maximumRequiredClearanceMeters")
    fraction = metadata.get("requiredClearanceBaselineFraction")
    if not _finite_number(maximum) or not _finite_number(fraction):
        return None
    if maximum <= 0 or fraction <= 0 or fraction > 1:
        return None
    return float(maximum), float(fraction)


def _known_clearance_policy(policy_id, origin):
    parameters = CLEARANCE_POLICIES[policy_id]
    return {
        "id": policy_id,
        "applicable": True,
        "maximum_required_clearance_m": parameters["maximum_required_clearance_m"],
        "required_clearance_baseline_fraction": parameters["required_clearance_baseline_fraction"],
        "reason": None,
        "origin": origin,
    }


def _resolve_clearance_policy(metadata):
    if metadata is _MISSING:
        return _unknown_clearance_policy("metadata_missing")

    if isinstance(metadata, str):
        if metadata in CLEARANCE_POLICIES:
            return _known_clearance_policy(metadata, "recognized_policy_id")
        return _unknown_clearance_policy("unknown_policy_id")

    if not isinstance(metadata, dict):
        return _unknown_clearance_policy("invalid_metadata")

    policy_id = metadata.get("id")
    if policy_id is not None and (not isinstance(policy_id, str) or not policy_id):
        return _unknown_clearance_policy("invalid_policy_id")

    has_parameters = ("maximumRequiredClearanceMeters" in metadata
                      or "requiredClearanceBaselineFraction" in metadata)
    if isinstance(policy_id, str) and policy_id in CLEARANCE_POLICIES:
        if has_parameters:
            supplied = _policy_parameters(metadata)
            expected = CLEARANCE_POLICIES[policy_id]
            if supplied is None:
                return _unknown_clearance_policy("invalid_policy_parameters")
            if (not math.isclose(supplied[0], expected["maximum_required_clearance_m"], rel_tol=0, abs_tol=1e-9)
                    or not math.isclose(supplied[1], expected["required_clearance_baseline_fraction"], rel_tol=0, abs_tol=1e-9)):
                return _unknown_clearance_policy("policy_parameters_mismatch")
        return _known_clearance_policy(policy_id, "recognized_policy_id")

    supplied = _policy_parameters(metadata)
    if supplied is not None:
        return {
            "id": policy_id or "explicit-parameters",
            "applicable": True,
            "maximum_required_clearance_m": supplied[0],
            "required_clearance_baseline_fraction": supplied[1],
            "reason": None,
            "origin": "explicit_parameters",
        }
    if has_parameters:
        return _unknown_clearance_policy("invalid_policy_parameters")
    if policy_id is not None:
        return _unknown_clearance_policy("unknown_policy_id")
    return _unknown_clearance_policy("policy_metadata_incomplete")


def _fleet_rows(stream):
    """Yield sample rows with their row-level or capture-level policy metadata."""
    capture_policy = _MISSING
    for line in stream:
        if not line.strip():
            continue
        row = json.loads(line)
        if not isinstance(row, dict):
            continue
        if row.get("k") == "meta":
            capture_policy = row.get(CLEARANCE_POLICY_FIELD, _MISSING)
            continue
        policy = row.get(CLEARANCE_POLICY_FIELD, capture_policy)
        yield row, policy


def _new_policy_stats():
    return {
        "feet_by_policy": Counter(),
        "pairs_by_policy": Counter(),
        "assessed_pairs_by_policy": Counter(),
        "unknown_feet_by_reason": Counter(),
        "unknown_pairs_by_reason": Counter(),
        "details": {},
    }


def _policy_label(policy):
    if not policy["applicable"]:
        return "unknown"
    if policy["id"] == "unknown":
        return "custom:unknown"
    return policy["id"]


def _record_policy_coverage(stats, policy, has_pair):
    label = _policy_label(policy)
    stats["feet_by_policy"][label] += 1
    if policy["applicable"]:
        stats["details"].setdefault(label, {
            "origin": policy["origin"],
            "maximum_required_clearance_m": policy["maximum_required_clearance_m"],
            "required_clearance_baseline_fraction": policy["required_clearance_baseline_fraction"],
        })
    else:
        stats["unknown_feet_by_reason"][policy["reason"]] += 1

    if has_pair:
        stats["pairs_by_policy"][label] += 1
        if policy["applicable"]:
            stats["assessed_pairs_by_policy"][label] += 1
        else:
            stats["unknown_pairs_by_reason"][policy["reason"]] += 1


def _clearance_policy_coverage(stats, tolerance):
    labels = (set(stats["feet_by_policy"]) | set(stats["pairs_by_policy"])
              | set(stats["assessed_pairs_by_policy"]))
    by_policy = {}
    for label in sorted(labels):
        entry = {
            "eligible_feet": stats["feet_by_policy"].get(label, 0),
            "measured_clearance_pairs": stats["pairs_by_policy"].get(label, 0),
            "assessed_clearance_pairs": stats["assessed_pairs_by_policy"].get(label, 0),
        }
        if label in stats["details"]:
            entry.update(stats["details"][label])
        else:
            entry["origin"] = "unknown"
        by_policy[label] = entry

    eligible_feet = sum(stats["feet_by_policy"].values())
    feet_with_unknown_policy = stats["feet_by_policy"].get("unknown", 0)
    measured_pairs = sum(stats["pairs_by_policy"].values())
    assessed_pairs = sum(stats["assessed_pairs_by_policy"].values())
    return {
        "eligible_feet": eligible_feet,
        "feet_with_policy": eligible_feet - feet_with_unknown_policy,
        "feet_with_unknown_policy": feet_with_unknown_policy,
        "measured_clearance_pairs": measured_pairs,
        "assessed_clearance_pairs": assessed_pairs,
        "unassessed_clearance_pairs": measured_pairs - assessed_pairs,
        "assessed_pair_coverage": assessed_pairs / measured_pairs if measured_pairs else None,
        "unknown_policy_feet_by_reason": dict(sorted(stats["unknown_feet_by_reason"].items())),
        "unknown_policy_pairs_by_reason": dict(sorted(stats["unknown_pairs_by_reason"].items())),
        "by_policy": by_policy,
        "tolerance_m": tolerance,
    }


def _new_group():
    return {
        "feet": 0,
        "flags": Counter(),
        "clips": Counter(),
        "metrics": defaultdict(list),
        "_clearance_policy_stats": _new_policy_stats(),
    }


def report(path):
    groups = defaultdict(_new_group)
    overall_policy_stats = _new_policy_stats()
    fleet = path.suffix.lower() == ".jsonl"
    tolerance = FLEET_CLEARANCE_TOLERANCE_M if fleet else PUPPET_CLEARANCE_TOLERANCE_M

    with path.open(encoding="utf-8-sig") as stream:
        if fleet:
            rows = _fleet_rows(stream)
        else:
            document = json.load(stream)
            capture_policy = document.get(CLEARANCE_POLICY_FIELD, _MISSING)
            rows = (
                (row, row.get(CLEARANCE_POLICY_FIELD, capture_policy) if isinstance(row, dict) else capture_policy)
                for row in document.get("Samples", [])
            )

        for row, policy_metadata in rows:
            if not isinstance(row, dict):
                continue
            if fleet:
                if row.get("k") != "s" or not row.get("placed") or row.get("vis") == 0 or row.get("simp") or row.get("sus"):
                    continue
                probes = [row.get(side, {}) for side in ("L", "R")]
            else:
                if row.get("Stage") != "after_lock" or row.get("UsedSimplifiedSkeleton") or (row.get("HasIsVisible") and not row.get("IsVisible")):
                    continue
                probes = [(row.get("Pose") or {}).get("Placer" + side, {}) for side in ("L", "R")]

            policy = _resolve_clearance_policy(policy_metadata)
            for probe in probes:
                if not isinstance(probe, dict):
                    continue
                key = lambda full, short: probe.get(short if fleet else full)
                if not fleet and not probe.get("Applied", bool(key("Active", "on") or key("Fading", "fade") or probe.get("Authority", 0) > 0)):
                    continue
                enabled = row.get("refinement") if fleet else key("Refinement", "refinement")
                group = groups["on" if enabled else "off" if enabled is not None else "unrecorded"]
                group["feet"] += 1

                before = key("ClearanceBefore", "clearance0")
                after = key("ClearanceAfter", "clearance")
                has_clearance_pair = _finite_number(before) and _finite_number(after)
                _record_policy_coverage(group["_clearance_policy_stats"], policy, has_clearance_pair)
                _record_policy_coverage(overall_policy_stats, policy, has_clearance_pair)

                for label, full, short in (("ground_normal_available", "GroundNormalAvailable", "normal"),
                                            ("fading", "Fading", "fade"), ("locked", "Locked", "lk"),
                                            ("swing_path_planned", "SwingPathPlanned", "swingPlanned"),
                                            ("swing_path_resolved", "SwingPathResolved", "swingResolved"),
                                            ("stop_corrective_step", "StopCorrectionStep", "stopStep"),
                                            ("stop_settlement_failed", "StopSettlementFailed", "stopFailed"),
                                            ("stop_settlement_ready", "StopSettlementReady", "stopReady"),
                                            ("swing_endpoints_blocked", "SwingEndpointsBlocked", "swingEndpointsBlocked")):
                    if key(full, short):
                        group["flags"][label] += 1
                for label, full, short in (("sole_target_error_m", "SoleTargetError", "soleError"),
                                           ("ankle_limit_degrees", "AnkleLimitDegrees", "ankleLimit"),
                                           ("midpoint_correction_m", "MidpointCorrection", "midCorrection"),
                                           ("swing_avoidance_m", "SwingAvoidance", "swingAvoidance"),
                                           ("clearance_fraction", "ClearanceFraction", "clearanceFraction")):
                    value = key(full, short)
                    if value is not None:
                        group["metrics"][label].append(value)
                fraction = key("ClearanceFraction", "clearanceFraction")
                if fraction is not None and fraction < 1:
                    group["flags"]["clearance_limited"] += 1

                if has_clearance_pair and policy["applicable"]:
                    required = (min(policy["maximum_required_clearance_m"],
                                    float(before) * policy["required_clearance_baseline_fraction"])
                                if before > 0 else 0.0)
                    measured_with_tolerance = float(after) + tolerance
                    if (measured_with_tolerance < required
                            and not math.isclose(measured_with_tolerance, required, rel_tol=0, abs_tol=1e-12)):
                        group["flags"]["clearance_floor_violation"] += 1

                if key("StridePartner", "stridePartner"):
                    group["flags"]["blended_stride"] += 1
                source = key("StrideSource", "strideSource")
                if source:
                    group["clips"][source] += 1

    for group in groups.values():
        group["metrics"] = {name: distribution(values) for name, values in group["metrics"].items()}
        group["clearance_policy_coverage"] = _clearance_policy_coverage(
            group.pop("_clearance_policy_stats"), tolerance)
        group["flags"] = dict(group["flags"])
        group["clips"] = dict(group["clips"])

    return {
        "source": str(path),
        "interpretation": "Eligible placed foot samples; versioned clearance limits and target residuals, not mesh collisions or a naturalness score. Unknown clearance policies are not assessed.",
        "clearance_floor_tolerance_m": tolerance,
        "clearance_policy_coverage": _clearance_policy_coverage(overall_policy_stats, tolerance),
        "groups": dict(groups),
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    text = json.dumps(report(args.capture), indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
    else:
        print(text)
