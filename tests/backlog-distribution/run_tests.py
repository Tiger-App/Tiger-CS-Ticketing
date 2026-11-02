#!/usr/bin/env python3
"""
Validation suite for the Call Center backlog-distribution scripts (Preview / Execute / Rollback) against a REAL SQL Server.

DISPOSABLE SQL SERVER ONLY. It restores a database named TigerCS_Ticketing_Prod inside a throw-away container (the scripts' own name guard
requires that exact name) - never point it at a real server. See README.md.

 * The schema is built from the repo's real migration scripts (src/TicketingMigrations.sql + the four pending Production migrations).
 * The expected Round Robin deal is recomputed by an INDEPENDENT oracle in Python (canonical GUID text order == .NET Guid order), not by T-SQL.
 * Every test starts from the same restored fixture (01_seed.sql).

Env: TIGERSQL_CONTAINER (default tigersql), TIGERSQL_SA_PASSWORD (default Tiger#Test2026x).
"""
import hashlib, os, re, subprocess, sys, tempfile, threading, time, uuid

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
CONTAINER = os.environ.get("TIGERSQL_CONTAINER", "tigersql")
PWD = os.environ.get("TIGERSQL_SA_PASSWORD", "Tiger#Test2026x")
DB = "TigerCS_Ticketing_Prod"
SVC = "a6000000-0000-0000-0000-000000000001"   # the fixture's service account
PREVIEW = os.path.join(ROOT, "Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql")
EXECUTE = os.path.join(ROOT, "Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql")
ROLLBACK = os.path.join(ROOT, "Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql")

def sqlcmd_base(db=DB):
    return ["docker", "exec", "-i", CONTAINER, "/opt/mssql-tools18/bin/sqlcmd", "-C", "-I", "-S", "localhost", "-U", "sa", "-P", PWD, "-d", db,
            "-b", "-W", "-s", "|", "-w", "65535"]

def run_text(sql, db=DB, timeout=600):
    """Run a T-SQL batch (may contain GO). Returns (returncode, stdout+stderr)."""
    with tempfile.NamedTemporaryFile("w", suffix=".sql", delete=False, encoding="utf-8") as f:
        f.write(sql); path = f.name
    os.chmod(path, 0o644)
    remote = "/tmp/t_%s.sql" % uuid.uuid4().hex
    subprocess.run(["docker", "cp", path, "%s:%s" % (CONTAINER, remote)], check=True, capture_output=True)
    os.unlink(path)
    p = subprocess.run(sqlcmd_base(db) + ["-i", remote], capture_output=True, text=True, timeout=timeout)
    return p.returncode, p.stdout + p.stderr

def rows(query, db=DB):
    rc, out = run_text("SET NOCOUNT ON;\n" + query, db)
    if rc != 0: raise RuntimeError("query failed: %s\n%s" % (query, out))
    lines = [l for l in out.splitlines() if l.strip()]
    for i, l in enumerate(lines[:2]):                      # header (possibly blank, so dropped above) is followed by a dashes line
        if l and set(l.replace("|", "")) <= {"-"}:
            return [x.split("|") for x in lines[i + 1:]]
    return [x.split("|") for x in lines]

def scalar(query): return rows(query)[0][0]

def restore():
    rc, out = run_text(
        "ALTER DATABASE [%s] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\n"
        "RESTORE DATABASE [%s] FROM DISK = '/tmp/seed.bak' WITH REPLACE;\n"
        "ALTER DATABASE [%s] SET MULTI_USER;" % (DB, DB, DB), "master")
    if rc != 0: raise RuntimeError("restore failed\n" + out)

def patch(script_path, **params):
    """Set `DECLARE @Name type = value;` defaults; special keys: services=[ids] injects the service-account list."""
    s = open(script_path, encoding="utf-8").read()
    services = params.pop("services", None)
    for k, v in params.items():
        s, n = re.subn(r"(DECLARE @%s\s+\w+(?:\([^)]*\))?\s*=\s*)[^;]*;" % k, lambda m: m.group(1) + str(v) + ";", s, count=1)
        assert n == 1, "parameter @%s not found in %s" % (k, os.path.basename(script_path))
    if services:
        ins = "\n".join("INSERT INTO @ServiceIdentity VALUES ('%s');" % x for x in services)
        s, n = re.subn(r"^-- INSERT INTO @ServiceIdentity VALUES.*$", ins, s, count=1, flags=re.M)
        assert n == 1, "service list marker not found"
    return s

def execute(dry=True, batch=None, approved=None, services=(SVC,), **extra):
    p = dict(DryRun=1 if dry else 0, ServiceIdsConfirmed=1, services=list(services))
    if batch: p["BatchId"] = "'%s'" % batch
    if approved: p.update(ApprovedTicketCount=approved[0], ApprovedTransferCount=approved[1], ApprovedAgentCount=approved[2])
    p.update(extra)
    return run_text(patch(EXECUTE, **p))

def rollback(batch, dry=True, **extra):
    return run_text(patch(ROLLBACK, BatchId="'%s'" % batch, DryRun=1 if dry else 0, **extra))

# ---------------------------------------------------------------------------------------------------------------------------------
# state capture
# ---------------------------------------------------------------------------------------------------------------------------------
def table_dump(table, order):
    cols = [r[0] for r in rows("SELECT c.name FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id "
                               "WHERE c.object_id=OBJECT_ID('dbo.%s') AND t.name<>'timestamp' ORDER BY c.column_id" % table)]
    return rows("SELECT %s FROM dbo.%s ORDER BY %s" % (", ".join("CONVERT(nvarchar(max),[%s])" % c for c in cols), table, order))

TABLES = [("Tickets", "TicketId"), ("TicketAssignments", "TicketAssignmentId"), ("AuditEntries", "AuditEntryId"),
          ("TicketWorkflowEvents", "TicketWorkflowEventId"), ("DepartmentAssignmentRotations", "DepartmentId"),
          ("TicketStatusHistory", "TicketStatusHistoryId"), ("TicketNotes", "TicketNoteId"), ("Employees", "EmployeeId"),
          ("UserDepartmentAssignments", "UserDepartmentAssignmentId")]

def state():
    return {t: table_dump(t, o) for t, o in TABLES}

def digest(st):
    return {t: hashlib.sha256(repr(r).encode()).hexdigest()[:12] for t, r in st.items()}

# ---------------------------------------------------------------------------------------------------------------------------------
# independent oracle
# ---------------------------------------------------------------------------------------------------------------------------------
def oracle(service_ids=(SVC,), cs_agent_only=True, include_live_ai=False):
    """Expected plan {ticketId: agentId} computed in Python from raw table data."""
    svc = {s.lower() for s in service_ids}
    cc = int(scalar("SELECT DepartmentId FROM dbo.Departments WHERE Code='CC' AND IsActive=1"))
    members = rows("SELECT e.EmployeeId, CASE WHEN e.DeactivatedAtUtc IS NULL THEN 0 ELSE 1 END, "
                   "CASE WHEN u.LockoutEnabled=1 AND u.LockoutEnd>SYSDATETIMEOFFSET() THEN 1 ELSE 0 END "
                   "FROM dbo.Employees e JOIN dbo.AspNetUsers u ON u.Id=e.EmployeeId "
                   "JOIN dbo.UserDepartmentAssignments a ON a.EmployeeId=e.EmployeeId AND a.DepartmentId=%d" % cc)
    roles = {}
    for uid, name in rows("SELECT ur.UserId, r.Name FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id=ur.RoleId"):
        roles.setdefault(uid.lower(), set()).add(name)
    ops = {"CS Agent"} if cs_agent_only else {"CS Agent", "Department Employee", "Department Head"}
    agents = sorted({m[0].lower() for m in members
                     if m[1] == "0" and m[2] == "0" and m[0].lower() not in svc and roles.get(m[0].lower(), set()) & ops})
    cur = rows("SELECT CONVERT(nvarchar(36),LastAssignedEmployeeId) FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId=%d" % cc)
    cursor = cur[0][0].lower() if cur and cur[0][0] != "NULL" else None
    cands = rows("SELECT CONVERT(nvarchar(30),t.CreatedAtUtc,121), t.TicketId, t.CurrentDepartmentId FROM dbo.Tickets t WHERE t.TicketStatus IN (1,2,3,4) "
                 "AND t.CurrentOwnerEmployeeId IS NULL AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId=t.TicketId AND a.IsCurrent=1)"
                 + ("" if include_live_ai else " AND NOT EXISTS (SELECT 1 FROM dbo.TicketInteractions i WHERE i.TicketId=t.TicketId AND i.IsOriginatingInteraction=1 AND i.GenesysConversationId IS NOT NULL AND i.RequiresHumanFollowUp=0 AND i.EndedAtUtc IS NULL AND i.InactivityClosedAtUtc IS NULL)"))
    cands.sort(key=lambda r: (r[0], int(r[1])))
    start = 0
    if cursor:
        nxt = [i for i, a in enumerate(agents) if a > cursor]
        start = nxt[0] if nxt else 0
    plan = {int(t): agents[(start + i) % len(agents)] for i, (_, t, _) in enumerate(cands)}
    origin = {int(t): int(d) for _, t, d in cands}
    return plan, origin, cc, agents

# ---------------------------------------------------------------------------------------------------------------------------------
results = []
def check(name, cond, detail=""):
    results.append((name, bool(cond), detail))
    print(("  PASS  " if cond else "  FAIL  ") + name + ((" -> " + str(detail)) if (detail and not cond) else ""))

def has(out, text): return text.lower() in out.lower()

def last_batch_id(out):
    m = re.search(r"BatchId=([0-9A-Fa-f-]{36})", out); return m.group(1) if m else None

APPROVED = (62, 56, 3)
BATCH = "11111111-2222-3333-4444-555555555555"

def t01_preview_readonly():
    print("T01 preview is read-only and correct")
    restore(); before = digest(state())
    p = patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC])
    rc, out = run_text(p)
    after = digest(state())
    check("preview exits 0", rc == 0, out[-400:])
    check("preview changed nothing (all table digests equal)", before == after, [t for t in before if before[t] != after[t]])
    check("verdict READY", "READY" in out)
    plan, origin, cc, agents = oracle()
    check("oracle: 62 in scope (6 live-AI held), 56 from other depts, 3 CS Agents", len(plan) == 62 and sum(1 for t, d in origin.items() if d != cc) == 56 and len(agents) == 3,
          (len(plan), len(agents)))
    check("preview approval row = 62/56/3", re.search(r"\n62\|56\|3\s*\n", out) is not None)
    check("preview lists the 6 held-back live AI conversations once (held list) and none of them in the deal plan", out.count("X-LIVEAI-") == 6 and not any("X-LIVEAI-" in l and "TRANSFER" in l for l in out.splitlines()), out.count("X-LIVEAI-"))
    # exact plan rows: DealOrder|TicketId|...|agent display name
    got = {}
    for line in out.splitlines():
        m = re.match(r"^(\d+)\|(\d+)\|E-\d+\|\w+\|[^|]+\|\w+\|[^|]+\|(\w+) Person\|", line)
        if m: got[int(m.group(2))] = m.group(3)
    names = {a: scalar("SELECT DisplayName FROM dbo.Employees WHERE EmployeeId='%s'" % a).split()[0] for a in agents}
    check("preview plan == independent oracle (all 62 tickets)", len(got) == 62 and all(names[plan[t]] == got[t] for t in got), (len(got),))
    # blockers
    rc, out = run_text(patch(PREVIEW, services=[SVC]))
    check("blocker when @ServiceIdsConfirmed = 0", "BLOCKER: service-account list not confirmed" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1))
    check("blocker when service list empty and no explicit 'none exist'", "BLOCKER: service-account list is empty" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, NoServiceAccountsExist=1))
    check("'none exist' acknowledgement clears that blocker", "READY" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=["99999999-9999-9999-9999-999999999999"]))
    check("blocker when a listed id is not an employee", "not an employee" in out)
    # live-AI hold and CS-Agent-only are explicit, visible switches
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], IncludeLiveAiConversations=1))
    plan2, origin2, cc2, agents2 = oracle(include_live_ai=True)
    check("with @IncludeLiveAiConversations=1 the preview scope is the 68 tickets the oracle expects", len(plan2) == 68 and re.search(r"\n68\|\d+\|3\s*\n", out) is not None, (len(plan2), out[:600]))
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], CsAgentRoleOnly=0))
    check("with @CsAgentRoleOnly=0 the Department Employee joins the pool (4 agents)", re.search(r"\n62\|56\|4\s*\n", out) is not None, out[:600])

def t02_dry_run():
    print("T02 execute @DryRun=1 writes nothing")
    restore(); before = digest(state())
    rc, out = execute(dry=True)
    after = digest(state())
    check("dry run exits 0 and says ROLLED BACK", rc == 0 and has(out, "DRY RUN complete - transaction ROLLED BACK"), out[-500:])
    check("dry run changed nothing", before == after, [t for t in before if before[t] != after[t]])
    check("dry run reports 62 assigned / 56 moved", has(out, "Tickets that would have been assigned: 62") and has(out, "transferred to Call Center: 56"))

def t03_gates():
    print("T03 commit gates")
    restore(); before = digest(state())
    def refused(label, rc_out, text):
        rc, out = rc_out
        check(label, rc != 0 and has(out, text) and digest(state()) == before, out[-300:])
    refused("no approval counts -> refused", run_text(patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC])), "commit gates not satisfied")
    refused("service ids not confirmed -> refused", run_text(patch(EXECUTE, DryRun=0, ApprovedTicketCount=62, ApprovedTransferCount=56, ApprovedAgentCount=3, services=[SVC])), "commit gates not satisfied")
    refused("empty service list without acknowledgement -> refused", execute(False, approved=APPROVED, services=()), "service-account list is empty")
    refused("service id typo -> refused", execute(False, approved=APPROVED, services=("99999999-9999-9999-9999-999999999999",)), "not an employee")
    refused("wrong ticket count -> refused", execute(False, approved=(61, 56, 3)), "backlog changed since the preview")
    refused("wrong moved count -> refused", execute(False, approved=(62, 55, 3)), "backlog changed since the preview")
    refused("wrong agent count -> refused", execute(False, approved=(62, 56, 4)), "ApprovedAgentCount")
    # listing a REAL agent as a 'service account' removes them from the pool -> agent count gate trips
    refused("excluding one more agent changes the pool -> refused", execute(False, approved=APPROVED, services=(SVC, "00000000-0000-0000-0000-000000000002")), "ApprovedAgentCount")
    # DB-name guard
    rc, out = run_text("CREATE DATABASE WrongName", "master");
    rc, out = run_text(patch(EXECUTE, DryRun=1), "WrongName")
    check("wrong database name -> aborted by guard", rc != 0 and has(out, "this script only runs against"), out[-200:])
    run_text("DROP DATABASE WrongName", "master")

def verify_commit(plan, origin, cc, pre, batch):
    st = state()
    tk = {r[0]: r for r in st["Tickets"]}
    cols = [r[0] for r in rows("SELECT c.name FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID('dbo.Tickets') AND t.name<>'timestamp' ORDER BY c.column_id")]
    ix = {c: i for i, c in enumerate(cols)}
    pre_tk = {r[0]: r for r in pre["Tickets"]}
    ok_owner = ok_dept = ok_pres = True
    for t, agent in plan.items():
        r, p0 = tk[str(t)], pre_tk[str(t)]
        ok_owner &= r[ix["CurrentOwnerEmployeeId"]].lower() == agent
        ok_dept &= int(r[ix["CurrentDepartmentId"]]) == cc
        for c in ("OriginatingDepartmentId", "RequestTypeId", "CategoryId", "PriorityId", "TicketStatus", "SlaState", "EscalationLevel", "VerificationStatus", "CreatedAtUtc", "TicketNumber", "RequestSummary"):
            ok_pres &= r[ix[c]] == p0[ix[c]]
    check("every eligible ticket owned by its oracle-planned agent", ok_owner)
    check("every eligible ticket now in Call Center", ok_dept)
    check("originating dept / request type / category / priority / status / SLA state / created preserved", ok_pres)
    untouched = [t for t in tk if int(t) not in plan]
    check("all %d non-eligible tickets are byte-identical" % len(untouched), all(tk[t] == pre_tk[t] for t in untouched))
    # assignments
    cur = rows("SELECT TicketId, CONVERT(nvarchar(36),AssignedEmployeeId), AssignedDepartmentId, ISNULL(CONVERT(nvarchar(36),AssigningActorEmployeeId),'NULL') FROM dbo.TicketAssignments WHERE IsCurrent=1")
    curmap = {}
    for t, a, d, act in cur: curmap.setdefault(int(t), []).append((a.lower(), int(d), act))
    check("exactly one current assignment per eligible ticket, matching owner, in CC, system actor",
          all(len(curmap.get(t, [])) == 1 and curmap[t][0] == (a, cc, "NULL") for t, a in plan.items()))
    check("no ticket anywhere has two current assignments", all(len(v) == 1 for v in curmap.values()))
    pre_a = {r[0] for r in pre["TicketAssignments"]}
    check("no pre-existing assignment row was modified", all(r in st["TicketAssignments"] for r in pre["TicketAssignments"]))
    check("%d new assignment rows" % len(plan), len(st["TicketAssignments"]) - len(pre["TicketAssignments"]) == len(plan))
    # audit
    moved = [t for t, d in origin.items() if d != cc]
    aud = rows("SELECT Action, EntityId, ISNULL(BeforeValue,'NULL'), ISNULL(AfterValue,'NULL') FROM dbo.AuditEntries WHERE CorrelationId='%s'" % batch)
    transfers = {int(a[1]): a for a in aud if a[0] == "Transfer"}
    autos = {int(a[1]): a for a in aud if a[0] == "AutoAssign"}
    check("one Transfer audit per moved ticket with original dept in BeforeValue", set(transfers) == set(moved) and
          all(("DepartmentId=%d;" % origin[t]) in transfers[t][2] and ("DepartmentId=%d;" % cc) in transfers[t][3] for t in moved))
    check("one AutoAssign audit per eligible ticket naming the planned agent", set(autos) == set(plan) and all(("AssignedEmployeeId=" + plan[t]) in autos[t][3] for t in plan))
    check("one batch audit row", sum(1 for a in aud if a[0] == "BacklogDistributionBatch") == 1)
    check("all pre-existing audit rows unchanged", all(r in st["AuditEntries"] for r in pre["AuditEntries"]))
    # workflow events
    ev = rows("SELECT TicketId, EventType, TransferReasonCode, FromDepartmentId, ToDepartmentId, CONVERT(nvarchar(36),ToEmployeeId), ISNULL(CONVERT(nvarchar(36),ActorEmployeeId),'NULL') "
              "FROM dbo.TicketWorkflowEvents WHERE CorrelationId='%s'" % batch)
    check("one TicketTransferred/SystemRouting event per moved ticket, From->CC, receiving agent, system actor",
          len(ev) == len(moved) and all(e[1] == "17" and e[2] == "5" and int(e[3]) == origin[int(e[0])] and int(e[4]) == cc and e[5].lower() == plan[int(e[0])] and e[6] == "NULL" for e in ev))
    check("no event for tickets that did not move", {int(e[0]) for e in ev} == set(moved))
    check("pre-existing workflow events unchanged", all(r in st["TicketWorkflowEvents"] for r in pre["TicketWorkflowEvents"]))
    # untouched history
    for t in ("TicketStatusHistory", "TicketNotes", "Employees", "UserDepartmentAssignments"):
        check("%s identical" % t, st[t] == pre[t])
    # rotation + fairness + eligibility
    agents = {a for a in plan.values()}
    counts = {a: sum(1 for x in plan.values() if x == a) for a in agents}
    check("fair: per-agent spread <= 1", max(counts.values()) - min(counts.values()) <= 1, counts)
    bad = scalar("SELECT COUNT(*) FROM dbo.Tickets t JOIN dbo.Employees e ON e.EmployeeId=t.CurrentOwnerEmployeeId WHERE e.DisplayName IN ('DEACT Person','LOCK Person','SVC Person','RO Person','SUPV Person','CHAIR Person') AND t.TicketId IN (%s)" % ",".join(map(str, plan)))
    check("nothing assigned to deactivated / locked / service / read-only / supervisory accounts", bad == "0", bad)
    last = plan[list(plan)[-1]]
    cursor = scalar("SELECT LOWER(CONVERT(nvarchar(36),LastAssignedEmployeeId)) FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId=%d" % cc)
    check("rotation cursor = last agent dealt", cursor == last, (cursor, last))

def t04_commit():
    print("T04 real commit")
    restore(); pre = state()
    plan, origin, cc, agents = oracle()
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit exits 0 and says COMMITTED", rc == 0 and has(out, "COMMITTED. Tickets assigned: 62"), out[-500:])
    check("prints the BatchId for rollback", BATCH in out)
    verify_commit(plan, origin, cc, pre, BATCH)
    rc, out = execute(dry=False, batch="22222222-2222-3333-4444-555555555555", approved=APPROVED)
    check("second run refused (one-time)", rc != 0 and has(out, "BacklogDistributionBatch audit row already exists"), out[-200:])
    rc, out = execute(dry=True, batch="33333333-2222-3333-4444-555555555555", AllowSecondBatch=1)
    check("with explicit override, a re-run finds nothing (idempotent)", has(out, "NOTHING TO DO"), out[-200:])
    check("no ticket has >1 current assignment", scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")
    return pre

def t05_rollback_clean():
    print("T05 rollback of an untouched batch restores the exact original state")
    restore(); pre = state()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    mid = digest(state())
    rc, out = rollback(BATCH, dry=True)
    check("rollback dry run exits 0, changes nothing", rc == 0 and digest(state()) == mid and has(out, "DRY RUN complete"), out[-300:])
    check("dry run plans 62 rollbacks, 56 department restores, 0 skipped", re.search(r"\|62\|62\|56\|0\|", out) is not None, out[-400:])
    rc, out = rollback(BATCH, dry=False)
    check("rollback commits", rc == 0 and has(out, "ROLLBACK COMMITTED. Released: 62"), out[-300:])
    post = state()
    check("Tickets table identical to pre-batch (owner, department, everything)", post["Tickets"] == pre["Tickets"])
    check("batch assignment rows retired (IsCurrent=0), not deleted", scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE IsCurrent=0 AND AssigningActorEmployeeId IS NULL AND AssignedAtUtc=(SELECT OccurredAtUtc FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch')") == "62")
    check("assignment rows only grew", len(post["TicketAssignments"]) == len(pre["TicketAssignments"]) + 62)
    check("every pre-existing assignment row is byte-identical (rollback only retires its own rows)", all(r in post["TicketAssignments"] for r in pre["TicketAssignments"]))
    check("all earlier audit rows intact", all(r in post["AuditEntries"] for r in pre["AuditEntries"]))
    check("all earlier workflow events intact", all(r in post["TicketWorkflowEvents"] for r in pre["TicketWorkflowEvents"]))
    check("rotation cursor restored to pre-batch", post["DepartmentAssignmentRotations"][0][1:2] == ["NULL"] or scalar("SELECT COUNT(*) FROM dbo.DepartmentAssignmentRotations WHERE LastAssignedEmployeeId IS NULL") == "1")
    check("history / notes untouched", post["TicketStatusHistory"] == pre["TicketStatusHistory"] and post["TicketNotes"] == pre["TicketNotes"])
    check("reversal events recorded for the 56 moved tickets", scalar("SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE Note LIKE 'Rollback of one-time%'") == "56")
    check("no current assignment left on any restored ticket", scalar("SELECT COUNT(*) FROM dbo.TicketAssignments a JOIN dbo.Tickets t ON t.TicketId=a.TicketId WHERE a.IsCurrent=1 AND t.TicketNumber LIKE 'E-%'") == "0")
    rc, out = rollback(BATCH, dry=False)
    check("second rollback of same batch refused", rc != 0 and has(out, "already been rolled back"), out[-200:])
    rc, out = rollback("99999999-9999-9999-9999-999999999999", dry=True)
    check("rollback with unknown BatchId refused", rc != 0 and has(out, "expected exactly one BacklogDistributionBatch"), out[-200:])
    # after a rollback the one-time guard still blocks a casual re-run, but the preview sees the backlog again
    rc, out = execute(dry=True)
    check("re-run after rollback still needs explicit @AllowSecondBatch", rc != 0 and has(out, "already exists"))
    rc, out = execute(dry=True, AllowSecondBatch=1)
    check("with override the backlog is eligible again (62)", has(out, "Tickets that would have been assigned: 62"), out[-300:])

def must(sql):
    rc, out = run_text(sql)
    if rc != 0: raise RuntimeError("test setup failed (this would silently invalidate the test):\n" + out[:600])

def t06_rollback_touched():
    print("T06 rollback skips tickets touched since the batch")
    restore(); pre = state()
    plan, origin, cc, agents = oracle()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    tid = sorted(plan)
    reassigned, statused, noted, transferred, handled, evented, responded = tid[0], tid[1], tid[2], tid[3], tid[4], tid[5], tid[6]
    SUPV = "a1000000-0000-0000-0000-000000000001"
    A3 = "7a000000-0000-0000-0000-000000000001" if plan[reassigned] != "7a000000-0000-0000-0000-000000000001" else "00000000-0000-0000-0000-000000000002"   # a DIFFERENT agent than the batch dealt
    fin = scalar("SELECT DepartmentId FROM dbo.Departments WHERE Code='FIN'")
    must(f"""
      SET QUOTED_IDENTIFIER ON;
      -- 1 reassigned by a supervisor to another agent
      UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={reassigned} AND IsCurrent=1;
      INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent)
        VALUES({reassigned},'{A3}',{cc},DATEADD(second,5,SYSUTCDATETIME()),'{SUPV}',1);
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{A3}' WHERE TicketId={reassigned};
      -- 2 agent started work (status history row)
      INSERT dbo.TicketStatusHistory(TicketId,Dimension,OldValue,NewValue,ActorEmployeeId,ActorIsSystem,CorrelationId,OccurredAtUtc)
        VALUES({statused},1,1,2,'{plan[statused]}',0,NEWID(),DATEADD(minute,1,SYSUTCDATETIME()));
      -- 3 note added
      INSERT dbo.TicketNotes(TicketId,NoteText,AuthorEmployeeId,CreatedAtUtc) VALUES({noted},N'called customer','{plan[noted]}',DATEADD(minute,1,SYSUTCDATETIME()));
      -- 4 transferred out by the agent (owner cleared, department changed, assignment retired)
      UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={transferred} AND IsCurrent=1;
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId=NULL, CurrentDepartmentId={fin} WHERE TicketId={transferred};
      -- 5 Genesys handler recorded
      INSERT dbo.TicketInteractions(TicketId,IsOriginatingInteraction,Source,ChannelId,CustomerPhone,CreatedAtUtc) VALUES({handled},0,1,1,N'0500000000',SYSUTCDATETIME());
      INSERT dbo.TicketInteractionHandlers(TicketInteractionId,TicketId,HandledByUserId,GenesysAgentUserId,Source,RecordedAtUtc)
        VALUES(SCOPE_IDENTITY(),{handled},'{plan[handled]}',N'g-1',1,DATEADD(minute,1,SYSUTCDATETIME()));
      -- 6 a later workflow event (e.g. handoff requested)
      INSERT dbo.TicketWorkflowEvents(TicketId,EventType,OccurredAtUtc,ActorEmployeeId,Note,CorrelationId) VALUES({evented},10,DATEADD(minute,1,SYSUTCDATETIME()),NULL,N'handoff requested',NEWID());
      -- 7 first human response recorded
      UPDATE dbo.Tickets SET FirstHumanResponseAtUtc=DATEADD(minute,2,SYSUTCDATETIME()) WHERE TicketId={responded};
    """)
    touched = {reassigned, statused, noted, transferred, handled, evented, responded}
    before = state()
    rc, out = rollback(BATCH, dry=True)
    check("dry run plans %d releases and %d skips" % (62 - len(touched), len(touched)), rc == 0 and has(out, "|%d|" % (62 - len(touched))) and digest(state()) == digest(before), out[-500:])
    rc, out = rollback(BATCH, dry=False)
    check("rollback commits", rc == 0, out[-400:])
    check("reports %d released" % (62 - len(touched)), has(out, "Released: %d" % (62 - len(touched))), out[-300:])
    st = state(); tk = {r[0]: r for r in st["Tickets"]}; bk = {r[0]: r for r in before["Tickets"]}
    check("each of the 7 touched tickets is byte-identical to how the human left it", all(tk[str(t)] == bk[str(t)] for t in touched),
          [t for t in touched if tk[str(t)] != bk[str(t)]])
    check("no rollback audit / reversal event / assignment retirement for touched tickets",
          scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action IN ('AutoAssignRollback','Transfer') AND CorrelationId<>'%s' AND AuditEntryId>(SELECT AuditEntryId FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch') AND EntityId IN (%s)" % (BATCH, ",".join(map(str, touched)))) == "0"
          and all(a in st["TicketAssignments"] for a in before["TicketAssignments"] if a[1] in {str(t) for t in touched}))
    released = [t for t in plan if t not in touched]
    pre_tk = {r[0]: r for r in pre["Tickets"]}
    check("all %d untouched tickets are back to pre-batch owner AND department" % len(released), all(tk[str(t)] == pre_tk[str(t)] for t in released),
          [t for t in released if tk[str(t)] != pre_tk[str(t)]][:5])
    for reason in ("owner changed since the batch", "transferred out of Call Center", "worked since the batch"):
        check("skip reason reported: " + reason, has(out, reason))
    check("5 'worked' tickets reported with activity counts", len(re.findall(r"worked since the batch", out)) >= 1)
    check("rotation cursor restored to pre-batch value (nobody was dealt a ticket since)", scalar("SELECT COUNT(*) FROM dbo.DepartmentAssignmentRotations WHERE LastAssignedEmployeeId IS NULL") == "1")
    check("every ticket has <=1 current assignment", scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")

def t06b_include_worked():
    print("T06b @IncludeWorked relaxes only the 'worked' test")
    restore()
    plan, origin, cc, agents = oracle()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    tid = sorted(plan); worked, reassigned = tid[0], tid[1]
    A3 = "7a000000-0000-0000-0000-000000000001" if plan[reassigned] != "7a000000-0000-0000-0000-000000000001" else "00000000-0000-0000-0000-000000000002"
    must(f"""
      SET QUOTED_IDENTIFIER ON;
      INSERT dbo.TicketNotes(TicketId,NoteText,AuthorEmployeeId,CreatedAtUtc) VALUES({worked},N'note','{plan[worked]}',DATEADD(minute,1,SYSUTCDATETIME()));
      UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={reassigned} AND IsCurrent=1;
      INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) VALUES({reassigned},'{A3}',{cc},DATEADD(second,5,SYSUTCDATETIME()),NULL,1);
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{A3}' WHERE TicketId={reassigned};""")
    rc, out = rollback(BATCH, dry=False, IncludeWorked=1)
    check("IncludeWorked=1 releases the worked ticket but still skips the reassigned one", rc == 0 and has(out, "Released: 61"), out[-300:])
    check("reassigned ticket still owned by the human's choice", scalar(f"SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketId={reassigned}") == A3)

def t07_concurrency():
    print("T07 concurrency")
    restore(); pre = state()
    plan, origin, cc, agents = oracle()
    slow = patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], ApprovedTicketCount=62, ApprovedTransferCount=56, ApprovedAgentCount=3, BatchId="'%s'" % BATCH)
    slow = slow.replace("    -- (h) report", "    WAITFOR DELAY '00:00:14';\n    -- (h) report", 1)
    assert "WAITFOR DELAY" in slow
    res = {}
    def a(): res["A"] = run_text(slow)
    th = threading.Thread(target=a); th.start(); time.sleep(5)      # A now holds app lock, cursor lock and the ticket locks
    victim = sorted(plan)[10]
    resB, resC = {}, {}
    def runB():
        t0 = time.time(); resB["r"] = execute(dry=False, batch="44444444-2222-3333-4444-555555555555", approved=APPROVED); resB["t"] = time.time() - t0
    def runC():
        t0 = time.time()
        resC["r"] = run_text(f"SET LOCK_TIMEOUT -1; SET NOCOUNT OFF; UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='7a000000-0000-0000-0000-000000000001' WHERE TicketId={victim} AND CurrentOwnerEmployeeId IS NULL;")
        resC["t"] = time.time() - t0
    tb, tc = threading.Thread(target=runB), threading.Thread(target=runC)
    tb.start(); tc.start(); tb.join(); tc.join(); th.join()
    rcB, outB = resB["r"]; tB = resB["t"]; rcC, outC = resC["r"]; tC = resC["t"]
    check("a second concurrent run is refused (application lock, or it queues behind the batch's uncommitted audit row and then finds the batch) and writes nothing", rcB != 0 and (has(outB, "application lock") or has(outB, "BacklogDistributionBatch audit row already exists")) and tB < 20, (round(tB, 1), outB[-200:]))
    check("a concurrent 'assign this ticket' blocks until the batch commits", tC > 3, round(tC, 1))
    check("...and then affects 0 rows (cannot overwrite the batch's owner)", "(0 rows affected)" in outC, outC[-200:])
    rcA, outA = res["A"]
    check("the batch itself committed", rcA == 0 and has(outA, "COMMITTED"), outA[-300:])
    owner = scalar(f"SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketId={victim}")
    check("victim ticket kept the oracle-planned owner", owner == plan[victim], (owner, plan[victim]))
    check("exactly one current assignment per ticket", scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")
    # the filtered unique index is the last line of defence
    rc, out = run_text(f"INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,IsCurrent) VALUES({victim},'7a000000-0000-0000-0000-000000000001',{cc},SYSUTCDATETIME(),1)")
    check("a duplicate CURRENT assignment is rejected by the unique index", rc != 0 and has(out, "unique index"), out[-200:])

def t08_race_between_preview_and_run():
    print("T08 backlog changes between preview and execution")
    restore()
    run_text("UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='00000000-0000-0000-0000-000000000002' WHERE TicketNumber='E-0020'; "
             "INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) "
             "SELECT TicketId,'00000000-0000-0000-0000-000000000002',CurrentDepartmentId,SYSUTCDATETIME(),'00000000-0000-0000-0000-000000000002',1 FROM dbo.Tickets WHERE TicketNumber='E-0020'")
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED)
    check("a ticket taken by a human after the preview aborts the run (approved 62, now 61)", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-300:])
    rc, out = execute(dry=False, approved=(61, 55, 3))
    check("re-approved with the new numbers it commits and leaves the human-owned ticket alone", rc == 0 and scalar("SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketNumber='E-0020'") == "00000000-0000-0000-0000-000000000002")

def t09_cursor_and_pool_edges():
    print("T09 cursor / pool edge cases vs oracle")
    for label, setup in [
        ("cursor in the middle", "UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId='7a000000-0000-0000-0000-000000000001'"),
        ("cursor on the last agent (wraps)", "UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId='f0000000-0000-0000-0000-000000000009'"),
        ("cursor on an ineligible/deactivated agent", "UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId='a4000000-0000-0000-0000-000000000001'"),
        ("one eligible agent only", "DELETE FROM dbo.UserDepartmentAssignments WHERE EmployeeId IN ('00000000-0000-0000-0001-000000000000','7a000000-0000-0000-0000-000000000001','f0000000-0000-0000-0000-000000000009')"),
    ]:
        restore()
        run_text("IF NOT EXISTS (SELECT 1 FROM dbo.DepartmentAssignmentRotations) INSERT dbo.DepartmentAssignmentRotations(DepartmentId,LastAssignedEmployeeId,UpdatedAtUtc) SELECT DepartmentId,NULL,SYSUTCDATETIME() FROM dbo.Departments WHERE Code='CC';\n" + setup)
        plan, origin, cc, agents = oracle()
        n_agents = len(agents)
        rc, out = execute(dry=False, batch=BATCH, approved=(len(plan), sum(1 for d in origin.values() if d != cc), n_agents))
        got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE TicketNumber LIKE 'E-%'")}
        check("%s: commit equals oracle for all %d tickets (%d agent(s))" % (label, len(plan), n_agents), rc == 0 and all(got[t] == a for t, a in plan.items()), out[-300:])
    restore()
    run_text("UPDATE dbo.DepartmentWorkflowSettings SET AllowAssignment=0 WHERE 1=0; INSERT dbo.DepartmentWorkflowSettings(DepartmentId,AllowAssignment,AllowInternalReassignment,AllowTransferToOtherDepartments,HeadRoleName,SupervisorRoleName) SELECT DepartmentId,0,0,0,N'h',N's' FROM dbo.Departments WHERE Code='CC'")
    rc, out = execute(dry=True)
    check("CC assignment disabled -> aborted like the engine", rc != 0 and has(out, "disable assignment"), out[-200:])
    restore()
    run_text("DELETE FROM dbo.UserDepartmentAssignments WHERE DepartmentId=(SELECT DepartmentId FROM dbo.Departments WHERE Code='CC')")
    rc, out = execute(dry=True)
    check("no eligible agent -> aborted, nothing changed", rc != 0 and has(out, "no eligible Call Center agent"), out[-200:])
    restore()
    run_text("UPDATE dbo.Departments SET IsActive=0 WHERE Code='CC'")
    rc, out = execute(dry=True)
    check("CC department inactive -> aborted", rc != 0 and has(out, "exactly one ACTIVE department"), out[-200:])

def t10_scale():
    n = int(os.environ.get("SCALE_N", "20000"))
    print("T10 scale: %d extra eligible tickets (plus a large owned/closed population)" % n)
    restore()
    must(f"""
      SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON;
      DECLARE @d TABLE (id int, rn int); INSERT @d SELECT DepartmentId, ROW_NUMBER() OVER (ORDER BY DepartmentId) FROM dbo.Departments;
      ;WITH x AS (SELECT TOP ({n}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
      INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
      SELECT N'S-' + CONVERT(nvarchar(10), i), d.id, d.id, NULL, (i % 4) + 1, 0, 0, 1, N'scale', 0, DATEADD(second, i, '2026-06-01') FROM x CROSS APPLY (SELECT id FROM @d WHERE rn = (i % 5) + 1) d;
      -- a big closed / owned population that must never be read into the plan
      ;WITH x AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
      INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
      SELECT N'C-' + CONVERT(nvarchar(10), i), 3, 3, NULL, 6, 0, 0, 3, N'closed', 0, '2026-01-01' FROM x;
      INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
      SELECT TOP (300000) NULL, N'Create', N'Ticket', CONVERT(nvarchar(100), TicketId), NULL, N'seed', NEWID(), CreatedAtUtc FROM dbo.Tickets;
    """)
    total = int(scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus IN (1,2,3,4) AND TicketNumber NOT LIKE 'X-LIVEAI-%' AND CurrentOwnerEmployeeId IS NULL AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId=Tickets.TicketId AND a.IsCurrent=1)"))
    moved = int(scalar("SELECT COUNT(*) FROM dbo.Tickets t WHERE TicketStatus IN (1,2,3,4) AND TicketNumber NOT LIKE 'X-LIVEAI-%' AND CurrentOwnerEmployeeId IS NULL AND CurrentDepartmentId<>(SELECT DepartmentId FROM dbo.Departments WHERE Code='CC') AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId=t.TicketId AND a.IsCurrent=1)"))
    print("   eligible=%d moved=%d audit rows=%s" % (total, moved, scalar("SELECT COUNT(*) FROM dbo.AuditEntries")))
    plan, origin, cc, agents = oracle()
    t0 = time.time(); rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC]).replace("ORDER BY c.Seq;", "ORDER BY c.Seq OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY;")); tp = time.time() - t0
    check("preview completes (%.1fs)" % tp, rc == 0 and "READY" in out, out[-300:])
    t0 = time.time(); rc, out = execute(dry=True); td = time.time() - t0
    check("dry run completes (%.1fs) and rolls back" % td, rc == 0 and has(out, "ROLLED BACK"), out[-300:])
    t0 = time.time(); rc, out = execute(dry=False, batch=BATCH, approved=(total, moved, 3)); tx = time.time() - t0
    check("commit of %d tickets completes (%.1fs)" % (total, tx), rc == 0 and has(out, "COMMITTED"), out[-300:])
    got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE TicketNumber LIKE 'S-%' OR TicketNumber LIKE 'E-%'")}
    check("all %d tickets match the independent oracle" % len(plan), all(got[t] == a for t, a in plan.items()))
    counts = {}
    for a in plan.values(): counts[a] = counts.get(a, 0) + 1
    check("fair at scale: spread <= 1 %s" % sorted(counts.values()), max(counts.values()) - min(counts.values()) <= 1)
    check("no duplicate current assignments", scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")
    check("the 100000 closed tickets were never touched", scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketNumber LIKE 'C-%' AND CurrentOwnerEmployeeId IS NULL AND CurrentDepartmentId=3") == "100000")
    t0 = time.time(); rc, out = rollback(BATCH, dry=False); tr = time.time() - t0
    check("rollback of %d tickets completes (%.1fs)" % (total, tr), rc == 0 and has(out, "Released: %d" % total), out[-300:])
    check("no owned eligible ticket remains after rollback", scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE (TicketNumber LIKE 'S-%' OR TicketNumber LIKE 'E-%') AND CurrentOwnerEmployeeId IS NOT NULL") == "0")
    print("   timings: preview %.1fs, dry-run %.1fs, commit %.1fs, rollback %.1fs" % (tp, td, tx, tr))

def t11_diagnose_readonly():
    print("T11 Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql: no errors, no writes, on the full AND the pre-release schema")
    src = open(os.path.join(ROOT, "Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql"), encoding="utf-8").read()
    restore(); before = digest(state())
    rc, out = run_text(src)
    check("full schema: exits 0 with no SQL error (no Msg 130 / 208)", rc == 0 and not re.search(r"^Msg \d+", out, re.M), out[:400])
    check("full schema: wrote nothing", digest(state()) == before)
    restore()
    must("DROP TABLE dbo.DepartmentAssignmentRotations; DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%AddDepartmentAssignmentRotation%';")
    rc, out = run_text(src)
    check("pre-release schema (no rotation table): exits 0 with no SQL error", rc == 0 and not re.search(r"^Msg \d+", out, re.M), out[:400])
    rc, out = run_text(src, "master")
    check("wrong database -> aborted by the guard", rc != 0 and has(out, "this script is for"), out[:300])

TESTS = [t01_preview_readonly, t02_dry_run, t03_gates, t04_commit, t05_rollback_clean, t06_rollback_touched, t06b_include_worked, t07_concurrency,
         t08_race_between_preview_and_run, t09_cursor_and_pool_edges, t10_scale, t11_diagnose_readonly]

if __name__ == "__main__":
    only = sys.argv[1:]
    for t in TESTS:
        if only and not any(o in t.__name__ for o in only): continue
        try: t()
        except Exception as ex:
            check("%s raised" % t.__name__, False, repr(ex))
    bad = [r for r in results if not r[1]]
    print("\n%d checks, %d passed, %d FAILED" % (len(results), len(results) - len(bad), len(bad)))
    for n, _, d in bad: print("  FAILED:", n, "|", d)
    sys.exit(1 if bad else 0)
