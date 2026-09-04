#!/usr/bin/env python3
"""Bind P02 generators, pinned dependency, validators and reports by SHA-256."""
from __future__ import annotations
import hashlib, json
from pathlib import Path
ROOT=Path(__file__).resolve().parent
OUT=ROOT/"generated/evidence-index.json"
FILES=[
 "contract_source.py","validate_contract.py","test_validator_mutations.py",
 "tools/DnlibFacts/DnlibFacts.csproj","tools/DnlibFacts/Program.cs",
 "dependency/dnlib-4.5.0-facts.json","generated/contract-index.json",
 "generated/validation-report.json","generated/mutation-validation-report.json",
]
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def main():
    rows={name:sha(ROOT/name) for name in FILES}
    contract=json.loads((ROOT/"generated/contract-index.json").read_text(encoding="utf-8"))
    validation=json.loads((ROOT/"generated/validation-report.json").read_text(encoding="utf-8"))
    mutations=json.loads((ROOT/"generated/mutation-validation-report.json").read_text(encoding="utf-8"))
    if validation["result"]!="PASS" or mutations["result"]!="PASS":raise SystemExit("reports must PASS before indexing")
    if rows["contract_source.py"]!=contract["source_sha256"] or rows["dependency/dnlib-4.5.0-facts.json"]!=contract["dependency_sha256"]:raise SystemExit("contract identity mismatch")
    value={"format":"dnspy.p02.contract-evidence-index.v1","files_sha256":rows,
           "validation":{"result":validation["result"],"checks_passed":validation["checks_passed"],"checks_total":validation["checks_total"]},
           "mutation_validation":{"result":mutations["result"],"passed":mutations["passed"],"total":mutations["total"]}}
    OUT.write_text(json.dumps(value,ensure_ascii=False,sort_keys=True,separators=(",",":"))+"\n",encoding="utf-8")
    print(json.dumps({"path":str(OUT),"sha256":sha(OUT),**value["validation"],"mutation_tests":value["mutation_validation"]},ensure_ascii=False,indent=2))
if __name__=="__main__":main()
