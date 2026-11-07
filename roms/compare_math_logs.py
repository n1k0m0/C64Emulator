"""Compare two Open-ROM math audit logs against the same reference cases.

The audit logs contain only results and status, never reference ROM bytes.
"""

# SPDX-License-Identifier: Apache-2.0

from __future__ import annotations

import argparse
import re
from dataclasses import dataclass
from pathlib import Path


CASE = re.compile(
    r"^  (?P<expression>.*?) \| open=(?P<open>[0-9A-F-]+) "
    r"reference=(?P<reference>[0-9A-F-]+) "
    r"exact=(?P<exact>True|False) status=(?P<status>\S+)"
)
ULP = re.compile(r"\bulp=(-?\d+)\b")


@dataclass(frozen=True)
class Result:
    expression: str
    open_bytes: str
    reference_bytes: str
    exact: bool
    status: str
    ulp: int | None


def read_results(path: Path) -> list[Result]:
    results: list[Result] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        match = CASE.match(line)
        if match is None:
            continue
        distance = ULP.search(line)
        results.append(
            Result(
                match["expression"],
                match["open"],
                match["reference"],
                match["exact"] == "True",
                match["status"],
                int(distance[1]) if distance else None,
            )
        )
    if not results:
        raise ValueError(f"No five-byte audit cases found in {path}")
    return results


def distance(result: Result) -> int | None:
    if result.status != "VALUE/VALUE":
        return None
    if result.exact:
        return 0
    return abs(result.ulp) if result.ulp is not None else None


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    args = parser.parse_args()
    baseline = read_results(args.baseline)
    candidate = read_results(args.candidate)
    if len(baseline) != len(candidate):
        raise SystemExit(f"Case count differs: {len(baseline)} vs {len(candidate)}")
    for number, (old, new) in enumerate(zip(baseline, candidate), 1):
        if (old.expression, old.reference_bytes,
            old.status.rsplit("/", 1)[-1]) != (
            new.expression,
            new.reference_bytes,
            new.status.rsplit("/", 1)[-1],
        ):
            raise SystemExit(f"Case/reference mismatch at #{number}: {old.expression!r}")

    old_exact = sum(case.exact for case in baseline)
    new_exact = sum(case.exact for case in candidate)
    gains = [new.expression for old, new in zip(baseline, candidate)
             if not old.exact and new.exact]
    losses = [new.expression for old, new in zip(baseline, candidate)
              if old.exact and not new.exact]
    status_changes = [new.expression for old, new in zip(baseline, candidate)
                      if old.status != new.status]
    comparable = [(distance(old), distance(new))
                  for old, new in zip(baseline, candidate)]
    comparable = [(old, new) for old, new in comparable
                  if old is not None and new is not None]
    excluded_values = [
        (old, new) for old, new in zip(baseline, candidate)
        if old.status == "VALUE/VALUE" and new.status == "VALUE/VALUE"
        and (distance(old) is None or distance(new) is None)
    ]
    changed_excluded = [(old, new) for old, new in excluded_values
                        if old.open_bytes != new.open_bytes]

    print(f"cases={len(baseline)} exact={old_exact}->{new_exact} "
          f"gains={len(gains)} losses={len(losses)} "
          f"status_changes={len(status_changes)}")
    print(f"comparable_ulp={len(comparable)} "
          f"absolute_ulp_sum={sum(old for old, _ in comparable)}"
          f"->{sum(new for _, new in comparable)}")
    print(f"value_pairs_without_ulp={len(excluded_values)} "
          f"changed={len(changed_excluded)}")
    if changed_excluded:
        print("changed values outside same-exponent ULP comparison:")
        for old, new in changed_excluded:
            print(f"  {new.expression}: baseline={old.open_bytes} "
                  f"candidate={new.open_bytes} reference={new.reference_bytes}")
    for title, expressions in (("gained exact", gains),
                               ("lost exact", losses),
                               ("changed status", status_changes)):
        if expressions:
            print(f"{title}:")
            for expression in expressions:
                print(f"  {expression}")


if __name__ == "__main__":
    main()
