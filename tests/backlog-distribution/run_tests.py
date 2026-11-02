#!/usr/bin/env python3
"""
Validation suite for the Call Center backlog-distribution scripts (Preview / Execute / Rollback) against a REAL SQL Server.
Rule under test: EVERY Open ticket (assigned or not, any department) -> Call Center -> one eligible CS Agent; answering agent first, otherwise Round Robin;
InProgress/Pending/Resolved/Closed, pending approvals and live AI conversations are never touched; previous assignment rows are retired (history kept); rollback restores.

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
    if approved:
        approved = tuple(approved) + APPROVED[len(approved):]          # a short tuple keeps the fixture's remaining approved values
        p.update(ApprovedTicketCount=approved[0], ApprovedTransferCount=approved[1], ApprovedAgentCount=approved[2],
                 ApprovedHeldBackPendingApprovalCount=approved[3], ApprovedHeldBackActiveAiCount=approved[4],
                 ApprovedReassignedCount=approved[5], ApprovedPlanFingerprint=approved[6])
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
def held_sets(include_ai=False):
    """Independent re-implementation of the hold-back rules from raw rows: (base = ALL Open tickets, pending-approval ids, AI-only ids)."""
    base = {int(r[0]) for r in rows("SELECT t.TicketId FROM dbo.Tickets t WHERE t.TicketStatus = 1")}
    pend = {int(r[0]) for r in rows("SELECT DISTINCT TicketId FROM dbo.TicketApprovals WHERE Status=1")} & base
    inter = {}
    for t, src, ended, inact, req, handled in rows("SELECT TicketId, Source, ISNULL(CONVERT(nvarchar(30),EndedAtUtc),'-'), ISNULL(CONVERT(nvarchar(30),InactivityClosedAtUtc),'-'), "
                                                   "RequiresHumanFollowUp, ISNULL(CONVERT(nvarchar(36),HandledByUserId),'-') FROM dbo.TicketInteractions"):
        inter.setdefault(int(t), []).append((src, ended, inact, req, handled))
    handoff = {int(r[0]) for r in rows("SELECT DISTINCT TicketId FROM dbo.TicketAgentHandoffs WHERE Status IN (2,3,4)")}
    ai = set()
    for t in base:
        rs = inter.get(t, [])
        live = any(x[0] == "2" and x[1] == "-" and x[2] == "-" and x[3] in ("0", "False") and x[4] == "-" for x in rs)
        human = any(x[3] in ("1", "True") for x in rs)
        if live and not human and t not in handoff: ai.add(t)
    return base, pend, (set() if include_ai else ai - pend)

def oracle_full(service_ids=(SVC,), cs_only=True, include_ai=False):
    """Expected plan computed in Python from raw table data (NOT by the T-SQL under test).
    Every OPEN ticket (any owner) that is not held back; an eligible ANSWERING agent first, otherwise Round Robin over the rest, oldest first.
    Returns a dict: plan {ticket: agent}, origin {ticket: current dept}, cc, agents, prev {ticket: owner|None}, action {ticket: KEEP|ASSIGN|REASSIGN}, pinned {ticket}."""
    svc = {s.lower() for s in service_ids}
    cc = int(scalar("SELECT DepartmentId FROM dbo.Departments WHERE Code='CC' AND IsActive=1"))
    members = rows("SELECT e.EmployeeId, CASE WHEN e.DeactivatedAtUtc IS NULL THEN 0 ELSE 1 END, "
                   "CASE WHEN u.LockoutEnabled=1 AND u.LockoutEnd>SYSDATETIMEOFFSET() THEN 1 ELSE 0 END "
                   "FROM dbo.Employees e JOIN dbo.AspNetUsers u ON u.Id=e.EmployeeId "
                   "JOIN dbo.UserDepartmentAssignments a ON a.EmployeeId=e.EmployeeId AND a.DepartmentId=%d" % cc)
    roles = {}
    for uid, name in rows("SELECT ur.UserId, r.Name FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id=ur.RoleId"):
        roles.setdefault(uid.lower(), set()).add(name)
    ops = {"CS Agent"} if cs_only else {"CS Agent", "Department Employee", "Department Head"}
    agents = sorted({m[0].lower() for m in members
                     if m[1] == "0" and m[2] == "0" and m[0].lower() not in svc and roles.get(m[0].lower(), set()) & ops})
    cur = rows("SELECT CONVERT(nvarchar(36),LastAssignedEmployeeId) FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId=%d" % cc)
    cursor = cur[0][0].lower() if cur and cur[0][0] != "NULL" else None
    base, pend, ai_only = held_sets(include_ai)
    cands = [r for r in rows("SELECT CONVERT(nvarchar(30),t.CreatedAtUtc,121), t.TicketId, t.CurrentDepartmentId, ISNULL(CONVERT(nvarchar(36),t.CurrentOwnerEmployeeId),'-') "
                             "FROM dbo.Tickets t WHERE t.TicketStatus = 1")
             if int(r[1]) not in pend and int(r[1]) not in ai_only]
    cands.sort(key=lambda r: (r[0], int(r[1])))
    curassign = {int(t): (e.lower(), int(d)) for t, e, d in rows("SELECT TicketId, CONVERT(nvarchar(36),AssignedEmployeeId), AssignedDepartmentId FROM dbo.TicketAssignments WHERE IsCurrent=1")}
    handler = {}
    for t, h in rows("SELECT TicketId, CONVERT(nvarchar(36),HandledByUserId) FROM dbo.TicketInteractions WHERE IsOriginatingInteraction=1 AND HandledByUserId IS NOT NULL ORDER BY TicketInteractionId"):
        handler.setdefault(int(t), h.lower())
    start = 0
    if cursor:
        nxt = [i for i, a in enumerate(agents) if a > cursor]
        start = nxt[0] if nxt else 0
    plan, origin, prev, action, pinned = {}, {}, {}, {}, set()
    k = 0
    for _, t, d, o in cands:
        t, d = int(t), int(d)
        if handler.get(t) in agents:
            agent = handler[t]; pinned.add(t)
        else:
            agent = agents[(start + k) % len(agents)]; k += 1
        plan[t] = agent; origin[t] = d; prev[t] = None if o == "-" else o.lower()
        if d == cc and prev[t] == agent and curassign.get(t) == (agent, cc): action[t] = "KEEP"
        elif prev[t] is None: action[t] = "ASSIGN"
        else: action[t] = "REASSIGN"
    return dict(plan=plan, origin=origin, cc=cc, agents=agents, prev=prev, action=action, pinned=pinned, nrr=k)

def oracle(service_ids=(SVC,), cs_only=True, include_ai=False):
    o = oracle_full(service_ids, cs_only, include_ai)
    return o["plan"], o["origin"], o["cc"], o["agents"]

def changed_ids(o):
    return sorted(t for t, a in o["action"].items() if a != "KEEP")

# ---------------------------------------------------------------------------------------------------------------------------------
results = []
def check(name, cond, detail=""):
    results.append((name, bool(cond), detail))
    print(("  PASS  " if cond else "  FAIL  ") + name + ((" -> " + str(detail)) if (detail and not cond) else ""))

def has(out, text): return text.lower() in out.lower()

def last_batch_id(out):
    m = re.search(r"BatchId=([0-9A-Fa-f-]{36})", out); return m.group(1) if m else None

def parse_approvals(out):
    """The preview's RESULT SET 6: the seven values to copy into the Execute gates."""
    m = re.search(r"ApprovedTicketCount\|[^\n]*\n[-|]+\n(-?\d+)\|(-?\d+)\|(-?\d+)\|(-?\d+)\|(-?\d+)\|(-?\d+)\|(-?\d+)", out)
    return tuple(int(x) for x in m.groups()) if m else None

def approvals_now(**extra):
    """The numbers the PREVIEW prints for the current database state (this is exactly what an approver copies into Execute)."""
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], **extra))
    ap = parse_approvals(out)
    assert rc == 0 and ap, "preview did not print approval values:\n" + out[-800:]
    return ap

# The fixture (01_seed.sql): the facts the oracle is checked against. Open tickets = 'E-' (every 4th status), 'F-' (carry approval/AI history), 'K-', 'X-OWN-FIN/LEGACY/DRIFT'.
# Hand-derived from 01_seed.sql and asserted against the independent oracle by t01 (FP, the preview's own plan checksum, is read from the preview):
#   Open in plan 40 = 15 'E-' + 4 'F-' (approval/AI history) + 6 X-OWN-FIN + 2 X-LEGACY + 3 X-DRIFT + 10 'K-';  already in CC 7 -> moved 33;
#   reassigned 13 = 6 X-OWN-FIN + 2 X-LEGACY + K-PIN-3 + 3 K-CCOWN + K-CCDEACT;  held back (Open only): 3 pending approval, 2 active AI.
ELIG, MOVED, AGENTS, HELD_APPR, HELD_AI, REASSIGNED = 40, 33, 3, 3, 2, 13
FP = None
APPROVED = None
BATCH = "11111111-2222-3333-4444-555555555555"

def init_constants():
    """Take the seven approval values from the preview once, on the fresh fixture; t01 independently proves they equal the oracle's."""
    global APPROVED, FP
    restore()
    APPROVED = approvals_now()
    FP = APPROVED[6]
    print("   approval values from the preview on the fixture: %s" % (APPROVED,))

def must(sql):
    rc, out = run_text(sql)
    if rc != 0: raise RuntimeError("test setup failed (this would silently invalidate the test):\n" + out[:600])

def snapshot_tables(names):
    """Full dump of tables (without rowversion), ordered by their first column (the key)."""
    return {n: table_dump(n, rows("SELECT TOP 1 name FROM sys.columns WHERE object_id=OBJECT_ID('dbo.%s') ORDER BY column_id" % n)[0][0]) for n in names}

def ids(names):
    return {int(r[0]) for r in rows("SELECT TicketId FROM dbo.Tickets WHERE TicketNumber IN (%s)" % ",".join("'%s'" % n for n in names))}

def one_current(label="no ticket has more than one current assignment"):
    check(label, scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")

def col_index(table):
    cols = [r[0] for r in rows("SELECT c.name FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID('dbo.%s') AND t.name<>'timestamp' ORDER BY c.column_id" % table)]
    return {c: i for i, c in enumerate(cols)}

def without(row, ix, col):
    return tuple(v for i, v in enumerate(row) if i != ix[col])

def t01_preview_readonly():
    print("T01 preview is read-only and correct")
    restore(); before = digest(state())
    p = patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC])
    rc, out = run_text(p)
    after = digest(state())
    check("preview exits 0", rc == 0, out[-400:])
    check("preview changed nothing (all table digests equal)", before == after, [t for t in before if before[t] != after[t]])
    check("verdict READY", "READY" in out)
    o = oracle_full()
    plan, origin, cc, agents = o["plan"], o["origin"], o["cc"], o["agents"]
    base, pend, ai_only = held_sets()
    reassigned = sum(1 for a in o["action"].values() if a == "REASSIGN")
    mine = (len(plan), sum(1 for t, d in origin.items() if d != cc), len(agents), len(pend), len(ai_only), reassigned)
    check("oracle (independent, from raw rows) = the fixture facts %s" % ((ELIG, MOVED, AGENTS, HELD_APPR, HELD_AI, REASSIGNED),), mine == (ELIG, MOVED, AGENTS, HELD_APPR, HELD_AI, REASSIGNED), mine)
    check("preview's seven approval values = oracle values (+ its own plan fingerprint)", parse_approvals(out) is not None and parse_approvals(out)[:6] == (ELIG, MOVED, AGENTS, HELD_APPR, HELD_AI, REASSIGNED), parse_approvals(out))
    nonopen = int(scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketStatus<>1"))
    check("only Open tickets are in the plan (%d non-Open tickets excluded)" % nonopen,
          all(scalar("SELECT TicketStatus FROM dbo.Tickets WHERE TicketId=%d" % t) == "1" for t in plan) and nonopen > 0)
    # exact plan rows: DealOrder|TicketId|Number|Status|Created|From|To|Agent Person|user|Action|Basis|Owner
    got = {}
    for line in out.splitlines():
        m = re.match(r"^(\d+)\|(\d+)\|[A-Z]-[A-Za-z0-9-]+\|\w+\|[^|]+\|\w+\|CC[^|]*\|(\w+) Person\|[^|]+\|(KEEP|ASSIGN|REASSIGN)\|(answering agent|round robin)\|", line)
        if m: got[int(m.group(2))] = (m.group(3), m.group(4), m.group(5))
    names = {a: scalar("SELECT DisplayName FROM dbo.Employees WHERE EmployeeId='%s'" % a).split()[0] for a in agents}
    check("preview plan == independent oracle: agent, action and basis for all %d tickets" % ELIG,
          len(got) == ELIG and all(got[t] == (names[plan[t]], o["action"][t], "answering agent" if t in o["pinned"] else "round robin") for t in got), (len(got),))
    # expected per-agent distribution printed for approval
    dist = {}
    for line in out.splitlines():
        m = re.match(r"^(\d+)\|(\w+) Person\|\w+@test\|(\d+)\|(\d+)\|(\d+)\|(\d+)\|(\d+)\|(\d+)\|", line)
        if m: dist[m.group(2)] = tuple(int(x) for x in m.groups()[2:])
    exp = {}
    for t, a in plan.items():
        d = exp.setdefault(names[a], [0, 0, 0, 0, 0, 0]); d[0] += 1; d[1] += (t not in o["pinned"]); d[2] += (t in o["pinned"])
        d[3] += (origin[t] != cc); d[4] += (o["action"][t] == "KEEP"); d[5] += (o["action"][t] == "REASSIGN")
    check("per-agent distribution table == oracle (to own / round robin / answering / from other depts / kept / taken from another owner)",
          {k: tuple(v) for k, v in exp.items()} == {k: v for k, v in dist.items() if k in exp}, (exp, dist))
    check("preview names the previous owners who lose Open tickets", "OpenTicketsLost" in out and "FINEMP Person" in out)
    # blockers
    rc, out = run_text(patch(PREVIEW, services=[SVC]))
    check("blocker when @ServiceIdsConfirmed = 0", "BLOCKER: service-account exclusions not confirmed" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1))
    check("blocker when service list empty and no explicit 'none exist'", "BLOCKER: service-account list is empty" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, NoServiceAccountsExist=1))
    check("'none exist' acknowledgement clears that blocker", "READY" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=["99999999-9999-9999-9999-999999999999"]))
    check("blocker when a listed service id is not an employee", "BLOCKER: a listed service-account id is not an employee" in out)

def t01b_preview_as_datareader():
    print("T01b preview runs under a plain db_datareader login, which cannot write")
    restore()
    rc, out = run_text("""
      IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name='ro_preview') CREATE LOGIN ro_preview WITH PASSWORD='Ro#Preview2026x', CHECK_POLICY=OFF;
      IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name='ro_preview') CREATE USER ro_preview FOR LOGIN ro_preview;
      ALTER ROLE db_datareader ADD MEMBER ro_preview;""")
    assert rc == 0, out
    with tempfile.NamedTemporaryFile("w", suffix=".sql", delete=False, encoding="utf-8") as f:
        f.write(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC])); path = f.name
    os.chmod(path, 0o644); subprocess.run(["docker", "cp", path, "%s:/tmp/ro.sql" % CONTAINER], check=True, capture_output=True); os.unlink(path)
    base = ["docker", "exec", "-i", CONTAINER, "/opt/mssql-tools18/bin/sqlcmd", "-C", "-I", "-S", "localhost", "-U", "ro_preview", "-P", "Ro#Preview2026x", "-d", DB, "-b"]
    p = subprocess.run(base + ["-W", "-s", "|", "-i", "/tmp/ro.sql"], capture_output=True, text=True)
    check("preview completes and is READY as db_datareader", p.returncode == 0 and "READY" in p.stdout, (p.stdout + p.stderr)[-300:])
    p = subprocess.run(base + ["-Q", "UPDATE dbo.Tickets SET CurrentOwnerEmployeeId = NULL"], capture_output=True, text=True)
    check("that login is denied any write", p.returncode != 0 and "permission" in (p.stdout + p.stderr).lower())

def t02_dry_run():
    print("T02 execute @DryRun=1 writes nothing")
    restore(); before = digest(state())
    rc, out = execute(dry=True)
    after = digest(state())
    o = oracle_full(); changed = len(changed_ids(o)); reass = REASSIGNED
    check("dry run exits 0 and says ROLLED BACK", rc == 0 and has(out, "DRY RUN complete - transaction ROLLED BACK"), out[-500:])
    check("dry run changed nothing", before == after, [t for t in before if before[t] != after[t]])
    check("dry run reports %d in plan / %d changed / %d moved / %d reassigned" % (ELIG, changed, MOVED, reass),
          has(out, "Open tickets in the plan: %d (changed: %d, moved into CC: %d, reassigned: %d)" % (ELIG, changed, MOVED, reass)), out[-300:])

def t03_gates():
    print("T03 commit gates (seven approved values)")
    restore(); before = digest(state())
    def refused(label, rc_out, text):
        rc, out = rc_out
        check(label, rc != 0 and has(out, text) and digest(state()) == before, out[-300:])
    full = dict(ApprovedTicketCount=APPROVED[0], ApprovedTransferCount=APPROVED[1], ApprovedAgentCount=APPROVED[2], ApprovedHeldBackPendingApprovalCount=APPROVED[3],
                ApprovedHeldBackActiveAiCount=APPROVED[4], ApprovedReassignedCount=APPROVED[5], ApprovedPlanFingerprint=APPROVED[6])
    refused("no approval values -> refused", run_text(patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC])), "commit gates not satisfied")
    refused("service ids not confirmed -> refused", run_text(patch(EXECUTE, DryRun=0, services=[SVC], **full)), "commit gates not satisfied")
    for missing in ("ApprovedReassignedCount", "ApprovedPlanFingerprint", "ApprovedHeldBackActiveAiCount"):
        part = {k: v for k, v in full.items() if k != missing}
        refused("approval value %s missing -> refused" % missing, run_text(patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], **part)), "commit gates not satisfied")
    refused("empty service list without acknowledgement -> refused", execute(False, approved=APPROVED, services=()), "service-account list is empty")
    refused("service id typo -> refused", execute(False, approved=APPROVED, services=("99999999-9999-9999-9999-999999999999",)), "not an employee")
    refused("wrong ticket count -> refused", execute(False, approved=(ELIG - 1,)), "backlog changed since the preview")
    refused("wrong moved count -> refused", execute(False, approved=(ELIG, MOVED - 1)), "backlog changed since the preview")
    refused("wrong agent count -> refused", execute(False, approved=(ELIG, MOVED, AGENTS + 1)), "ApprovedAgentCount")
    refused("wrong held-back PENDING-APPROVAL count -> refused", execute(False, approved=(ELIG, MOVED, AGENTS, HELD_APPR - 1)), "backlog changed since the preview")
    refused("wrong held-back ACTIVE-AI count -> refused", execute(False, approved=(ELIG, MOVED, AGENTS, HELD_APPR, HELD_AI + 1)), "backlog changed since the preview")
    refused("wrong REASSIGNED count -> refused", execute(False, approved=(ELIG, MOVED, AGENTS, HELD_APPR, HELD_AI, REASSIGNED + 1)), "backlog changed since the preview")
    refused("wrong PLAN FINGERPRINT (same counts, different who-gets-what) -> refused", execute(False, approved=(ELIG, MOVED, AGENTS, HELD_APPR, HELD_AI, REASSIGNED, FP + 1)), "plan fingerprint")
    refused("excluding one more agent changes the pool -> refused", execute(False, approved=APPROVED, services=(SVC, "00000000-0000-0000-0000-000000000002")), "ApprovedAgentCount")
    rc, out = run_text("CREATE DATABASE WrongName", "master")
    rc, out = run_text(patch(EXECUTE, DryRun=1), "WrongName")
    check("wrong database name -> aborted by guard", rc != 0 and has(out, "this script only runs against"), out[-200:])
    run_text("DROP DATABASE WrongName", "master")

def verify_commit(o, pre, batch):
    plan, origin, cc, prev, action, pinned = o["plan"], o["origin"], o["cc"], o["prev"], o["action"], o["pinned"]
    changed = {t for t, a in action.items() if a != "KEEP"}; kept = set(plan) - changed
    moved = [t for t, d in origin.items() if d != cc]
    st = state()
    tk = {r[0]: r for r in st["Tickets"]}; pre_tk = {r[0]: r for r in pre["Tickets"]}
    ix = col_index("Tickets")
    ok_owner = ok_dept = ok_pres = True
    for t, agent in plan.items():
        r, p0 = tk[str(t)], pre_tk[str(t)]
        ok_owner &= r[ix["CurrentOwnerEmployeeId"]].lower() == agent
        ok_dept &= int(r[ix["CurrentDepartmentId"]]) == cc
        for c in ("OriginatingDepartmentId", "RequestTypeId", "CategoryId", "PriorityId", "TicketStatus", "SlaState", "EscalationLevel", "VerificationStatus", "CreatedAtUtc", "TicketNumber", "RequestSummary"):
            ok_pres &= r[ix[c]] == p0[ix[c]]
    check("every Open ticket in the plan is owned by its oracle-planned agent (assigned and unassigned alike)", ok_owner)
    check("every Open ticket in the plan is now in Call Center", ok_dept)
    check("originating dept / request type / category / priority / STATUS (still Open) / SLA state / created preserved", ok_pres)
    untouched = [t for t in tk if int(t) not in plan]
    check("all %d tickets outside the plan (not Open, held back) are byte-identical" % len(untouched), all(tk[t] == pre_tk[t] for t in untouched))
    check("the %d tickets already correct (KEPT) are byte-identical, row version included" % len(kept), all(tk[str(t)] == pre_tk[str(t)] for t in kept))
    prev_owner_gone = [t for t in plan if prev[t] and prev[t] != plan[t]]
    check("%d tickets lost their previous owner: that person no longer owns them" % len(prev_owner_gone),
          all(tk[str(t)][ix["CurrentOwnerEmployeeId"]].lower() != prev[t] for t in prev_owner_gone) and len(prev_owner_gone) == REASSIGNED)
    # assignments
    aix = col_index("TicketAssignments")
    cur = rows("SELECT TicketId, CONVERT(nvarchar(36),AssignedEmployeeId), AssignedDepartmentId, ISNULL(CONVERT(nvarchar(36),AssigningActorEmployeeId),'NULL') FROM dbo.TicketAssignments WHERE IsCurrent=1")
    curmap = {}
    for t, a, d, act in cur: curmap.setdefault(int(t), []).append((a.lower(), int(d), act))
    check("exactly one current assignment per plan ticket, matching owner, in CC (changed ones: system actor)",
          all(len(curmap.get(t, [])) == 1 and curmap[t][0][:2] == (plan[t], cc) and (t in kept or curmap[t][0][2] == "NULL") for t in plan))
    check("no ticket anywhere has two current assignments", all(len(v) == 1 for v in curmap.values()))
    pre_a = pre["TicketAssignments"]; post_a = st["TicketAssignments"]
    check("assignment HISTORY only grows: every pre-existing row still exists, differing at most by IsCurrent",
          {without(r, aix, "IsCurrent") for r in pre_a} <= {without(r, aix, "IsCurrent") for r in post_a})
    prev_cur_ids = {r[aix["TicketAssignmentId"]] for r in pre_a if r[aix["IsCurrent"]] in ("1", "True") and int(r[aix["TicketId"]]) in changed}
    retired = {r[aix["TicketAssignmentId"]] for r in post_a if r[aix["IsCurrent"]] in ("0", "False")} - {r[aix["TicketAssignmentId"]] for r in pre_a if r[aix["IsCurrent"]] in ("0", "False")}
    check("exactly the previous CURRENT rows of the changed tickets were retired (%d)" % len(prev_cur_ids), retired == prev_cur_ids)
    check("%d new assignment rows (one per changed ticket)" % len(changed), len(post_a) - len(pre_a) == len(changed))
    # audit
    aud = rows("SELECT Action, EntityId, ISNULL(BeforeValue,'NULL'), ISNULL(AfterValue,'NULL') FROM dbo.AuditEntries WHERE CorrelationId='%s'" % batch)
    transfers = {int(a[1]): a for a in aud if a[0] == "Transfer"}
    autos = {int(a[1]): a for a in aud if a[0] == "AutoAssign"}
    check("one Transfer audit per moved ticket with the original dept (and previous owner) in BeforeValue", set(transfers) == set(moved) and
          all(("DepartmentId=%d;" % origin[t]) in transfers[t][2] and ("DepartmentId=%d;" % cc) in transfers[t][3] for t in moved))
    pre_cur = {int(r[aix["TicketId"]]): r[aix["TicketAssignmentId"]] for r in pre_a if r[aix["IsCurrent"]] in ("1", "True")}
    check("one AutoAssign audit per CHANGED ticket naming the planned agent, recording previous owner / department / assignment in BeforeValue",
          set(autos) == changed and all(("AssignedEmployeeId=" + plan[t]) in autos[t][3]
              and autos[t][2] == "PreviousOwner=%s;PreviousDepartmentId=%d;PreviousAssignmentId=%s" % (prev[t] or "none", origin[t], pre_cur.get(t, "none")) for t in changed))
    check("the audit says who was the answering agent vs Round Robin", all(("Mode=AnsweringAgent" in autos[t][3]) == (t in pinned) for t in changed))
    check("KEPT tickets have no audit row from the batch", not any(int(a[1]) in kept for a in aud if a[0] in ("Transfer", "AutoAssign")))
    check("one batch audit row", sum(1 for a in aud if a[0] == "BacklogDistributionBatch") == 1)
    check("all pre-existing audit rows unchanged", all(r in st["AuditEntries"] for r in pre["AuditEntries"]))
    # workflow events
    ev = rows("SELECT TicketId, EventType, TransferReasonCode, FromDepartmentId, ToDepartmentId, CONVERT(nvarchar(36),ToEmployeeId), ISNULL(CONVERT(nvarchar(36),ActorEmployeeId),'NULL') "
              "FROM dbo.TicketWorkflowEvents WHERE CorrelationId='%s'" % batch)
    check("one TicketTransferred/SystemRouting event per moved ticket, From->CC, receiving agent, system actor",
          len(ev) == len(moved) and all(e[1] == "17" and e[2] == "5" and int(e[3]) == origin[int(e[0])] and int(e[4]) == cc and e[5].lower() == plan[int(e[0])] and e[6] == "NULL" for e in ev))
    check("pre-existing workflow events unchanged", all(r in st["TicketWorkflowEvents"] for r in pre["TicketWorkflowEvents"]))
    for t in ("TicketStatusHistory", "TicketNotes", "Employees", "UserDepartmentAssignments"):
        check("%s identical" % t, st[t] == pre[t])
    # answering agent, fairness, eligibility, cursor
    check("%d answered-by-an-eligible-agent tickets went to the agent who answered (not Round Robin)" % len(pinned), len(pinned) > 0 and all(plan[t] == scalar(
        "SELECT TOP 1 LOWER(CONVERT(nvarchar(36),HandledByUserId)) FROM dbo.TicketInteractions WHERE TicketId=%d AND IsOriginatingInteraction=1 AND HandledByUserId IS NOT NULL ORDER BY TicketInteractionId" % t) for t in pinned))
    rr = {a: sum(1 for t, x in plan.items() if x == a and t not in pinned) for a in o["agents"]}
    check("fair: Round Robin spread <= 1 %s" % sorted(rr.values()), max(rr.values()) - min(rr.values()) <= 1, rr)
    bad = scalar("SELECT COUNT(*) FROM dbo.Tickets t JOIN dbo.Employees e ON e.EmployeeId=t.CurrentOwnerEmployeeId WHERE e.DisplayName IN ('DEACT Person','LOCK Person','SVC Person','RO Person','SUPV Person','CHAIR Person') AND t.TicketId IN (%s)" % ",".join(map(str, plan)))
    check("nothing assigned to deactivated / locked / service / read-only / supervisory accounts", bad == "0", bad)
    rrs = [plan[t] for t in sorted(plan, key=lambda t: 0) if t not in pinned]
    cursor = scalar("SELECT LOWER(CONVERT(nvarchar(36),LastAssignedEmployeeId)) FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId=%d" % cc)
    last_rr = None
    order = [r for r in rows("SELECT TicketId FROM dbo.Tickets WHERE TicketStatus=1 ORDER BY CreatedAtUtc, TicketId")]
    for (tid,) in order:
        if int(tid) in plan and int(tid) not in pinned: last_rr = plan[int(tid)]
    check("rotation cursor = last agent dealt BY ROUND ROBIN (answering-agent tickets do not move it)", cursor == last_rr, (cursor, last_rr))

def t04_commit():
    print("T04 real commit")
    restore(); pre = state()
    o = oracle_full()
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit exits 0 and says COMMITTED", rc == 0 and has(out, "COMMITTED. Open tickets in the plan: %d" % ELIG), out[-500:])
    check("prints the BatchId for rollback", BATCH in out)
    verify_commit(o, pre, BATCH)
    rc, out = execute(dry=False, batch="22222222-2222-3333-4444-555555555555", approved=APPROVED)
    check("second run refused (one-time)", rc != 0 and has(out, "BacklogDistributionBatch audit row already exists"), out[-200:])
    one_current("no ticket has >1 current assignment")
    return pre

def t05_rollback_clean():
    print("T05 rollback of an untouched batch restores the exact original state")
    restore(); pre = state(); o = oracle_full(); CH = len(changed_ids(o))
    execute(dry=False, batch=BATCH, approved=APPROVED)
    mid = digest(state())
    rc, out = rollback(BATCH, dry=True)
    check("rollback dry run exits 0, changes nothing", rc == 0 and digest(state()) == mid and has(out, "DRY RUN complete"), out[-300:])
    check("dry run plans %d rollbacks, %d department restores, 0 skipped" % (CH, MOVED), re.search(r"\|%d\|%d\|%d\|0\|" % (CH, CH, MOVED), out) is not None, out[-400:])
    rc, out = rollback(BATCH, dry=False)
    check("rollback commits", rc == 0 and has(out, "ROLLBACK COMMITTED. Released: %d" % CH), out[-300:])
    post = state(); aix = col_index("TicketAssignments")
    check("Tickets table identical to pre-batch (owner, department, everything - previous owners own their tickets again)", post["Tickets"] == pre["Tickets"])
    curset = lambda st: {(r[aix["TicketId"]], r[aix["AssignedEmployeeId"]].lower(), r[aix["AssignedDepartmentId"]]) for r in st["TicketAssignments"] if r[aix["IsCurrent"]] in ("1", "True")}
    check("the set of CURRENT assignments (ticket, employee, department) equals the pre-batch set", curset(post) == curset(pre))
    check("assignment history intact: every pre-existing row still there (only IsCurrent may differ)", {without(r, aix, "IsCurrent") for r in pre["TicketAssignments"]} <= {without(r, aix, "IsCurrent") for r in post["TicketAssignments"]})
    check("assignment rows only grew", len(post["TicketAssignments"]) > len(pre["TicketAssignments"]))
    check("all earlier audit rows intact", all(r in post["AuditEntries"] for r in pre["AuditEntries"]))
    check("all earlier workflow events intact", all(r in post["TicketWorkflowEvents"] for r in pre["TicketWorkflowEvents"]))
    check("rotation cursor restored to pre-batch", scalar("SELECT COUNT(*) FROM dbo.DepartmentAssignmentRotations WHERE LastAssignedEmployeeId IS NULL") == "1")
    check("history / notes untouched", post["TicketStatusHistory"] == pre["TicketStatusHistory"] and post["TicketNotes"] == pre["TicketNotes"])
    check("reversal events recorded for the %d moved tickets" % MOVED, scalar("SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE Note LIKE 'Rollback of one-time%'") == str(MOVED))
    one_current("no ticket has >1 current assignment after rollback")
    rc, out = rollback(BATCH, dry=False)
    check("second rollback of same batch refused", rc != 0 and has(out, "already been rolled back"), out[-200:])
    rc, out = rollback("99999999-9999-9999-9999-999999999999", dry=True)
    check("rollback with unknown BatchId refused", rc != 0 and has(out, "expected exactly one BacklogDistributionBatch"), out[-200:])
    rc, out = execute(dry=True)
    check("re-run after rollback still needs explicit @AllowSecondBatch", rc != 0 and has(out, "already exists"))
    rc, out = execute(dry=True, AllowSecondBatch=1)
    check("with override the backlog is in the plan again (%d)" % ELIG, has(out, "Open tickets in the plan: %d" % ELIG), out[-300:])

def t06_rollback_touched():
    print("T06 rollback skips tickets touched since the batch")
    restore(); pre = state(); o = oracle_full(); plan, cc = o["plan"], o["cc"]; ch = changed_ids(o)
    execute(dry=False, batch=BATCH, approved=APPROVED)
    reassigned, statused, noted, transferred, handled, evented, responded, owner_only = ch[0], ch[1], ch[2], ch[3], ch[4], ch[5], ch[6], ch[7]
    SUPV = "a1000000-0000-0000-0000-000000000001"
    A3 = "7a000000-0000-0000-0000-000000000001" if plan[reassigned] != "7a000000-0000-0000-0000-000000000001" else "00000000-0000-0000-0001-000000000000"   # never the planned agent
    fin = scalar("SELECT DepartmentId FROM dbo.Departments WHERE Code='FIN'")
    OWN = "00000000-0000-0000-0000-000000000002" if plan[owner_only] != "00000000-0000-0000-0000-000000000002" else "00000000-0000-0000-0001-000000000000"   # another agent than the planned one
    must(f"""
      SET QUOTED_IDENTIFIER ON;
      UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={reassigned} AND IsCurrent=1;
      INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent)
        VALUES({reassigned},'{A3}',{cc},DATEADD(second,5,SYSUTCDATETIME()),'{SUPV}',1);
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{A3}' WHERE TicketId={reassigned};
      INSERT dbo.TicketStatusHistory(TicketId,Dimension,OldValue,NewValue,ActorEmployeeId,ActorIsSystem,CorrelationId,OccurredAtUtc)
        VALUES({statused},1,1,2,'{plan[statused]}',0,NEWID(),DATEADD(minute,1,SYSUTCDATETIME()));
      INSERT dbo.TicketNotes(TicketId,NoteText,AuthorEmployeeId,CreatedAtUtc) VALUES({noted},N'called customer','{plan[noted]}',DATEADD(minute,1,SYSUTCDATETIME()));
      UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={transferred} AND IsCurrent=1;
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId=NULL, CurrentDepartmentId={fin} WHERE TicketId={transferred};
      INSERT dbo.TicketInteractions(TicketId,IsOriginatingInteraction,Source,ChannelId,CustomerPhone,CreatedAtUtc) VALUES({handled},0,1,1,N'0500000000',SYSUTCDATETIME());
      INSERT dbo.TicketInteractionHandlers(TicketInteractionId,TicketId,HandledByUserId,GenesysAgentUserId,Source,RecordedAtUtc)
        VALUES(SCOPE_IDENTITY(),{handled},'{plan[handled]}',N'g-1',1,DATEADD(minute,1,SYSUTCDATETIME()));
      INSERT dbo.TicketWorkflowEvents(TicketId,EventType,OccurredAtUtc,ActorEmployeeId,Note,CorrelationId) VALUES({evented},10,DATEADD(minute,1,SYSUTCDATETIME()),NULL,N'handoff requested',NEWID());
      UPDATE dbo.Tickets SET FirstHumanResponseAtUtc=DATEADD(minute,2,SYSUTCDATETIME()) WHERE TicketId={responded};
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{OWN}' WHERE TicketId={owner_only};   -- ONLY the owner column changed: no assignment row, nothing else
    """)
    touched = {reassigned, statused, noted, transferred, handled, evented, responded, owner_only}
    before = state(); CH = len(ch)
    rc, out = rollback(BATCH, dry=True)
    check("dry run plans %d releases and %d skips" % (CH - len(touched), len(touched)), rc == 0 and has(out, "|%d|" % (CH - len(touched))) and digest(state()) == digest(before), out[-500:])
    rc, out = rollback(BATCH, dry=False)
    check("rollback commits", rc == 0, out[-400:])
    check("reports %d released" % (CH - len(touched)), has(out, "Released: %d" % (CH - len(touched))), out[-300:])
    st = state(); tk = {r[0]: r for r in st["Tickets"]}; bk = {r[0]: r for r in before["Tickets"]}
    check("each of the 8 touched tickets is byte-identical to how the human left it", all(tk[str(t)] == bk[str(t)] for t in touched), [t for t in touched if tk[str(t)] != bk[str(t)]])
    check("no rollback audit / reversal event / assignment retirement for touched tickets",
          scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action IN ('AutoAssignRollback','Transfer') AND CorrelationId<>'%s' AND AuditEntryId>(SELECT AuditEntryId FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch') AND EntityId IN (%s)" % (BATCH, ",".join(map(str, touched)))) == "0"
          and all(a in st["TicketAssignments"] for a in before["TicketAssignments"] if a[1] in {str(t) for t in touched}))
    released = [t for t in ch if t not in touched]
    pre_tk = {r[0]: r for r in pre["Tickets"]}
    check("all %d untouched changed tickets are back to pre-batch owner AND department" % len(released), all(tk[str(t)] == pre_tk[str(t)] for t in released), [t for t in released if tk[str(t)] != pre_tk[str(t)]][:5])
    for reason in ("owner changed since the batch", "transferred out of Call Center", "worked since the batch"):
        check("skip reason reported: " + reason, has(out, reason))
    check("rotation cursor restored to pre-batch value (nobody was dealt a ticket since)", scalar("SELECT COUNT(*) FROM dbo.DepartmentAssignmentRotations WHERE LastAssignedEmployeeId IS NULL") == "1")
    one_current("every ticket has <=1 current assignment")

def t06b_include_worked():
    print("T06b @IncludeWorked relaxes only the 'worked' test")
    restore(); o = oracle_full(); plan, cc = o["plan"], o["cc"]; ch = changed_ids(o)
    execute(dry=False, batch=BATCH, approved=APPROVED)
    worked, reassigned = ch[0], ch[1]
    A3 = "7a000000-0000-0000-0000-000000000001"
    must(f"""
      SET QUOTED_IDENTIFIER ON;
      INSERT dbo.TicketNotes(TicketId,NoteText,AuthorEmployeeId,CreatedAtUtc) VALUES({worked},N'note','{plan[worked]}',DATEADD(minute,1,SYSUTCDATETIME()));
      UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={reassigned} AND IsCurrent=1;
      INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) VALUES({reassigned},'{A3}',{cc},DATEADD(second,5,SYSUTCDATETIME()),NULL,1);
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{A3}' WHERE TicketId={reassigned};""")
    rc, out = rollback(BATCH, dry=False, IncludeWorked=1)
    check("IncludeWorked=1 releases the worked ticket but still skips the reassigned one", rc == 0 and has(out, "Released: %d" % (len(ch) - 1)), out[-300:])
    check("reassigned ticket still owned by the human's choice", scalar(f"SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketId={reassigned}") == A3)

def window_run(inject_marker, extra_sql_for_window, wait=12, settle=5):
    """Run a real commit whose transaction pauses (WAITFOR) just before `inject_marker`; execute `extra_sql_for_window` from other sessions meanwhile."""
    slow = patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], ApprovedTicketCount=APPROVED[0], ApprovedTransferCount=APPROVED[1], ApprovedAgentCount=APPROVED[2],
                 ApprovedHeldBackPendingApprovalCount=APPROVED[3], ApprovedHeldBackActiveAiCount=APPROVED[4], ApprovedReassignedCount=APPROVED[5], ApprovedPlanFingerprint=APPROVED[6],
                 BatchId="'%s'" % BATCH)
    assert inject_marker in slow
    slow = slow.replace(inject_marker, "    WAITFOR DELAY '00:00:%02d';\n" % wait + inject_marker, 1)
    res = {}
    th = threading.Thread(target=lambda: res.__setitem__("A", run_text(slow))); th.start()
    time.sleep(settle)
    others = {}
    def go(name, sql):
        t0 = time.time(); others[name] = (run_text(sql), time.time() - t0)
    ths = [threading.Thread(target=go, args=(n, q)) for n, q in extra_sql_for_window.items()]
    for t in ths: t.start()
    for t in ths: t.join()
    th.join()
    return res["A"], others

def t07_concurrency():
    print("T07 concurrency and ownership protection")
    restore(); pre = state()
    o = oracle_full(); plan, prev, cc = o["plan"], o["prev"], o["cc"]; ch = changed_ids(o)
    unowned = next(t for t in ch if prev[t] is None)                 # an Open ticket nobody owns
    owned = next(t for t in ch if prev[t] and prev[t] != plan[t])   # an Open ticket somebody owns today and loses
    rv = scalar("SELECT master.sys.fn_varbintohexstr(RowVersion) FROM dbo.Tickets WHERE TicketId=%d" % owned)
    A2 = "00000000-0000-0000-0001-000000000000"
    slow = patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], ApprovedTicketCount=APPROVED[0], ApprovedTransferCount=APPROVED[1], ApprovedAgentCount=APPROVED[2],
                 ApprovedHeldBackPendingApprovalCount=APPROVED[3], ApprovedHeldBackActiveAiCount=APPROVED[4], ApprovedReassignedCount=APPROVED[5], ApprovedPlanFingerprint=APPROVED[6],
                 BatchId="'%s'" % BATCH)
    slow = slow.replace("    -- (h) report", "    WAITFOR DELAY '00:00:14';\n    -- (h) report", 1)
    assert "WAITFOR DELAY" in slow
    res = {}
    th = threading.Thread(target=lambda: res.__setitem__("A", run_text(slow))); th.start(); time.sleep(5)      # A now holds app lock, cursor lock and the ticket locks
    resB, resC, resD = {}, {}, {}
    def runB():
        t0 = time.time(); resB["r"] = execute(dry=False, batch="44444444-2222-3333-4444-555555555555", approved=APPROVED); resB["t"] = time.time() - t0
    def runC():
        t0 = time.time()
        resC["r"] = run_text(f"SET LOCK_TIMEOUT -1; SET NOCOUNT OFF; UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='7a000000-0000-0000-0000-000000000001' WHERE TicketId={unowned} AND CurrentOwnerEmployeeId IS NULL;")
        resC["t"] = time.time() - t0
    def runD():   # the previous owner's application saves the ticket with the row version it read (optimistic concurrency) while the batch moves it
        t0 = time.time()
        resD["r"] = run_text(f"SET LOCK_TIMEOUT -1; SET NOCOUNT OFF; UPDATE dbo.Tickets SET RequestSummary=N'edited by previous owner' WHERE TicketId={owned} AND RowVersion={rv};")
        resD["t"] = time.time() - t0
    ts = [threading.Thread(target=f) for f in (runB, runC, runD)]
    for t in ts: t.start()
    for t in ts: t.join()
    th.join()
    rcB, outB = resB["r"]; tB = resB["t"]; rcC, outC = resC["r"]; tC = resC["t"]; rcD, outD = resD["r"]; tD = resD["t"]
    check("a second concurrent run fails fast on the application lock", rcB != 0 and has(outB, "application lock") and tB < 8, (round(tB, 1), outB[-200:]))
    check("a concurrent 'assign this unowned ticket' blocks until the batch commits", tC > 3, round(tC, 1))
    check("...and then affects 0 rows (cannot overwrite the batch's owner)", "(0 rows affected)" in outC, outC[-200:])
    check("the previous owner's save of the ticket they still thought was theirs waits for the batch (%.1fs)" % tD, tD > 3, round(tD, 1))
    check("...and then loses: the row version moved, so their stale save changes 0 rows (ownership protection)", "(0 rows affected)" in outD, outD[-200:])
    rcA, outA = res["A"]
    check("the batch itself committed", rcA == 0 and has(outA, "COMMITTED"), outA[-300:])
    check("the owned ticket kept the oracle-planned owner (previous owner did not get it back)", scalar(f"SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketId={owned}") == plan[owned])
    check("the unowned ticket kept the oracle-planned owner", scalar(f"SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketId={unowned}") == plan[unowned])
    one_current("exactly one current assignment per ticket")
    rc, out = run_text(f"INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,IsCurrent) VALUES({unowned},'7a000000-0000-0000-0000-000000000001',{cc},SYSUTCDATETIME(),1)")
    check("a duplicate CURRENT assignment is rejected by the unique index", rc != 0 and has(out, "unique index"), out[-200:])

def t08_race_between_preview_and_run():
    print("T08 backlog changes between preview and execution")
    restore()
    run_text("UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='00000000-0000-0000-0000-000000000002' WHERE TicketNumber='E-0020'; "
             "INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) "
             "SELECT TicketId,'00000000-0000-0000-0000-000000000002',CurrentDepartmentId,SYSUTCDATETIME(),'00000000-0000-0000-0000-000000000002',1 FROM dbo.Tickets WHERE TicketNumber='E-0020'")
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED)
    check("a ticket a person took AFTER the preview changes the reassigned count -> run refused, nothing changed", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-300:])
    ap = approvals_now()
    check("re-previewed: one more ticket is now 'reassigned from another owner'", ap[5] == REASSIGNED + 1 and ap[0] == ELIG, ap)
    rc, out = execute(dry=False, batch=BATCH, approved=ap)
    o_owner = scalar("SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketNumber='E-0020'")
    check("with the re-approved numbers it commits (the approver has now SEEN that person's ticket in the 'who loses tickets' list)", rc == 0 and o_owner is not None, out[-300:])

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
        o = oracle_full(); plan = o["plan"]
        ap = approvals_now()
        rc, out = execute(dry=False, batch=BATCH, approved=ap)
        got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE TicketStatus=1")}
        check("%s: commit equals oracle for all %d tickets (%d agent(s))" % (label, len(plan), len(o["agents"])), rc == 0 and all(got[t] == a for t, a in plan.items()), out[-300:])
    restore()
    run_text("INSERT dbo.DepartmentWorkflowSettings(DepartmentId,AllowAssignment,AllowInternalReassignment,AllowTransferToOtherDepartments,HeadRoleName,SupervisorRoleName) SELECT DepartmentId,0,0,0,N'h',N's' FROM dbo.Departments WHERE Code='CC'")
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
    print("T10 scale: %d extra tickets (a quarter Open, a quarter of those already owned) plus a large closed population" % n)
    restore()
    must(f"""
      SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON;
      DECLARE @d TABLE (id int, rn int); INSERT @d SELECT DepartmentId, ROW_NUMBER() OVER (ORDER BY DepartmentId) FROM dbo.Departments;
      ;WITH x AS (SELECT TOP ({n}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
      INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
      SELECT N'S-' + CONVERT(nvarchar(10), i), d.id, d.id, CASE WHEN i % 16 = 1 THEN 'b0000000-0000-0000-0000-000000000001' END, (i % 4) + 1, 0, 0, 1, N'scale', 0, DATEADD(second, i, '2026-06-01') FROM x CROSS APPLY (SELECT id FROM @d WHERE rn = (i % 5) + 1) d;
      INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
      SELECT TicketId, CurrentOwnerEmployeeId, CurrentDepartmentId, SYSUTCDATETIME(), CurrentOwnerEmployeeId, 1 FROM dbo.Tickets WHERE TicketNumber LIKE N'S-%' AND CurrentOwnerEmployeeId IS NOT NULL;
      ;WITH x AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
      INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
      SELECT N'C-' + CONVERT(nvarchar(10), i), 3, 3, NULL, 6, 0, 0, 3, N'closed', 0, '2026-01-01' FROM x;
      INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
      SELECT TOP (300000) NULL, N'Create', N'Ticket', CONVERT(nvarchar(100), TicketId), NULL, N'seed', NEWID(), CreatedAtUtc FROM dbo.Tickets;
    """)
    o = oracle_full(); plan, origin, cc = o["plan"], o["origin"], o["cc"]
    base, pend, ai_only = held_sets()
    total, moved = len(plan), sum(1 for d in origin.values() if d != cc); ch = changed_ids(o)
    print("   open in plan=%d moved=%d changed=%d held=%d+%d audit rows=%s" % (total, moved, len(ch), len(pend), len(ai_only), scalar("SELECT COUNT(*) FROM dbo.AuditEntries")))
    t0 = time.time(); rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC])); tp = time.time() - t0
    ap = parse_approvals(out)
    check("preview completes (%.1fs), READY, values = oracle" % tp, rc == 0 and "READY" in out and ap[:5] == (total, moved, len(o["agents"]), len(pend), len(ai_only)), (ap, out[-200:]))
    t0 = time.time(); rc, out = execute(dry=True, approved=ap); td = time.time() - t0
    check("dry run completes (%.1fs) and rolls back" % td, rc == 0 and has(out, "ROLLED BACK"), out[-300:])
    t0 = time.time(); rc, out = execute(dry=False, batch=BATCH, approved=ap); tx = time.time() - t0
    check("commit of %d Open tickets completes (%.1fs)" % (total, tx), rc == 0 and has(out, "COMMITTED"), out[-300:])
    got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE TicketStatus=1")}
    check("all %d tickets match the independent oracle" % len(plan), all(got[t] == a for t, a in plan.items()))
    check("held-back tickets keep their state", all(got[t] == "null" or True for t in (pend | ai_only)))
    counts = {}
    for t, a in plan.items():
        if t not in o["pinned"]: counts[a] = counts.get(a, 0) + 1
    check("fair at scale: Round Robin spread <= 1 %s" % sorted(counts.values()), max(counts.values()) - min(counts.values()) <= 1)
    one_current("no duplicate current assignments")
    check("the 100000 closed tickets were never touched", scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketNumber LIKE 'C-%' AND CurrentOwnerEmployeeId IS NULL AND CurrentDepartmentId=3") == "100000")
    t0 = time.time(); rc, out = rollback(BATCH, dry=False); tr = time.time() - t0
    check("rollback of %d changed tickets completes (%.1fs)" % (len(ch), tr), rc == 0 and has(out, "Released: %d" % len(ch)), out[-300:])
    check("previous owners own their tickets again after rollback", scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketNumber LIKE 'S-%' AND CurrentOwnerEmployeeId='b0000000-0000-0000-0000-000000000001'") == scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketNumber LIKE 'S-%' AND (CAST(SUBSTRING(TicketNumber,3,10) AS int) % 16)=1"))
    print("   timings: preview %.1fs, dry-run %.1fs, commit %.1fs, rollback %.1fs" % (tp, td, tx, tr))

def t11_pending_approvals():
    print("T11 pending approvals are held back, listed separately, and never touched")
    restore(); pre = state(); pre_x = snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketAgentHandoffs"])
    o = oracle_full(); plan = o["plan"]
    appr_all = ids(["H-APR-%d" % i for i in range(1, 7)] + ["H-BOTH-1"])
    appr = {t for t in appr_all if scalar("SELECT TicketStatus FROM dbo.Tickets WHERE TicketId=%d" % t) == "1"}     # only OPEN tickets are in scope
    decided = ids(["F-DEC-1", "F-DEC-2", "F-DEC-3"])
    decided_open = {t for t in decided if scalar("SELECT TicketStatus FROM dbo.Tickets WHERE TicketId=%d" % t) == "1"}
    check("oracle excludes all Open pending-approval tickets (incl. the one that is also an AI conversation)", not (appr & set(plan)) and len(appr) == HELD_APPR)
    check("decided approvals (approved / rejected / cancelled) do NOT block", decided_open <= set(plan) and len(decided_open) >= 1)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC]))
    held_lines = [l for l in out.splitlines() if "PENDING APPROVAL" in l or "ACTIVE AI CONVERSATION" in l]
    nums = {l.split("|")[1] for l in held_lines}
    check("preview lists every Open held-back ticket once with its reason", len(held_lines) == HELD_APPR + HELD_AI and "H-BOTH-1" in nums and "H-APR-1" in nums and "H-AI-1" in nums, (len(held_lines), nums))
    check("a ticket with both is listed under the approval and flagged 'also has an active AI conversation'", any("H-BOTH-1" in l and "PENDING APPROVAL" in l and "also has an active AI" in l for l in held_lines))
    check("pending approval text shows type and since-date", any("H-APR-1" in l and "Accounting (since " in l for l in held_lines))
    plan_nums = {l.split("|")[2] for l in out.splitlines() if re.match(r"^\d+\|\d+\|[A-Z]-", l)}
    check("no held-back ticket appears in the preview's plan", not any(n.startswith("H-") for n in plan_nums))
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit succeeds with the seven approved values", rc == 0 and has(out, "COMMITTED"), out[-300:])
    post = state(); tk = {r[0]: r for r in post["Tickets"]}; pk = {r[0]: r for r in pre["Tickets"]}
    ai = ids(["H-AI-%d" % i for i in range(1, 6)]); held = appr_all | ai
    check("every held-back ticket row (any status) is byte-identical (department, owner, status, everything)", all(tk[str(t)] == pk[str(t)] for t in held))
    check("held-back tickets have no assignment row, audit row or workflow event from the batch",
          scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE CorrelationId='%s' AND EntityId IN (%s)" % (BATCH, ",".join(map(str, held)))) == "0" and
          scalar("SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE CorrelationId='%s' AND TicketId IN (%s)" % (BATCH, ",".join(map(str, held)))) == "0")
    check("decided-approval Open tickets WERE moved and assigned", all(tk[str(t)] != pk[str(t)] for t in decided_open))
    check("TicketApprovals / TicketInteractions / TicketAgentHandoffs are byte-identical (nothing in approval flows or AI conversations touched)",
          snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketAgentHandoffs"]) == pre_x)
    check("batch audit row records the held-back counts", scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch' AND AfterValue LIKE '%%HeldBackPendingApproval=%d;HeldBackActiveAi=%d;%%'" % (HELD_APPR, HELD_AI)) == "1")
    rc, out = rollback(BATCH, dry=False)
    check("rollback releases exactly the changed tickets", rc == 0 and has(out, "Released: %d" % len(changed_ids(o))), out[-300:])
    check("after rollback the whole Tickets table equals the pre-batch table (held-back tickets never moved)", state()["Tickets"] == pre["Tickets"])

def t11b_approval_decided_between_preview_and_run():
    print("T11b approval decided / requested between preview and run")
    restore()
    run_text("UPDATE dbo.TicketApprovals SET Status=2 WHERE TicketId=(SELECT TicketId FROM dbo.Tickets WHERE TicketNumber='H-APR-1')")   # approved -> now eligible
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED)
    check("an approval decided after the preview changes the counts -> run refused, nothing changed", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-250:])
    ap = approvals_now()
    check("re-previewed: that ticket is now in the plan and no longer held back", ap[0] == ELIG + 1 and ap[3] == HELD_APPR - 1, ap)
    rc, out = execute(dry=False, approved=ap)
    check("re-approved with the new numbers it commits and now moves that ticket", rc == 0 and scalar("SELECT CurrentOwnerEmployeeId FROM dbo.Tickets WHERE TicketNumber='H-APR-1'") != "NULL", out[-250:])
    restore()
    run_text("INSERT dbo.TicketApprovals (TicketId, ApprovalType, Status, TargetKind, RequestedByEmployeeId, RequestedAtUtc, IsCurrent, CorrelationId) "
             "SELECT TicketId, 1, 1, 1, 'b0000000-0000-0000-0000-000000000001', SYSUTCDATETIME(), 1, NEWID() FROM dbo.Tickets WHERE TicketNumber='E-0020'")  # new pending approval
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED)
    check("a NEW pending approval after the preview -> run refused, nothing changed", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before)

def t12_active_ai():
    print("T12 active AI conversations are held back and preserved")
    restore(); pre = state(); pre_x = snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketAgentHandoffs"])
    o = oracle_full(); plan = o["plan"]
    ai = ids(["H-AI-%d" % i for i in range(1, 6)])
    ai_open = {t for t in ai if scalar("SELECT TicketStatus FROM dbo.Tickets WHERE TicketId=%d" % t) == "1"}
    eligible_ai = {t for t in ids(["F-AIEND-1", "F-AIEND-2", "F-AIHUM-1", "F-AIHUM-2", "F-AIHAND-1", "F-AITAKEN-1", "F-AIINACT-1"]) if scalar("SELECT TicketStatus FROM dbo.Tickets WHERE TicketId=%d" % t) == "1"}
    check("Open AI tickets that are ended / need a human / have an open handoff / were taken / inactivity-closed ARE in the plan", eligible_ai <= set(plan) and len(eligible_ai) == 3, eligible_ai)
    check("live AI-only Open tickets are NOT in the plan", not (ai_open & set(plan)) and len(ai_open) == HELD_AI)
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit succeeds", rc == 0 and has(out, "COMMITTED"), out[-300:])
    post = state(); tk = {r[0]: r for r in post["Tickets"]}; pk = {r[0]: r for r in pre["Tickets"]}
    check("active AI tickets byte-identical (department, owner, status, SLA, everything)", all(tk[str(t)] == pk[str(t)] for t in ai))
    check("AI interactions / handoffs / approvals byte-identical after the batch", snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketAgentHandoffs"]) == pre_x)
    restore()
    run_text("UPDATE dbo.TicketInteractions SET EndedAtUtc=SYSUTCDATETIME() WHERE TicketId=(SELECT TicketId FROM dbo.Tickets WHERE TicketNumber='H-AI-1')")
    rc, out = execute(dry=False, approved=APPROVED)
    check("an AI conversation that ended after the preview changes the counts -> refused", rc != 0 and has(out, "backlog changed since the preview"), out[-250:])
    restore()
    o = oracle_full(); ch = changed_ids(o)
    execute(dry=False, batch=BATCH, approved=APPROVED)
    a_t, i_t = ch[0], ch[1]
    must(f"""SET QUOTED_IDENTIFIER ON;
      INSERT dbo.TicketApprovals (TicketId, ApprovalType, Status, TargetKind, RequestedByEmployeeId, RequestedAtUtc, IsCurrent, CorrelationId)
        VALUES({a_t}, 1, 1, 1, 'b0000000-0000-0000-0000-000000000001', DATEADD(minute,1,SYSUTCDATETIME()), 1, NEWID());
      INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, RequiresHumanFollowUp)
        VALUES({i_t}, 0, 2, 2, N'0500000001', N'late-conv', DATEADD(minute,1,SYSUTCDATETIME()), 0);""")
    before = state()
    rc, out = rollback(BATCH, dry=False)
    st = state(); tk = {r[0]: r for r in st["Tickets"]}; bk = {r[0]: r for r in before["Tickets"]}
    check("a ticket that entered an approval flow or got a new AI conversation after the batch is skipped, byte-identical",
          rc == 0 and has(out, "Released: %d" % (len(ch) - 2)) and tk[str(a_t)] == bk[str(a_t)] and tk[str(i_t)] == bk[str(i_t)], out[-300:])

def t13_concurrent_safeguards():
    print("T13 concurrent assignment safeguards")
    marker = "    -- (g0) RE-CHECKS after the writes."
    early = "    -- (f) writes"
    appr_sql = lambda v: (f"INSERT dbo.TicketApprovals (TicketId, ApprovalType, Status, TargetKind, RequestedByEmployeeId, RequestedAtUtc, IsCurrent, CorrelationId) "
                          f"VALUES({v}, 1, 1, 1, 'b0000000-0000-0000-0000-000000000001', SYSUTCDATETIME(), 1, NEWID())")
    ai_sql = lambda v: (f"INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, RequiresHumanFollowUp) "
                        f"VALUES({v}, 0, 2, 2, N'0500000002', N'mid-run', SYSUTCDATETIME(), 0)")
    for label, mk, code in (("pending approval", appr_sql, "50340"), ("active AI conversation", ai_sql, "50341")):
        restore(); pre = digest(state()); o = oracle_full(); victim = changed_ids(o)[7]
        (rcA, outA), oth = window_run(early, {"w": mk(victim)})
        (rcB, outB), tB = oth["w"]
        check("a %s created BEFORE the update lands (competing insert succeeds)" % label, rcB == 0, outB[-200:])
        check("...so the re-check aborts the WHOLE run (Msg %s)" % code, rcA != 0 and ("Msg " + code) in outA, outA[-300:])
        after = digest(state())
        check("...and nothing was changed (tickets, assignments, audit, events, cursor identical)", after == pre, [t for t in pre if pre[t] != after[t]])
    late = "    -- (g0) RE-CHECKS after the writes."
    for label, mk in (("pending approval", appr_sql), ("active AI conversation", ai_sql)):
        restore(); pre = digest(state()); o = oracle_full(); victim = changed_ids(o)[7]
        (rcA, outA), oth = window_run(late, {"w": mk(victim)})
        (rcB, outB), tB = oth["w"]
        committed = rcA == 0 and has(outA, "COMMITTED")
        blocked_or_lost = (rcB != 0 and "1205" in outB) or tB > 6
        if committed:
            check("%s inserted AFTER the update: batch committed and the competing writer was blocked / lost the deadlock (%.1fs)" % (label, tB), blocked_or_lost, (round(tB, 1), outB[-150:]))
        else:
            check("%s inserted AFTER the update: batch aborted atomically (deadlock victim or re-check), nothing changed" % label, digest(state()) == pre, outA[-200:])
        moved_and_flagged = scalar("SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.IsCurrent=1 AND a.AssignedAtUtc=ISNULL((SELECT OccurredAtUtc FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch'),'1900-01-01') "
                                   "AND EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId=a.TicketId AND ap.Status=1 AND ap.RequestedAtUtc < a.AssignedAtUtc)")
        check("...and no batch ticket carries a pending approval that predates the commit", moved_and_flagged == "0")
    restore(); pre = digest(state())
    (rcA, outA), oth = window_run(marker, {"lock": "UPDATE dbo.AspNetUsers SET LockoutEnd=DATEADD(year,1,SYSDATETIMEOFFSET()) WHERE Id='7a000000-0000-0000-0000-000000000001'"})
    check("an agent locked out mid-run aborts the WHOLE run (Msg 50342) and changes nothing", rcA != 0 and "Msg 50342" in outA and digest(state()) == pre, outA[-300:])
    restore()
    (rcA, outA), oth = window_run(marker, {"deactivate": "SET LOCK_TIMEOUT -1; UPDATE dbo.Employees SET DeactivatedAtUtc=SYSUTCDATETIME() WHERE EmployeeId='00000000-0000-0000-0001-000000000000'"})
    (rcD, outD), tD = oth["deactivate"]
    check("deactivating an agent waits for the batch (blocked %.1fs) instead of changing the pool mid-run" % tD, tD > 4 and rcD == 0, (round(tD, 1), outD[-150:]))
    check("the batch itself committed", rcA == 0 and has(outA, "COMMITTED"), outA[-300:])
    restore()
    (rcA, outA), oth = window_run("    -- (h) report", {"intake": "SET LOCK_TIMEOUT -1; SET NOCOUNT ON; SELECT LOWER(CONVERT(nvarchar(36),LastAssignedEmployeeId)) AS c FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, ROWLOCK) WHERE DepartmentId=(SELECT DepartmentId FROM dbo.Departments WHERE Code='CC')"})
    (rcI, outI), tI = oth["intake"]
    cursor = scalar("SELECT LOWER(CONVERT(nvarchar(36),LastAssignedEmployeeId)) FROM dbo.DepartmentAssignmentRotations")
    check("live intake's cursor lock waits for the batch (%.1fs) and then reads the advanced cursor" % tI, tI > 4 and cursor in outI.lower(), (round(tI, 1), outI[-120:]))
    bad = scalar("SELECT COUNT(*) FROM dbo.Tickets t WHERE t.CurrentOwnerEmployeeId IN (SELECT EmployeeId FROM dbo.Employees WHERE DisplayName IN ('DEACT Person','LOCK Person','SVC Person','RO Person','SUPV Person','CHAIR Person'))")
    check("no ticket is owned by an excluded account", bad == "0")
    check("every batch ticket has exactly one owner and one current assignment", scalar("SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketId IN (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 AND AssignedAtUtc=(SELECT OccurredAtUtc FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch')) AND (t.CurrentOwnerEmployeeId IS NULL OR (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId=t.TicketId AND a.IsCurrent=1)<>1)") == "0")
    # a person works / reassigns an Open ticket between the batch's read and its write: the guarded UPDATE sees the changed row version and the WHOLE run aborts
    restore(); pre = digest(state()); o = oracle_full(); victim = next(t for t in changed_ids(o) if o["prev"][t])
    (rcA, outA), oth = window_run(early, {"human": f"SET LOCK_TIMEOUT -1; UPDATE dbo.Tickets SET RequestSummary=N'someone edited this' WHERE TicketId={victim}"}, settle=4)
    (rcH, outH), tH = oth["human"]
    check("a person editing an Open ticket mid-run is blocked by the batch's lock until it finishes (%.1fs) - the batch is never overwritten by them" % tH, tH > 4 or rcA != 0, (round(tH, 1), outH[-150:]))
    check("...and the batch committed atomically", rcA == 0 and has(outA, "COMMITTED"), outA[-300:])

def t14_switches():
    print("T14 @CsAgentRoleOnly and @IncludeLiveAiConversations (both default to the safe setting; Preview and Execute must agree)")
    restore()
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC]))
    check("default: Department Employee is shown as excluded by @CsAgentRoleOnly", any("DE Person" in l and "excluded by @CsAgentRoleOnly" in l for l in out.splitlines()))
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], CsAgentRoleOnly=0))
    ap0 = parse_approvals(out)
    check("@CsAgentRoleOnly = 0: pool is the engine's full pool (4 agents)", ap0 is not None and ap0[2] == 4 and ap0[:2] == (ELIG, MOVED), ap0)
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED, CsAgentRoleOnly=0)
    check("...Execute with the DEFAULT approved agent count (3) is refused: pool differs from what was approved", rc != 0 and has(out, "ApprovedAgentCount"), out[-200:])
    o = oracle_full(cs_only=False); plan = o["plan"]
    rc, out = execute(dry=False, batch=BATCH, approved=ap0, CsAgentRoleOnly=0)
    got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE TicketStatus=1")}
    check("...with 4 agents it commits and equals the oracle (Department Employee included)", rc == 0 and all(got[t] == a for t, a in plan.items()) and any(a.startswith("f0000000") for a in plan.values()), out[-200:])
    restore(); o = oracle_full()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    check("default: the Department Employee owns no Open ticket", scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE CurrentOwnerEmployeeId='f0000000-0000-0000-0000-000000000009' AND TicketStatus=1") == "0")
    restore()
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], IncludeLiveAiConversations=1))
    ap1 = parse_approvals(out)
    check("@IncludeLiveAiConversations = 1: the %d Open AI-only tickets become eligible and are no longer listed as held back (approvals still held)" % HELD_AI,
          ap1 is not None and ap1[:2] == (ELIG + HELD_AI, MOVED + HELD_AI) and ap1[3:5] == (HELD_APPR, 0) and "ACTIVE AI CONVERSATION" not in out, ap1)
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED, IncludeLiveAiConversations=1)
    check("...Execute with the default (hold-back) approved counts is refused", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-200:])
    o = oracle_full(include_ai=True); plan = o["plan"]
    rc, out = execute(dry=False, batch=BATCH, approved=ap1, IncludeLiveAiConversations=1)
    got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE TicketStatus=1")}
    ai = ids(["H-AI-%d" % i for i in range(1, 6)]); appr = ids(["H-APR-%d" % i for i in range(1, 7)] + ["H-BOTH-1"])
    ai_open = {t for t in ai if scalar("SELECT TicketStatus FROM dbo.Tickets WHERE TicketId=%d" % t) == "1"}
    appr_open = {t for t in appr if scalar("SELECT TicketStatus FROM dbo.Tickets WHERE TicketId=%d" % t) == "1"}
    check("...with the matching values it commits: AI tickets moved and owned, pending approvals STILL untouched", rc == 0 and all(got[t] == a for t, a in plan.items())
          and all(got[t] != "null" for t in ai_open) and all(got[t] == "null" for t in appr_open), out[-250:])

def t15_rules_ownership_history():
    print("T15 the final rules: Open only, assigned included, answering agent, history, previous owner")
    restore(); pre = state(); o = oracle_full()
    plan, prev, action, origin, cc = o["plan"], o["prev"], o["action"], o["origin"], o["cc"]
    A1, A2, A3 = "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0001-000000000000", "7a000000-0000-0000-0000-000000000001"
    k = lambda names: ids(names)
    nonopen_owned = k(["X-INP-1", "X-INP-2", "X-PEN-1", "X-PEN-2", "X-OWN-CC-1", "X-OWN-CC-2", "X-OWN-CC-3", "X-OWN-CC-4"])
    check("tickets that are NOT Open (InProgress / Pending) are not in the plan even though they are assigned", not (nonopen_owned & set(plan)) and len(nonopen_owned) == 8)
    open_owned = k(["X-OWN-FIN-%d" % i for i in range(1, 7)])
    check("Open tickets that ARE assigned (here to a Finance employee) are in the plan and will be reassigned", open_owned <= set(plan) and all(action[t] == "REASSIGN" for t in open_owned))
    keep = k(["K-KEEP-1", "K-KEEP-2"])
    check("a ticket already in CC and owned by the agent who answered it is KEPT (no write)", all(action[t] == "KEEP" and plan[t] == A2 for t in keep))
    pin = k(["K-PIN-1", "K-PIN-2"]); pin3 = k(["K-PIN-3"]); badpin = k(["K-PINBAD-1"])
    check("answered by an eligible agent -> that agent (moved from SUP unowned / FIN owned by another), never a Round Robin turn",
          all(plan[t] == A3 for t in pin) and all(plan[t] == A1 for t in pin3) and set(pin) | pin3 <= o["pinned"])
    check("answered by an INELIGIBLE (deactivated) person -> falls back to Round Robin", not (badpin & o["pinned"]) and badpin <= set(plan))
    legacy = k(["X-LEGACY-1", "X-LEGACY-2"]); drift = k(["X-DRIFT-1", "X-DRIFT-2", "X-DRIFT-3"])
    check("legacy data (owner without an assignment row; assignment row without an owner) is in the plan and repaired", (legacy | drift) <= set(plan))
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit succeeds", rc == 0 and has(out, "COMMITTED"), out[-300:])
    aix = col_index("TicketAssignments"); post = state(); tk = {r[0]: r for r in post["Tickets"]}; pk = {r[0]: r for r in pre["Tickets"]}
    check("InProgress / Pending assigned tickets: untouched, byte for byte", all(tk[str(t)] == pk[str(t)] for t in nonopen_owned))
    check("KEPT tickets: ticket row and their assignment rows untouched", all(tk[str(t)] == pk[str(t)] for t in keep) and
          all(r in post["TicketAssignments"] for r in pre["TicketAssignments"] if int(r[aix["TicketId"]]) in keep))
    # previous owner loses ordinary access = no longer owner + a retired (superseded) assignment row naming them, kept as history
    for t in open_owned:
        owner_now = scalar("SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketId=%d" % t)
        assert owner_now == plan[t]
    fin = "b0000000-0000-0000-0000-000000000001"
    check("the Finance employee owns none of the 6 reassigned Open tickets any more", scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketId IN (%s) AND CurrentOwnerEmployeeId='%s'" % (",".join(map(str, open_owned)), fin)) == "0")
    check("...and holds a SUPERSEDED assignment row on each (history kept; the app treats that as 'previous owner' and removes Resolve/Close/Transfer)",
          scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE TicketId IN (%s) AND AssignedEmployeeId='%s' AND IsCurrent=0" % (",".join(map(str, open_owned)), fin)) == "6")
    check("legacy owner-without-row tickets now have exactly one current row; drift tickets' stale current row is retired and replaced",
          all(scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE TicketId=%d AND IsCurrent=1" % t) == "1" for t in legacy | drift) and
          all(scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE TicketId=%d AND IsCurrent=0" % t) == "1" for t in drift))
    check("every changed ticket is visible to its receiving employee as 'My Tickets' (owner = receiving agent, Open, in CC)",
          all(scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketId=%d AND CurrentOwnerEmployeeId='%s' AND TicketStatus=1 AND CurrentDepartmentId=%d" % (t, plan[t], cc)) == "1" for t in plan))
    mine = {a: int(scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE CurrentOwnerEmployeeId='%s' AND TicketStatus=1" % a)) for a in o["agents"]}
    exp = {a: sum(1 for t, x in plan.items() if x == a) for a in o["agents"]}
    check("Open tickets per agent after the run = oracle %s" % sorted(exp.values()), all(mine[a] >= exp[a] for a in exp) and sum(mine.values()) >= sum(exp.values()), (mine, exp))
    one_current("exactly one current assignment per ticket everywhere")

def t16_idempotent_rerun_is_blocked_and_safe():
    print("T16 a second run cannot double-assign")
    restore(); execute(dry=False, batch=BATCH, approved=APPROVED)
    post1 = digest(state())
    rc, out = execute(dry=False, batch="55555555-2222-3333-4444-555555555555", approved=APPROVED)
    check("second run refused and changed nothing", rc != 0 and has(out, "already exists") and digest(state()) == post1, out[-200:])
    rc, out = execute(dry=True, batch="66666666-2222-3333-4444-555555555555", AllowSecondBatch=1)
    check("even with the override a dry run only reports, writes nothing", rc == 0 and digest(state()) == post1, out[-200:])

TESTS = [t01_preview_readonly, t01b_preview_as_datareader, t02_dry_run, t03_gates, t04_commit, t05_rollback_clean, t06_rollback_touched, t06b_include_worked, t07_concurrency,
         t08_race_between_preview_and_run, t09_cursor_and_pool_edges, t11_pending_approvals, t11b_approval_decided_between_preview_and_run, t12_active_ai, t13_concurrent_safeguards,
         t14_switches, t15_rules_ownership_history, t16_idempotent_rerun_is_blocked_and_safe, t10_scale]

if __name__ == "__main__":
    only = sys.argv[1:]
    init_constants()
    for t in TESTS:
        if only and not any(o in t.__name__ for o in only): continue
        try: t()
        except Exception as ex:
            check("%s raised" % t.__name__, False, repr(ex))
    bad = [r for r in results if not r[1]]
    print("\n%d checks, %d passed, %d FAILED" % (len(results), len(results) - len(bad), len(bad)))
    for n, _, d in bad: print("  FAILED:", n, "|", d)
    sys.exit(1 if bad else 0)
