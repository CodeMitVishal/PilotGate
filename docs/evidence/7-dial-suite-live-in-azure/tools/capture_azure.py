"""Captures the deployed Azure state (read-only az queries) and the logs in Log Analytics, as evidence.

Usage: python capture_azure.py <resource-group> <prefix> <out-dir> [state|logs|final]
  state  container apps, environment, registry, identity roles  -> azure.json
  logs   guard events and ClaimsAssist audit lines from the workspace -> logs.json
  final  what is left in the resource group after -Destroy -> final.json
No subscription ids, object ids or keys are written: resource names and settings only.
"""

from __future__ import annotations

import json
import subprocess
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

RG, PREFIX, OUT, WHAT = sys.argv[1], sys.argv[2], Path(sys.argv[3]), sys.argv[4]


# The Azure CLI's own interpreter, without cmd.exe in between: the KQL and JMESPath arguments contain | > and quotes.
AZ = [r"C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe", "-IBm", "azure.cli"]


def az(*args: str):
    out = subprocess.run([*AZ, *args, "-o", "json"], capture_output=True, text=True, encoding="utf-8")
    if out.returncode != 0:
        raise SystemExit(f"az {' '.join(args[:3])} failed: {out.stderr.strip()[:400]}")
    return json.loads(out.stdout or "null")


def short_scope(scope: str) -> str:
    parts = scope.split("/")
    return f"{parts[-2]}/{parts[-1]}" if len(parts) >= 2 else scope


now = datetime.now(timezone.utc).isoformat(timespec="seconds")

if WHAT == "state":
    apps = az("containerapp", "list", "-g", RG, "--query",
              f"[?starts_with(name, '{PREFIX}-')].{{name:name, external:properties.configuration.ingress.external, "
              "allowInsecure:properties.configuration.ingress.allowInsecure, fqdn:properties.configuration.ingress.fqdn, "
              "targetPort:properties.configuration.ingress.targetPort, minReplicas:properties.template.scale.minReplicas, "
              "maxReplicas:properties.template.scale.maxReplicas, images:properties.template.containers[].image, "
              "cpu:properties.template.containers[].resources.cpu, memory:properties.template.containers[].resources.memory, "
              "running:properties.runningStatus, provisioning:properties.provisioningState, "
              "secrets:properties.configuration.secrets[].name, identity:identity.type}")
    for app in apps:
        app["replicas"] = len(az("containerapp", "replica", "list", "-g", RG, "-n", app["name"]) or [])
        app["images"] = [i.split("/", 1)[1] if "/" in i else i for i in app["images"]]  # drop the registry host
    envs = az("containerapp", "env", "list", "-g", RG, "--query",
              f"[?contains(name, '-{PREFIX}-')].{{name:name, location:location, defaultDomain:properties.defaultDomain, "
              "peerEncryption:properties.peerTrafficConfiguration.encryption.enabled, "
              "workloadProfiles:properties.workloadProfiles[].workloadProfileType, logs:properties.appLogsConfiguration.destination}")
    registries = az("acr", "list", "-g", RG, "--query",
                    f"[?starts_with(name, 'acr{PREFIX}')].{{name:name, sku:sku.name, adminUserEnabled:adminUserEnabled, "
                    "anonymousPull:anonymousPullEnabled}")
    for reg in registries:
        reg["repositories"] = az("acr", "repository", "list", "-n", reg["name"])
    identity = az("identity", "show", "-g", RG, "-n", f"id-{PREFIX}-apps", "--query", "{name:name, principalId:principalId}")
    roles = az("role", "assignment", "list", "--all", "--assignee", identity["principalId"], "--query",
               "[].{role:roleDefinitionName, scope:scope}")
    workspaces = az("monitor", "log-analytics", "workspace", "list", "-g", RG, "--query",
                    f"[?contains(name, '-{PREFIX}-')].{{name:name, retentionInDays:retentionInDays, "
                    "localAuthDisabled:features.disableLocalAuth, sku:sku.name}")
    data = {"captured_at": now, "resource_group": RG, "apps": apps, "environments": envs, "registries": registries,
            "identity": {"name": identity["name"],
                         "roles": [{"role": r["role"], "scope": short_scope(r["scope"])} for r in roles]},
            "workspaces": workspaces}
    (OUT / "azure.json").write_text(json.dumps(data, indent=2), encoding="utf-8")
    print(f"{len(apps)} apps, {len(envs)} environment(s), {len(registries)} registry, {len(roles)} role assignment(s)")

elif WHAT == "logs":
    ws = az("monitor", "log-analytics", "workspace", "list", "-g", RG, "--query",
            f"[?contains(name, '-{PREFIX}-')].customerId")[0]
    query = (
        "union isfuzzy=true ContainerAppConsoleLogs, ContainerAppConsoleLogs_CL "
        "| where TimeGenerated > ago(3h) "
        "| extend app = coalesce(column_ifexists('ContainerAppName', ''), column_ifexists('ContainerAppName_s', '')), "
        "line = coalesce(column_ifexists('Log', ''), column_ifexists('Log_s', '')) "
        f"| where app in ('{PREFIX}-guard', '{PREFIX}-claimsassist') "
        "| where line has 'dial-guard.event.v1' or line has 'conversationId' "
        "| project TimeGenerated, app, line | order by TimeGenerated asc")
    rows = az("monitor", "log-analytics", "query", "-w", ws, "--analytics-query", query) or []
    events, audit = [], []
    for row in rows:
        line = row.get("line", "")
        start = line.find("{")
        try:
            record = json.loads(line[start:]) if start >= 0 else None
        except ValueError:
            record = None
        if not record:
            continue
        if record.get("schema") == "dial-guard.event.v1":
            events.append(record)
        elif "conversationId" in record:
            audit.append({k: record.get(k) for k in ("timestamp", "user", "status", "retrievedChunkIds", "toolCalls",
                                                     "inputTokens", "outputTokens", "latencyMs", "question")})
    kinds = Counter(e.get("kind") for e in events)
    detectors = Counter(e.get("detector") for e in events if e.get("detector"))
    data = {"captured_at": now, "rows": len(rows), "guard_events": len(events), "by_kind": dict(kinds),
            "by_detector": dict(detectors), "sample_events": events[-6:], "audit_records": len(audit),
            "audit_sample": audit[-4:]}
    (OUT / "logs.json").write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"{len(rows)} rows: {len(events)} guard events {dict(kinds)}, {len(audit)} audit records")

elif WHAT == "final":
    left = az("resource", "list", "-g", RG, "--query", "[].{name:name, type:type}")
    (OUT / "final.json").write_text(json.dumps({"captured_at": now, "resources": left}, indent=2), encoding="utf-8")
    print(f"{len(left)} resource(s) left: {[r['name'] for r in left]}")
