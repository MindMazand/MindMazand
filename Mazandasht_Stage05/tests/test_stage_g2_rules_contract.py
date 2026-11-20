# -*- coding: utf-8 -*-
"""G2 acceptance tests: the rules contract must agree with the real schema."""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DB = ROOT / "app" / "Mazandasht.Desktop" / "Database"
CONTRACT = ROOT / "docs" / "12_Rules_Contract.json"


def _schema_sql() -> str:
    parts = [DB.joinpath("schema_v1.sql").read_text(encoding="utf-8")]
    for migration in sorted(DB.glob("migration_*.sql")):
        parts.append(migration.read_text(encoding="utf-8"))
    return "\n".join(parts)


def _seed_sql() -> str:
    return (DB / "system_seed.sql").read_text(encoding="utf-8")


def test_contract_is_wellformed():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    assert contract["schema"] == "mazandasht.rules/v1"
    assert contract["version"] == "1.0"
    for key in ("documentLifecycle", "money", "inventory", "weighing", "sorting",
                "basket", "invoices", "cheque", "settlement", "partnership",
                "accounting", "tax", "roles", "reports"):
        assert key in contract, key


def test_document_types_used_by_journal_rules_exist_in_schema():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    schema = _schema_sql()
    match = re.search(r"document_type\s+TEXT NOT NULL CHECK \(document_type IN \(([^)]*)\)\)", schema)
    assert match, "documents.document_type CHECK not found"
    allowed = {v.strip().strip("'") for v in match.group(1).split(",")}
    used = {rule["documentType"] for rule in contract["accounting"]["documentJournalRules"]}
    assert used <= allowed, f"journal rules reference unknown document types: {used - allowed}"


def test_lifecycle_transitions_match_schema_trigger():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    schema = _schema_sql()
    trigger = re.search(
        r"trg_document_status_transition.*?(?=CREATE TRIGGER|\Z)", schema, re.S)
    assert trigger, "status transition trigger not found"
    body = trigger.group(0)

    allowed_pairs = set()
    for old, new in re.findall(r"OLD\.status = '(\w+)' AND NEW\.status IN \(([^)]*)\)", body):
        for target in re.findall(r"'(\w+)'", new):
            allowed_pairs.add((old, target))
    for old, new in re.findall(r"OLD\.status = '(\w+)' AND NEW\.status = '(\w+)'", body):
        allowed_pairs.add((old, new))

    for transition in contract["documentLifecycle"]["transitions"]:
        pair = (transition["from"], transition["to"])
        assert pair in allowed_pairs, f"contract transition {pair} is not allowed by the schema trigger"
    assert ("APPROVED", "VOIDED") in allowed_pairs


def test_journal_rules_match_schema_invariants():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    journal = contract["accounting"]["journal"]
    assert journal["minLines"] >= 2
    assert journal["mustBalance"] is True
    assert journal["postRequiresApprovedDocument"] is True
    schema = _schema_sql()
    assert "Journal requires at least two lines" in schema
    assert "Journal is not balanced" in schema
    assert "Journal can only be posted for an approved document" in schema
    assert "Posted journal lines are immutable" in schema


def test_money_and_weight_storage_matches_schema_columns():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    assert contract["money"]["storage"] == "integer_rial"
    assert contract["money"]["weightStorage"] == "integer_gram"
    assert contract["money"]["kgToGramFactor"] == 1000
    schema = _schema_sql()
    assert re.search(r"amount_irr\s+INTEGER", schema)
    assert re.search(r"gross_weight_g\s+INTEGER", schema)
    assert re.search(r"quantity_delta_g\s+INTEGER", schema)


def test_fifo_columns_exist():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    assert contract["inventory"]["allocationMethod"] == "FIFO"
    schema = _schema_sql()
    for column in contract["inventory"]["fifoOrderBy"]:
        column = column.split(".")[-1]
        assert re.search(rf"\b{column}\b", schema), f"FIFO column {column} missing from schema"


def test_roles_and_permissions_exist_in_seed():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    seed = _seed_sql()
    seeded_roles = set(re.findall(r"'role-([a-z]+)'", seed))
    for role in contract["roles"]["systemRoles"]:
        assert role.lower() in seeded_roles, f"role {role} not seeded"

    seeded_permissions = set(re.findall(r"\('([a-z]+\.[a-z_]+)'", seed))
    for role, grants in contract["roles"].items():
        if role in ("systemRoles", "enforcedByRoleCodeOnly") or not isinstance(grants, list):
            continue
        for permission in grants:
            assert permission in seeded_permissions, f"permission {permission} not seeded"


def test_tax_rule_flags_owner_approval():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    assert contract["tax"]["ownerApprovalRequired"] is True
    assert contract["tax"]["appliesTo"] == "SALE"
