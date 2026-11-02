#!/usr/bin/env python3
"""
Validation suite for the Call Center backlog-distribution scripts (Preview / Execute / Rollback) against a REAL SQL Server.
Scope under test: EVERY Open ticket (owned or not) is moved to the Call Center and dealt Round Robin; pending approvals and live AI conversations are held back.

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

def approval_params(a):
    """a = (tickets, moved, reassigned, agents, held-back approvals, held-back AI) - the six commit gates."""
    return dict(ApprovedTicketCount=a[0], ApprovedTransferCount=a[1], ApprovedReassignedCount=a[2], ApprovedAgentCount=a[3],
                ApprovedHeldBackPendingApprovalCount=a[4], ApprovedHeldBackActiveAiCount=a[5])

def execute(dry=True, batch=None, approved=None, services=(SVC,), **extra):
    p = dict(DryRun=1 if dry else 0, ServiceIdsConfirmed=1, services=list(services))
    if batch: p["BatchId"] = "'%s'" % batch
    if approved: p.update(approval_params(approved))
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

TABLES = [("Tickets", "TicketId"), ("TicketApprovals", "TicketApprovalId"), ("TicketInteractions", "TicketInteractionId"), ("TicketAssignments", "TicketAssignmentId"), ("AuditEntries", "AuditEntryId"),
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
PREV = {}      # ticketId -> (previous owner or None, previous CURRENT assignment row id or None); refreshed by every oracle() call

def open_tickets():
    """All Open tickets: (id, created, current dept, owner|None)."""
    return [(int(r[0]), r[1], int(r[2]), None if r[3] == "NULL" else r[3].lower())
            for r in rows("SELECT TicketId, CONVERT(nvarchar(30),CreatedAtUtc,121), CurrentDepartmentId, ISNULL(CONVERT(nvarchar(36),CurrentOwnerEmployeeId),'NULL') "
                          "FROM dbo.Tickets WHERE TicketStatus=1")]

import datetime as _dt
def _ts(v): return None if v in ("NULL", "", None) else _dt.datetime.strptime(v[:26].ljust(26, "0"), "%Y-%m-%d %H:%M:%S.%f")

def ai_state(recent_min=60, stale_h=24):
    """Independent re-implementation of the diagnostic: for every Open ticket the active-AI rule flags (before the pending-approval split):
    {ticketId: (state, human_evidence, age_hours)} - computed in Python from raw rows."""
    now = _ts(scalar("SELECT CONVERT(varchar(27),SYSUTCDATETIME(),121)"))
    open_ids = {t for t, _, _, _ in open_tickets()}
    created = {int(r[0]): _ts(r[1]) for r in rows("SELECT TicketId, CONVERT(varchar(27),CreatedAtUtc,121) FROM dbo.Tickets WHERE TicketStatus=1")}
    firstresp = {int(r[0]) for r in rows("SELECT TicketId FROM dbo.Tickets WHERE TicketStatus=1 AND FirstHumanResponseAtUtc IS NOT NULL")}
    inter = {}; iid2t = {}
    for r in rows("SELECT TicketInteractionId, TicketId, Source, ISNULL(CONVERT(varchar(27),EndedAtUtc,121),'NULL'), ISNULL(CONVERT(varchar(27),InactivityClosedAtUtc,121),'NULL'), RequiresHumanFollowUp, ISNULL(CONVERT(nvarchar(36),HandledByUserId),'NULL'), "
                  "CONVERT(varchar(27),CreatedAtUtc,121), ISNULL(CONVERT(varchar(27),InteractionStartedAtUtc,121),'NULL'), ISNULL(CONVERT(varchar(27),LastCustomerInteractionAtUtc,121),'NULL'), ISNULL(CONVERT(varchar(27),AwaitingCustomerReplySinceUtc,121),'NULL') FROM dbo.TicketInteractions"):
        iid2t[int(r[0])] = int(r[1]); inter.setdefault(int(r[1]), []).append(r)
    msgs = {}
    for iid, sender, sent in rows("SELECT TicketInteractionId, Sender, CONVERT(varchar(27),SentAtUtc,121) FROM dbo.TicketInteractionMessages"):
        msgs.setdefault(iid2t[int(iid)], []).append((sender, _ts(sent)))
    handlers = {int(r[0]) for r in rows("SELECT DISTINCT TicketId FROM dbo.TicketInteractionHandlers")}
    handoff_any = {int(r[0]) for r in rows("SELECT DISTINCT TicketId FROM dbo.TicketAgentHandoffs")}
    handoff_open = {int(r[0]) for r in rows("SELECT DISTINCT TicketId FROM dbo.TicketAgentHandoffs WHERE Status IN (2,3,4)")}
    events = {}
    for t, et, occ in rows("SELECT TicketId, EventType, CONVERT(varchar(27),OccurredAtUtc,121) FROM dbo.TicketWorkflowEvents"):
        events.setdefault(int(t), []).append((int(et), _ts(occ)))
    out = {}
    for t in open_ids:
        rs = inter.get(t, [])
        live = [r for r in rs if r[2] == "2" and r[3] == "NULL" and r[4] == "NULL" and r[5] in ("0", "False") and r[6] == "NULL"]
        if not live or any(r[5] in ("1", "True") for r in rs) or t in handoff_open: continue
        stamps = [created[t]] + [_ts(v) for r in rs for v in (r[7], r[8], r[9], r[10]) if _ts(v)]
        stamps += [m[1] for m in msgs.get(t, [])] + [o for et, o in events.get(t, []) if 10 <= et <= 19]
        last = max(stamps); age = (now - last).total_seconds() / 3600.0
        human = (any(m[0] == "2" for m in msgs.get(t, [])) or t in handlers or t in firstresp or t in handoff_any or any(et in (11, 12, 18, 19) for et, _ in events.get(t, [])))
        st = "HUMAN_TOUCHED_NOT_FLAGGED" if human else ("GENUINELY_ACTIVE" if age <= recent_min / 60.0 else ("SILENT_BUT_UNDER_THRESHOLD" if age <= stale_h else "STALE_NO_END_EVENT"))
        out[t] = (st, human, age)
    return out

def held_sets(include_ai=False, stale_after=None, human=False):
    """Independent re-implementation of the hold-back rules from raw rows: (all Open ids, pending-approval ids, AI-only ids)."""
    base = {t for t, _, _, _ in open_tickets()}
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
        flagged_human = any(x[3] in ("1", "True") for x in rs)
        if live and not flagged_human and t not in handoff: ai.add(t)
    if stale_after is not None or human:
        st = ai_state()
        ai = {t for t in ai if not ((st[t][1] and human) or (not st[t][1] and stale_after is not None and st[t][2] > stale_after))}
    return base, pend, (set() if include_ai else ai - pend)

def oracle(service_ids=(SVC,), cs_only=True, include_ai=False, reassign_owned=True, exclude_denied=True, stale_after=None, human=False):
    """Expected plan {ticketId: agentId} computed in Python from raw table data (Open tickets only, owned or not)."""
    svc = {s.lower() for s in service_ids}
    cc = int(scalar("SELECT DepartmentId FROM dbo.Departments WHERE Code='CC' AND IsActive=1"))
    members = rows("SELECT e.EmployeeId, CASE WHEN e.DeactivatedAtUtc IS NULL THEN 0 ELSE 1 END, "
                   "CASE WHEN u.LockoutEnabled=1 AND u.LockoutEnd>SYSDATETIMEOFFSET() THEN 1 ELSE 0 END "
                   "FROM dbo.Employees e JOIN dbo.AspNetUsers u ON u.Id=e.EmployeeId "
                   "JOIN dbo.UserDepartmentAssignments a ON a.EmployeeId=e.EmployeeId AND a.DepartmentId=%d" % cc)
    roles = {}
    for uid, name in rows("SELECT ur.UserId, r.Name FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id=ur.RoleId"):
        roles.setdefault(uid.lower(), set()).add(name)
    denied = set()
    if scalar("SELECT ISNULL(OBJECT_ID('dbo.UserPermissionOverrides','U'),0)") != "0":
        denied = {r[0].lower() for r in rows("SELECT EmployeeId FROM dbo.UserPermissionOverrides WHERE Effect=2 AND PermissionKey IN ('Tickets.View','Tickets.Resolve','Tickets.Close')")}
    ops = {"CS Agent"} if cs_only else {"CS Agent", "Department Employee", "Department Head"}
    agents = sorted({m[0].lower() for m in members
                     if m[1] == "0" and m[2] == "0" and m[0].lower() not in svc and roles.get(m[0].lower(), set()) & ops
                     and not (exclude_denied and m[0].lower() in denied)})
    cur = rows("SELECT CONVERT(nvarchar(36),LastAssignedEmployeeId) FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId=%d" % cc)
    cursor = cur[0][0].lower() if cur and cur[0][0] != "NULL" else None
    base, pend, ai_only = held_sets(include_ai, stale_after, human)
    cur_asg = {int(r[0]): int(r[1]) for r in rows("SELECT TicketId, TicketAssignmentId FROM dbo.TicketAssignments WHERE IsCurrent=1")}
    cands = []
    PREV.clear()
    for t, created, dept, owner in open_tickets():
        if t in pend or t in ai_only: continue
        if not reassign_owned and dept == cc and owner in set(agents): continue
        cands.append((created, t, dept)); PREV[t] = (owner, cur_asg.get(t))
    cands.sort(key=lambda r: (r[0], r[1]))
    start = 0
    if cursor:
        nxt = [i for i, a in enumerate(agents) if a > cursor]
        start = nxt[0] if nxt else 0
    plan = {t: agents[(start + i) % len(agents)] for i, (_, t, _) in enumerate(cands)}
    origin = {t: d for _, t, d in cands}
    return plan, origin, cc, agents

# ---------------------------------------------------------------------------------------------------------------------------------
results = []
def check(name, cond, detail=""):
    results.append((name, bool(cond), detail))
    print(("  PASS  " if cond else "  FAIL  ") + name + ((" -> " + str(detail)) if (detail and not cond) else ""))

def has(out, text): return text.lower() in out.lower()

def last_batch_id(out):
    m = re.search(r"BatchId=([0-9A-Fa-f-]{36})", out); return m.group(1) if m else None

# the fixture (01_seed.sql, hand-counted in its header): 62 E + 10 F + 3 D + 24 O eligible; 14 held back; default pool = CS Agent role, not denied (A1, A2, A3)
ELIG, MOVED, REASS, AGENTS, HELD_APPR, HELD_AI = 99, 87, 24, 3, 8, 6
OPEN_TOTAL, OPEN_OWNED = 113, 26
APPROVED = (ELIG, MOVED, REASS, AGENTS, HELD_APPR, HELD_AI)
def A(**kw):
    """The approved tuple with named overrides: t(ickets) m(oved) r(eassigned) a(gents) ha (held approvals) hi (held AI)."""
    d = dict(t=ELIG, m=MOVED, r=REASS, a=AGENTS, ha=HELD_APPR, hi=HELD_AI); d.update(kw)
    return (d["t"], d["m"], d["r"], d["a"], d["ha"], d["hi"])
BATCH = "11111111-2222-3333-4444-555555555555"
A1, A2, A3, A4 = "00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0001-000000000000", "7a000000-0000-0000-0000-000000000001", "7b000000-0000-0000-0000-000000000004"
FINEMP = "b0000000-0000-0000-0000-000000000001"

def counts_of(plan, origin, cc):
    return (len(plan), sum(1 for t, d in origin.items() if d != cc), sum(1 for t in plan if PREV[t][0]))

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
    base, pend, ai_only = held_sets()
    check("oracle: %d eligible, %d moved, %d owned (owner replaced), %d agents, held back %d approvals + %d AI-only" % (ELIG, MOVED, REASS, AGENTS, HELD_APPR, HELD_AI),
          counts_of(plan, origin, cc) + (len(agents), len(pend), len(ai_only)) == (ELIG, MOVED, REASS, AGENTS, HELD_APPR, HELD_AI), (counts_of(plan, origin, cc), len(agents), len(pend), len(ai_only)))
    owned_open = sum(1 for t, _, _, o in open_tickets() if o)
    check("oracle: %d Open tickets, %d currently owned" % (OPEN_TOTAL, OPEN_OWNED), (len(base), owned_open) == (OPEN_TOTAL, OPEN_OWNED), (len(base), owned_open))
    check("preview verdict row: total Open / owned / unowned / eligible / moved / owner replaced / held back",
          re.search(r"\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|0\|" % (OPEN_TOTAL, OPEN_OWNED, OPEN_TOTAL - OPEN_OWNED, ELIG, MOVED, ELIG - MOVED, REASS, ELIG - REASS, HELD_APPR, HELD_AI, 0), out) is not None
          or re.search(r"\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|0\|" % (OPEN_TOTAL, OPEN_OWNED, OPEN_TOTAL - OPEN_OWNED, ELIG, MOVED, ELIG - MOVED, REASS, ELIG - REASS, HELD_APPR, HELD_AI), out) is not None, out[:900])
    check("preview approval values = %s" % (APPROVED,), re.search(r"\n%d\|%d\|%d\|%d\|%d\|%d\s*\n" % APPROVED, out) is not None)
    # exact plan rows: DealOrder|TicketId|TicketNumber|CreatedAt|FromDept|ToDept|PreviousOwner|agent display name
    got = {}
    for line in out.splitlines():
        m = re.match(r"^(\d+)\|(\d+)\|[A-Z]-[A-Za-z0-9-]+\|[^|]+\|\w+\|CC[^|]*\|[^|]*\|(\w+) Person\|", line)
        if m: got[int(m.group(2))] = m.group(3)
    names = {a: scalar("SELECT DisplayName FROM dbo.Employees WHERE EmployeeId='%s'" % a).split()[0] for a in agents}
    check("preview plan == independent oracle (all %d tickets)" % ELIG, len(got) == ELIG and all(names[plan[t]] == got[t] for t in got), (len(got),))
    prevowner = {}
    for line in out.splitlines():
        m = re.match(r"^(\d+)\|(\d+)\|[A-Z]-[A-Za-z0-9-]+\|[^|]+\|\w+\|CC[^|]*\|([^|]*)\|(\w+) Person\|", line)
        if m: prevowner[int(m.group(2))] = m.group(3)
    check("preview names the previous owner of every owned ticket and '(unowned)' for the rest", len(prevowner) == ELIG and
          all((prevowner[t] == "(unowned)") == (PREV[t][0] is None) for t in prevowner), len(prevowner))
    check("preview shows who loses tickets (FINEMP 9, SUPEMP 4, A1 4, DEACT 2, SUPV 2, SVC 2, LOCK 1) with owner kind",
          all(re.search(r"^%s\|[^|]*\|%s" % (n, k), out, re.M) for n, k in (("FINEMP Person", "employee of another department"), ("A1 Person", "CC agent in the pool"), ("DEACT Person", "deactivated employee"),
                                                                           ("SVC Person", "SERVICE ACCOUNT"), ("LOCK Person", "locked out"), ("SUPV Person", "CC member, not in the pool"))))
    check("non-Open statuses are listed as never touched (InProgress 8, PendingCustomer 3, PendingThirdParty 1, Resolved 7, Closed 4)",
          all(re.search(r"^%s\|%d\|" % (s, n), out, re.M) for s, n in (("InProgress", 8), ("PendingCustomer", 3), ("PendingThirdParty", 1), ("Resolved", 7), ("Closed", 4))))
    check("data drift (3) and legacy owner-without-assignment (2) are reported as included", re.search(r"\n3\|2\|0\|0\s*\n", out) is not None)
    check("agent A4 (explicit DENY of Tickets.Resolve) is listed as excluded with the denied permission", any(l.startswith("-|A4 Person|") and "explicit permission DENIAL" in l and l.rstrip().split("|")[-3] == "Tickets.Resolve" for l in out.splitlines()), [l for l in out.splitlines() if "A4 Person" in l][:2])
    # blockers
    rc, out = run_text(patch(PREVIEW, services=[SVC]))
    check("blocker when @ServiceIdsConfirmed = 0", "BLOCKER: service-account exclusions not confirmed" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1))
    check("blocker when service list empty and no explicit 'none exist'", "BLOCKER: service-account list is empty" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, NoServiceAccountsExist=1))
    check("'none exist' acknowledgement clears that blocker", "READY" in out)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=["99999999-9999-9999-9999-999999999999"]))
    check("blocker when a listed id is not an employee", "not an employee" in out)

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
    check("...and sees the explicit denial (UserPermissionOverrides is readable)", "Tickets.Resolve" in p.stdout)
    p = subprocess.run(base + ["-Q", "UPDATE dbo.Tickets SET CurrentOwnerEmployeeId = NULL"], capture_output=True, text=True)
    check("that login is denied any write", p.returncode != 0 and "permission" in (p.stdout + p.stderr).lower())

def t02_dry_run():
    print("T02 execute @DryRun=1 writes nothing")
    restore(); before = digest(state())
    rc, out = execute(dry=True)
    after = digest(state())
    check("dry run exits 0 and says ROLLED BACK", rc == 0 and has(out, "DRY RUN complete - transaction ROLLED BACK"), out[-500:])
    check("dry run changed nothing", before == after, [t for t in before if before[t] != after[t]])
    check("dry run reports %d assigned / %d moved" % (ELIG, MOVED), has(out, "Tickets that would have been assigned: %d" % ELIG) and has(out, "moved into CC: %d" % MOVED))

def t03_gates():
    print("T03 commit gates")
    restore(); before = digest(state())
    def refused(label, rc_out, text):
        rc, out = rc_out
        check(label, rc != 0 and has(out, text) and digest(state()) == before, out[-300:])
    refused("no approval counts -> refused", run_text(patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC])), "commit gates not satisfied")
    refused("service ids not confirmed -> refused", run_text(patch(EXECUTE, DryRun=0, services=[SVC], **approval_params(APPROVED))), "commit gates not satisfied")
    refused("empty service list without acknowledgement -> refused", execute(False, approved=APPROVED, services=()), "service-account list is empty")
    refused("service id typo -> refused", execute(False, approved=APPROVED, services=("99999999-9999-9999-9999-999999999999",)), "not an employee")
    refused("wrong ticket count -> refused", execute(False, approved=A(t=ELIG - 1)), "backlog changed since the preview")
    refused("wrong moved count -> refused", execute(False, approved=A(m=MOVED - 1)), "backlog changed since the preview")
    refused("wrong REASSIGNED (owner replaced) count -> refused", execute(False, approved=A(r=REASS - 1)), "backlog changed since the preview")
    refused("wrong agent count -> refused", execute(False, approved=A(a=AGENTS + 1)), "ApprovedAgentCount")
    refused("wrong held-back PENDING-APPROVAL count -> refused", execute(False, approved=A(ha=HELD_APPR - 1)), "backlog changed since the preview")
    refused("wrong held-back ACTIVE-AI count -> refused", execute(False, approved=A(hi=HELD_AI + 1)), "backlog changed since the preview")
    p = approval_params(APPROVED); p.pop("ApprovedReassignedCount")
    refused("reassigned-count gate missing -> refused", run_text(patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], **p)), "commit gates not satisfied")
    p = approval_params(APPROVED); p.pop("ApprovedHeldBackPendingApprovalCount"); p.pop("ApprovedHeldBackActiveAiCount")
    refused("held-back counts missing -> refused", run_text(patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], **p)), "commit gates not satisfied")
    # listing a REAL agent as a 'service account' removes them from the pool -> agent count gate trips
    refused("excluding one more agent changes the pool -> refused", execute(False, approved=APPROVED, services=(SVC, A1)), "ApprovedAgentCount")
    # DB-name guard
    rc, out = run_text("CREATE DATABASE WrongName", "master");
    rc, out = run_text(patch(EXECUTE, DryRun=1), "WrongName")
    check("wrong database name -> aborted by guard", rc != 0 and has(out, "this script only runs against"), out[-200:])
    run_text("DROP DATABASE WrongName", "master")

ASG_COLS = "TicketAssignmentId, TicketId, CONVERT(nvarchar(36),AssignedEmployeeId), AssignedDepartmentId, CONVERT(nvarchar(30),AssignedAtUtc,121), ISNULL(CONVERT(nvarchar(36),AssigningActorEmployeeId),'NULL')"
def asg_rows(where="1=1", cols=ASG_COLS):
    return rows("SELECT %s, IsCurrent FROM dbo.TicketAssignments WHERE %s ORDER BY TicketAssignmentId" % (cols, where))

def verify_commit(plan, origin, cc, pre, batch):
    st = state()
    tk = {r[0]: r for r in st["Tickets"]}
    cols = [r[0] for r in rows("SELECT c.name FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID('dbo.Tickets') AND t.name<>'timestamp' ORDER BY c.column_id")]
    ix = {c: i for i, c in enumerate(cols)}
    pre_tk = {r[0]: r for r in pre["Tickets"]}
    ok_owner = ok_dept = ok_pres = ok_open = True
    for t, agent in plan.items():
        r, p0 = tk[str(t)], pre_tk[str(t)]
        ok_owner &= r[ix["CurrentOwnerEmployeeId"]].lower() == agent
        ok_dept &= int(r[ix["CurrentDepartmentId"]]) == cc
        ok_open &= r[ix["TicketStatus"]] == "1"
        for c in ("OriginatingDepartmentId", "RequestTypeId", "CategoryId", "PriorityId", "TicketStatus", "SlaState", "EscalationLevel", "VerificationStatus", "CreatedAtUtc", "TicketNumber", "RequestSummary", "FirstHumanResponseAtUtc"):
            ok_pres &= r[ix[c]] == p0[ix[c]]
    check("every eligible ticket owned by its oracle-planned agent (owned tickets: previous owner REPLACED)", ok_owner)
    check("every eligible ticket now in Call Center and still Open", ok_dept and ok_open)
    check("originating dept / request type / category / priority / status / SLA state / created / first response preserved", ok_pres)
    untouched = [t for t in tk if int(t) not in plan]
    check("all %d non-eligible tickets (non-Open, held back, ...) are byte-identical" % len(untouched), all(tk[t] == pre_tk[t] for t in untouched))
    # assignments: exactly one current per eligible ticket; every pre-existing row still there, unchanged except IsCurrent
    cur = rows("SELECT TicketId, CONVERT(nvarchar(36),AssignedEmployeeId), AssignedDepartmentId, ISNULL(CONVERT(nvarchar(36),AssigningActorEmployeeId),'NULL') FROM dbo.TicketAssignments WHERE IsCurrent=1")
    curmap = {}
    for t, a, d, act in cur: curmap.setdefault(int(t), []).append((a.lower(), int(d), act))
    check("exactly one current assignment per eligible ticket, matching owner, in CC, system actor",
          all(len(curmap.get(t, [])) == 1 and curmap[t][0] == (a, cc, "NULL") for t, a in plan.items()))
    check("no ticket anywhere has two current assignments", all(len(v) == 1 for v in curmap.values()))
    pre_cols = {r[0]: r[1:] for r in pre["TicketAssignments"]}
    post_asg = {r[0]: r for r in st["TicketAssignments"]}
    acols = [r[0] for r in rows("SELECT c.name FROM sys.columns c WHERE c.object_id=OBJECT_ID('dbo.TicketAssignments') ORDER BY c.column_id")]
    ci = acols.index("IsCurrent")
    ok_hist = True
    for old in pre["TicketAssignments"]:
        new = post_asg.get(old[0])
        if new is None: ok_hist = False; continue
        for i, name in enumerate(acols):
            if name == "IsCurrent": continue
            ok_hist &= old[i] == new[i]
    check("every pre-existing assignment row (incl. superseded history) still exists, unchanged except IsCurrent", ok_hist)
    prev_rows = [PREV[t][1] for t in plan if PREV[t][1] is not None]
    check("the %d previous current assignment rows are retired (IsCurrent=0), none deleted" % len(prev_rows),
          all(post_asg[str(a)][ci] in ("0", "False") for a in prev_rows) and all(str(a) in post_asg for a in prev_rows))
    check("%d new assignment rows" % len(plan), len(st["TicketAssignments"]) - len(pre["TicketAssignments"]) == len(plan))
    # audit
    moved = [t for t, d in origin.items() if d != cc]
    aud = rows("SELECT Action, EntityId, ISNULL(BeforeValue,'NULL'), ISNULL(AfterValue,'NULL') FROM dbo.AuditEntries WHERE CorrelationId='%s'" % batch)
    transfers = {int(a[1]): a for a in aud if a[0] == "Transfer"}
    autos = {int(a[1]): a for a in aud if a[0] == "AutoAssign"}
    check("one Transfer audit per moved ticket with original dept and previous owner (or DepartmentQueue) in BeforeValue", set(transfers) == set(moved) and
          all(("DepartmentId=%d;" % origin[t]) in transfers[t][2] and ("DepartmentId=%d;" % cc) in transfers[t][3]
              and transfers[t][2].endswith(PREV[t][0] or "DepartmentQueue") for t in moved))
    check("one AutoAssign audit per eligible ticket naming the planned agent", set(autos) == set(plan) and all(("AssignedEmployeeId=" + plan[t]) in autos[t][3] for t in plan))
    check("every AutoAssign audit records PreviousOwnerId / PreviousAssignmentId / PreviousDepartmentId", all(
          autos[t][2] == "PreviousOwnerId=%s;PreviousAssignmentId=%s;PreviousDepartmentId=%d" % (PREV[t][0] or "none", PREV[t][1] if PREV[t][1] is not None else "none", origin[t]) for t in plan))
    check("AutoAssign audit flags Reassigned=1 exactly for tickets that had an owner", all(autos[t][3].endswith(";Reassigned=%d" % (1 if PREV[t][0] else 0)) for t in plan))
    check("one batch audit row", sum(1 for a in aud if a[0] == "BacklogDistributionBatch") == 1)
    check("all pre-existing audit rows unchanged", all(r in st["AuditEntries"] for r in pre["AuditEntries"]))
    # workflow events: moved OR previously owned
    evt = {t for t in plan if origin[t] != cc or PREV[t][0]}
    ev = rows("SELECT TicketId, EventType, TransferReasonCode, FromDepartmentId, ToDepartmentId, CONVERT(nvarchar(36),ToEmployeeId), ISNULL(CONVERT(nvarchar(36),ActorEmployeeId),'NULL'), Note "
              "FROM dbo.TicketWorkflowEvents WHERE CorrelationId='%s'" % batch)
    check("one TicketTransferred/SystemRouting event per moved OR reassigned ticket, From->CC, receiving agent, system actor",
          len(ev) == len(evt) and all(e[1] == "17" and e[2] == "5" and int(e[3]) == origin[int(e[0])] and int(e[4]) == cc and e[5].lower() == plan[int(e[0])] and e[6] == "NULL" for e in ev))
    check("the event of an owned ticket names its previous owner", all(("previous owner " + PREV[int(e[0])][0]) in e[7] for e in ev if PREV[int(e[0])][0]) and
          all("previous owner" not in e[7] for e in ev if not PREV[int(e[0])][0]))
    check("no event for an unowned ticket that was already in CC", {int(e[0]) for e in ev} == evt)
    check("pre-existing workflow events unchanged", all(r in st["TicketWorkflowEvents"] for r in pre["TicketWorkflowEvents"]))
    # untouched history
    for t in ("TicketStatusHistory", "TicketNotes", "Employees", "UserDepartmentAssignments", "TicketApprovals", "TicketInteractions"):
        check("%s identical" % t, st[t] == pre[t])
    # rotation + fairness + eligibility
    agents = {a for a in plan.values()}
    counts = {a: sum(1 for x in plan.values() if x == a) for a in agents}
    check("fair: per-agent spread <= 1", max(counts.values()) - min(counts.values()) <= 1, counts)
    bad = scalar("SELECT COUNT(*) FROM dbo.Tickets t JOIN dbo.Employees e ON e.EmployeeId=t.CurrentOwnerEmployeeId WHERE e.DisplayName IN ('DEACT Person','LOCK Person','SVC Person','RO Person','SUPV Person','CHAIR Person','A4 Person','DE Person') AND t.TicketId IN (%s)" % ",".join(map(str, plan)))
    check("nothing assigned to deactivated / locked / service / read-only / supervisory / denied accounts", bad == "0", bad)
    my = scalar("SELECT COUNT(*) FROM dbo.Tickets t JOIN dbo.UserDepartmentAssignments u ON u.EmployeeId=t.CurrentOwnerEmployeeId AND u.DepartmentId=%d WHERE t.TicketStatus=1 AND t.TicketId IN (%s)" % (cc, ",".join(map(str, plan))))
    check("My Tickets: every one of the %d tickets is owned by (and listed under) a Call Center employee" % len(plan), my == str(len(plan)), my)
    last = plan[list(plan)[-1]]
    cursor = scalar("SELECT LOWER(CONVERT(nvarchar(36),LastAssignedEmployeeId)) FROM dbo.DepartmentAssignmentRotations WHERE DepartmentId=%d" % cc)
    check("rotation cursor = last agent dealt", cursor == last, (cursor, last))

def t04_commit():
    print("T04 real commit")
    restore(); pre = state()
    plan, origin, cc, agents = oracle()
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit exits 0 and says COMMITTED", rc == 0 and has(out, "COMMITTED. Tickets assigned: %d" % ELIG), out[-500:])
    check("prints the BatchId for rollback", BATCH in out)
    verify_commit(plan, origin, cc, pre, BATCH)
    nonopen = ids(["N-INP-%d" % i for i in range(1, 5)] + ["N-PEN-1", "N-PEN-2", "N-PEN4-1", "N-OWNINP-1", "N-OWNINP-2", "N-OWNPEN-1", "N-APR-1", "N-AI-1",
                  "X-RES-1", "X-RESOWN-1", "X-RESOWN-2", "X-CLO-1", "X-CLOOWN-1"])
    tk = {r[0]: r for r in state()["Tickets"]}; pk = {r[0]: r for r in pre["Tickets"]}
    check("InProgress / Pending / Resolved / Closed tickets (owned and unowned, even with a pending approval or AI conversation) are byte-identical",
          not (nonopen & set(plan)) and all(tk[str(t)] == pk[str(t)] for t in nonopen) and
          all(r in state()["TicketAssignments"] for r in pre["TicketAssignments"] if int(r[1]) in nonopen), len(nonopen))
    rc, out = execute(dry=False, batch="22222222-2222-3333-4444-555555555555", approved=APPROVED)
    check("second run refused (one-time)", rc != 0 and has(out, "BacklogDistributionBatch audit row already exists"), out[-200:])
    rc, out = execute(dry=True, batch="33333333-2222-3333-4444-555555555555", AllowSecondBatch=1)
    check("with explicit override, a re-run is a no-op for the dealt tickets only (idempotence is guarded by the batch row)", rc == 0 or has(out, "NOTHING TO DO"), out[-200:])
    check("no ticket has >1 current assignment", scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")
    return pre

def t05_rollback_clean():
    print("T05 rollback of an untouched batch restores the exact original state (previous owners and previous assignment rows included)")
    restore(); pre = state()
    plan, origin, cc, agents = oracle()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    mid = digest(state())
    rc, out = rollback(BATCH, dry=True)
    check("rollback dry run exits 0, changes nothing", rc == 0 and digest(state()) == mid and has(out, "DRY RUN complete"), out[-300:])
    check("dry run plans %d rollbacks, %d department restores, %d previous owners, 0 skipped" % (ELIG, MOVED, REASS), re.search(r"\|%d\|%d\|%d\|%d\|0\|" % (ELIG, ELIG, MOVED, REASS), out) is not None, out[-500:])
    rc, out = rollback(BATCH, dry=False)
    check("rollback commits", rc == 0 and has(out, "ROLLBACK COMMITTED. Released: %d" % ELIG), out[-300:])
    post = state()
    check("Tickets table identical to pre-batch (previous owner, department, everything)", post["Tickets"] == pre["Tickets"])
    check("every previously OWNED ticket has its previous owner back", all(
          scalar("SELECT ISNULL(LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)),'none') FROM dbo.Tickets WHERE TicketId=%d" % t) == (PREV[t][0] or "none") for t in list(plan)[:40]))
    # assignments: pre-existing rows byte-identical INCLUDING IsCurrent (previous current rows are current again); batch rows retired, not deleted
    check("every pre-existing assignment row is byte-identical again, IsCurrent included", all(r in post["TicketAssignments"] for r in pre["TicketAssignments"]))
    check("assignment rows only grew: the %d batch rows stay as retired history" % ELIG, len(post["TicketAssignments"]) == len(pre["TicketAssignments"]) + ELIG)
    check("batch assignment rows retired (IsCurrent=0), not deleted", scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE IsCurrent=0 AND AssigningActorEmployeeId IS NULL AND AssignedAtUtc=(SELECT OccurredAtUtc FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch')") == str(ELIG))
    cur = {int(r[0]): int(r[1]) for r in rows("SELECT TicketId, TicketAssignmentId FROM dbo.TicketAssignments WHERE IsCurrent=1")}
    check("each restored ticket has exactly its previous current assignment (or none, if it had none)", all(cur.get(t) == PREV[t][1] for t in plan))
    check("all earlier audit rows intact", all(r in post["AuditEntries"] for r in pre["AuditEntries"]))
    check("all earlier workflow events intact", all(r in post["TicketWorkflowEvents"] for r in pre["TicketWorkflowEvents"]))
    check("rotation cursor restored to pre-batch", scalar("SELECT COUNT(*) FROM dbo.DepartmentAssignmentRotations WHERE LastAssignedEmployeeId IS NULL") == "1")
    check("history / notes / approvals / interactions untouched", all(post[t] == pre[t] for t in ("TicketStatusHistory", "TicketNotes", "TicketApprovals", "TicketInteractions")))
    nrev = sum(1 for t in plan if origin[t] != cc or PREV[t][0])
    check("reversal events recorded for the %d moved-or-reassigned tickets, receiving employee = previous owner" % nrev,
          scalar("SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE Note LIKE 'Rollback of one-time%'") == str(nrev) and
          scalar("SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE Note LIKE 'Rollback of one-time%' AND ToEmployeeId IS NOT NULL") == str(REASS))
    rc, out = rollback(BATCH, dry=False)
    check("second rollback of same batch refused", rc != 0 and has(out, "already been rolled back"), out[-200:])
    rc, out = rollback("99999999-9999-9999-9999-999999999999", dry=True)
    check("rollback with unknown BatchId refused", rc != 0 and has(out, "expected exactly one BacklogDistributionBatch"), out[-200:])
    # after a rollback the one-time guard still blocks a casual re-run, but the preview sees the backlog again
    rc, out = execute(dry=True)
    check("re-run after rollback still needs explicit @AllowSecondBatch", rc != 0 and has(out, "already exists"))
    rc, out = execute(dry=True, AllowSecondBatch=1)
    check("with override the backlog is eligible again (%d)" % ELIG, has(out, "Tickets that would have been assigned: %d" % ELIG), out[-300:])

def must(sql):
    rc, out = run_text(sql)
    if rc != 0: raise RuntimeError("test setup failed (this would silently invalidate the test):\n" + out[:600])

def t06_rollback_touched():
    print("T06 rollback skips tickets touched since the batch")
    restore(); pre = state()
    plan, origin, cc, agents = oracle()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    owned = [t for t in sorted(plan) if PREV[t][0]]
    unowned = [t for t in sorted(plan) if not PREV[t][0]]
    reassigned, statused, noted, transferred, handled, evented, responded, started = owned[0], unowned[0], owned[1], owned[2], unowned[1], owned[3], unowned[2], owned[4]
    SUPV = "a1000000-0000-0000-0000-000000000001"
    OTHER = A3 if plan[reassigned] != A3 else A2                                                                   # never the planned agent
    fin = scalar("SELECT DepartmentId FROM dbo.Departments WHERE Code='FIN'")
    must(f"""
      SET QUOTED_IDENTIFIER ON;
      -- 1 reassigned by a supervisor to another agent
      UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={reassigned} AND IsCurrent=1;
      INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent)
        VALUES({reassigned},'{OTHER}',{cc},DATEADD(second,5,SYSUTCDATETIME()),'{SUPV}',1);
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{OTHER}' WHERE TicketId={reassigned};
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
      -- 8 the new owner started the ticket: status Open -> InProgress (with its history row)
      UPDATE dbo.Tickets SET TicketStatus=2 WHERE TicketId={started};
      INSERT dbo.TicketStatusHistory(TicketId,Dimension,OldValue,NewValue,ActorEmployeeId,ActorIsSystem,CorrelationId,OccurredAtUtc)
        VALUES({started},1,1,2,'{plan[started]}',0,NEWID(),DATEADD(minute,1,SYSUTCDATETIME()));
    """)
    touched = {reassigned, statused, noted, transferred, handled, evented, responded, started}
    before = state()
    rc, out = rollback(BATCH, dry=True)
    check("dry run plans %d releases and %d skips" % (ELIG - len(touched), len(touched)), rc == 0 and has(out, "|%d|" % (ELIG - len(touched))) and digest(state()) == digest(before), out[-500:])
    rc, out = rollback(BATCH, dry=False)
    check("rollback commits", rc == 0, out[-400:])
    check("reports %d released" % (ELIG - len(touched)), has(out, "Released: %d" % (ELIG - len(touched))), out[-300:])
    st = state(); tk = {r[0]: r for r in st["Tickets"]}; bk = {r[0]: r for r in before["Tickets"]}
    check("each of the %d touched tickets is byte-identical to how the human left it" % len(touched), all(tk[str(t)] == bk[str(t)] for t in touched),
          [t for t in touched if tk[str(t)] != bk[str(t)]])
    check("no rollback audit / reversal event / assignment retirement for touched tickets",
          scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action IN ('AutoAssignRollback','Transfer') AND CorrelationId<>'%s' AND AuditEntryId>(SELECT AuditEntryId FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch') AND EntityId IN (%s)" % (BATCH, ",".join(map(str, touched)))) == "0"
          and all(a in st["TicketAssignments"] for a in before["TicketAssignments"] if a[1] in {str(t) for t in touched}))
    released = [t for t in plan if t not in touched]
    pre_tk = {r[0]: r for r in pre["Tickets"]}
    check("all %d untouched tickets are back to pre-batch owner AND department" % len(released), all(tk[str(t)] == pre_tk[str(t)] for t in released),
          [t for t in released if tk[str(t)] != pre_tk[str(t)]][:5])
    rel = {str(t) for t in released}
    check("...and their assignment rows are the pre-batch ones again (previous owner's row current)",
          all(r in st["TicketAssignments"] for r in pre["TicketAssignments"] if r[1] in rel))
    for reason in ("owner changed since the batch", "transferred out of Call Center", "worked since the batch", "no longer Open"):
        check("skip reason reported: " + reason, has(out, reason))
    check("rotation cursor restored to pre-batch value (nobody was dealt a ticket since)", scalar("SELECT COUNT(*) FROM dbo.DepartmentAssignmentRotations WHERE LastAssignedEmployeeId IS NULL") == "1")
    check("every ticket has <=1 current assignment", scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")

def t06b_include_worked():
    print("T06b @IncludeWorked relaxes only the 'worked' test")
    restore()
    plan, origin, cc, agents = oracle()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    owned = [t for t in sorted(plan) if PREV[t][0]]
    worked, reassigned = owned[0], owned[1]
    must(f"""
      SET QUOTED_IDENTIFIER ON;
      INSERT dbo.TicketNotes(TicketId,NoteText,AuthorEmployeeId,CreatedAtUtc) VALUES({worked},N'note','{plan[worked]}',DATEADD(minute,1,SYSUTCDATETIME()));
      UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={reassigned} AND IsCurrent=1;
      INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) VALUES({reassigned},'{A4}',{cc},DATEADD(second,5,SYSUTCDATETIME()),NULL,1);
      UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{A4}' WHERE TicketId={reassigned};""")
    rc, out = rollback(BATCH, dry=False, IncludeWorked=1)
    check("IncludeWorked=1 releases the worked ticket but still skips the reassigned one", rc == 0 and has(out, "Released: %d" % (ELIG - 1)), out[-300:])
    check("reassigned ticket still owned by the human's choice", scalar(f"SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketId={reassigned}") == A4)
    check("the released worked ticket has its previous owner back", scalar(f"SELECT ISNULL(LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)),'none') FROM dbo.Tickets WHERE TicketId={worked}") == PREV[worked][0])

def t07_concurrency():
    print("T07 concurrency")
    restore(); pre = state()
    plan, origin, cc, agents = oracle()
    slow = patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], BatchId="'%s'" % BATCH, **approval_params(APPROVED))
    slow = slow.replace("    -- (h) report", "    WAITFOR DELAY '00:00:14';\n    -- (h) report", 1)
    assert "WAITFOR DELAY" in slow
    victim = [t for t in sorted(plan) if PREV[t][0]][5]                       # an OWNED ticket: a person tries to reassign it while the batch runs
    unowned_victim = [t for t in sorted(plan) if not PREV[t][0]][3]
    rv = scalar("SELECT CONVERT(varchar(20),CAST(RowVersion AS binary(8)),1) AS rv FROM dbo.Tickets WHERE TicketId=%d" % victim)          # what that person's screen loaded
    res = {}
    def a(): res["A"] = run_text(slow)
    th = threading.Thread(target=a); th.start(); time.sleep(5)      # A now holds app lock, cursor lock and the ticket locks
    resB, resC, resD = {}, {}, {}
    def runB():
        t0 = time.time(); resB["r"] = execute(dry=False, batch="44444444-2222-3333-4444-555555555555", approved=APPROVED); resB["t"] = time.time() - t0
    def runC():      # the app's optimistic reassignment: UPDATE ... WHERE RowVersion = <value read earlier>
        t0 = time.time()
        resC["r"] = run_text(f"SET LOCK_TIMEOUT -1; SET NOCOUNT OFF; UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{A3}' WHERE TicketId={victim} AND RowVersion={rv};")
        resC["t"] = time.time() - t0
    def runD():      # a second, unguarded writer racing for an UNOWNED eligible ticket
        resD["r"] = run_text(f"SET LOCK_TIMEOUT -1; SET NOCOUNT OFF; UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{A3}' WHERE TicketId={unowned_victim} AND CurrentOwnerEmployeeId IS NULL;")
    ths = [threading.Thread(target=f) for f in (runB, runC, runD)]
    for t in ths: t.start()
    for t in ths: t.join()
    th.join()
    rcB, outB = resB["r"]; tB = resB["t"]; rcC, outC = resC["r"]; tC = resC["t"]; rcD, outD = resD["r"]
    check("a second concurrent run fails fast on the application lock", rcB != 0 and has(outB, "application lock") and tB < 8, (round(tB, 1), outB[-200:]))
    check("a person's concurrent reassignment of an OWNED ticket blocks until the batch commits", tC > 3, round(tC, 1))
    check("...and then affects 0 rows (the RowVersion it loaded is stale: it cannot overwrite the batch's owner)", "(0 rows affected)" in outC, outC[-200:])
    check("a racing writer for an unowned ticket also affects 0 rows", "(0 rows affected)" in outD, outD[-200:])
    rcA, outA = res["A"]
    check("the batch itself committed", rcA == 0 and has(outA, "COMMITTED"), outA[-300:])
    owner = scalar(f"SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketId={victim}")
    check("victim ticket kept the oracle-planned owner", owner == plan[victim], (owner, plan[victim]))
    check("exactly one current assignment per ticket", scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")
    # the filtered unique index is the last line of defence
    rc, out = run_text(f"INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,IsCurrent) VALUES({victim},'{A3}',{cc},SYSUTCDATETIME(),1)")
    check("a duplicate CURRENT assignment is rejected by the unique index", rc != 0 and has(out, "unique index"), out[-200:])

def oracle_agents(): return set(oracle(service_ids=(SVC,))[3]) | {A4}

def t08_race_between_preview_and_run():
    print("T08 backlog changes between preview and execution")
    restore()
    run_text(f"UPDATE dbo.Tickets SET CurrentOwnerEmployeeId='{A1}' WHERE TicketNumber='E-0020'; "
             "INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) "
             f"SELECT TicketId,'{A1}',CurrentDepartmentId,SYSUTCDATETIME(),'{A1}',1 FROM dbo.Tickets WHERE TicketNumber='E-0020'")
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED)
    check("a ticket taken by a person after the preview changes the owner-replaced count -> run refused, nothing changed", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-300:])
    rc, out = execute(dry=False, batch=BATCH, approved=A(r=REASS + 1))
    check("re-approved with the new numbers it commits: that person's ownership is replaced (explicit scope)",
          rc == 0 and scalar("SELECT LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)) FROM dbo.Tickets WHERE TicketNumber='E-0020'") in oracle_agents(), out[-300:])
    check("...the person's own assignment row survives as superseded history", scalar("SELECT COUNT(*) FROM dbo.TicketAssignments a JOIN dbo.Tickets t ON t.TicketId=a.TicketId WHERE t.TicketNumber='E-0020' AND a.IsCurrent=0 AND a.AssignedEmployeeId='%s'" % A1) == "1")
    restore()
    run_text("UPDATE dbo.Tickets SET TicketStatus=2 WHERE TicketNumber='E-0021'")
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED)
    check("a ticket started (Open -> InProgress) after the preview leaves the scope -> run refused, nothing changed", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-300:])
    rc, out = execute(dry=False, approved=A(t=ELIG - 1, m=MOVED - 1))
    check("re-approved it commits and the InProgress ticket is untouched", rc == 0 and scalar("SELECT CurrentOwnerEmployeeId FROM dbo.Tickets WHERE TicketNumber='E-0021'") == "NULL", out[-300:])

def commit_vs_oracle(label, rc_out, plan, extra_where="TicketStatus=1"):
    rc, out = rc_out
    got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE %s" % extra_where)}
    check(label, rc == 0 and all(got[t] == a for t, a in plan.items()), out[-300:])

def t09_cursor_and_pool_edges():
    print("T09 cursor / pool edge cases vs oracle")
    for label, setup in [
        ("cursor in the middle", "UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId='%s'" % A3),
        ("cursor on the last agent (wraps)", "UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId='%s'" % A3),
        ("cursor on an ineligible/deactivated agent", "UPDATE dbo.DepartmentAssignmentRotations SET LastAssignedEmployeeId='a4000000-0000-0000-0000-000000000001'"),
        ("one eligible agent only", "DELETE FROM dbo.UserDepartmentAssignments WHERE EmployeeId IN ('%s','%s','f0000000-0000-0000-0000-000000000009')" % (A2, A3)),
    ]:
        restore()
        run_text("IF NOT EXISTS (SELECT 1 FROM dbo.DepartmentAssignmentRotations) INSERT dbo.DepartmentAssignmentRotations(DepartmentId,LastAssignedEmployeeId,UpdatedAtUtc) SELECT DepartmentId,NULL,SYSUTCDATETIME() FROM dbo.Departments WHERE Code='CC';\n" + setup)
        plan, origin, cc, agents = oracle()
        n, mv, rs = counts_of(plan, origin, cc)
        rc_out = execute(dry=False, batch=BATCH, approved=(n, mv, rs, len(agents), HELD_APPR, HELD_AI))
        commit_vs_oracle("%s: commit equals oracle for all %d tickets (%d agent(s))" % (label, len(plan), len(agents)), rc_out, plan)
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
    print("T10 scale: %d extra Open tickets (a third of them owned) plus a large closed population" % n)
    restore()
    must(f"""
      SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON;
      DECLARE @d TABLE (id int, rn int); INSERT @d SELECT DepartmentId, ROW_NUMBER() OVER (ORDER BY DepartmentId) FROM dbo.Departments;
      ;WITH x AS (SELECT TOP ({n}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
      INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
      SELECT N'S-' + CONVERT(nvarchar(10), i), d.id, d.id, CASE WHEN i % 3 = 0 THEN '{FINEMP}' END, CASE WHEN i % 5 = 0 THEN 2 ELSE 1 END, 0, 0, 1, N'scale', 0, DATEADD(second, i, '2026-06-01') FROM x CROSS APPLY (SELECT id FROM @d WHERE rn = (i % 5) + 1) d;
      INSERT dbo.TicketAssignments (TicketId, AssignedEmployeeId, AssignedDepartmentId, AssignedAtUtc, AssigningActorEmployeeId, IsCurrent)
      SELECT TicketId, CurrentOwnerEmployeeId, CurrentDepartmentId, SYSUTCDATETIME(), CurrentOwnerEmployeeId, 1 FROM dbo.Tickets WHERE TicketNumber LIKE N'S-%' AND CurrentOwnerEmployeeId IS NOT NULL;
      -- a big closed population that must never be read into the plan
      ;WITH x AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
      INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
      SELECT N'C-' + CONVERT(nvarchar(10), i), 3, 3, NULL, 6, 0, 0, 3, N'closed', 0, '2026-01-01' FROM x;
      INSERT dbo.AuditEntries (ActorEmployeeId, Action, EntityType, EntityId, BeforeValue, AfterValue, CorrelationId, OccurredAtUtc)
      SELECT TOP (300000) NULL, N'Create', N'Ticket', CONVERT(nvarchar(100), TicketId), NULL, N'seed', NEWID(), CreatedAtUtc FROM dbo.Tickets;
    """)
    plan, origin, cc, agents = oracle()
    base, pend, ai_only = held_sets()
    total, moved, reass = counts_of(plan, origin, cc)
    sig = "SELECT CONVERT(varchar(20), CHECKSUM_AGG(CHECKSUM(TicketId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, SlaState, OriginatingDepartmentId))) FROM dbo.Tickets"
    asg_max = scalar("SELECT MAX(TicketAssignmentId) FROM dbo.TicketAssignments")      # rows added by the batch (retired again by rollback) are history and excluded
    asg_sig = "SELECT CONVERT(varchar(20), CHECKSUM_AGG(CHECKSUM(TicketAssignmentId, TicketId, AssignedEmployeeId, IsCurrent))) FROM dbo.TicketAssignments WHERE TicketAssignmentId <= " + asg_max
    sig0, asg0 = scalar(sig), scalar(asg_sig)
    print("   eligible=%d moved=%d owned=%d held=%d+%d audit rows=%s" % (total, moved, reass, len(pend), len(ai_only), scalar("SELECT COUNT(*) FROM dbo.AuditEntries")))
    t0 = time.time(); rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC]).replace("ORDER BY c.Seq;", "ORDER BY c.Seq OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY;")); tp = time.time() - t0
    check("preview completes (%.1fs)" % tp, rc == 0 and "READY" in out, out[-300:])
    t0 = time.time(); rc, out = execute(dry=True); td = time.time() - t0
    check("dry run completes (%.1fs) and rolls back" % td, rc == 0 and has(out, "ROLLED BACK"), out[-300:])
    check("dry run changed nothing", scalar(sig) == sig0 and scalar(asg_sig) == asg0)
    t0 = time.time(); rc, out = execute(dry=False, batch=BATCH, approved=(total, moved, reass, len(agents), len(pend), len(ai_only))); tx = time.time() - t0
    check("commit of %d tickets (%d owner-replaced) completes (%.1fs)" % (total, reass, tx), rc == 0 and has(out, "COMMITTED"), out[-300:])
    got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE TicketStatus=1")}
    check("all %d tickets match the independent oracle" % len(plan), all(got[t] == a for t, a in plan.items()))
    check("held-back tickets untouched (no batch audit row)", scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE CorrelationId='%s' AND EntityId IN (%s)" % (BATCH, ",".join(map(str, pend | ai_only)))) == "0")
    counts = {}
    for a in plan.values(): counts[a] = counts.get(a, 0) + 1
    check("fair at scale: spread <= 1 %s" % sorted(counts.values()), max(counts.values()) - min(counts.values()) <= 1)
    check("no duplicate current assignments", scalar("SELECT COUNT(*) FROM (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 GROUP BY TicketId HAVING COUNT(*)>1) x") == "0")
    check("the 100000 closed tickets were never touched", scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketNumber LIKE 'C-%' AND CurrentOwnerEmployeeId IS NULL AND CurrentDepartmentId=3") == "100000")
    t0 = time.time(); rc, out = rollback(BATCH, dry=False); tr = time.time() - t0
    check("rollback of %d tickets completes (%.1fs)" % (total, tr), rc == 0 and has(out, "Released: %d" % total), out[-300:])
    check("after rollback the Tickets and TicketAssignments signatures equal the pre-batch ones (previous owners and rows restored)", scalar(sig) == sig0 and scalar(asg_sig) == asg0, (scalar(sig), sig0, scalar(asg_sig), asg0))
    print("   timings: preview %.1fs, dry-run %.1fs, commit %.1fs, rollback %.1fs" % (tp, td, tx, tr))

def snapshot_tables(names):
    """Full dump of tables (without rowversion), ordered by their first column (the key)."""
    return {n: table_dump(n, rows("SELECT TOP 1 name FROM sys.columns WHERE object_id=OBJECT_ID('dbo.%s') ORDER BY column_id" % n)[0][0]) for n in names}

def ids(names):
    return {int(r[0]) for r in rows("SELECT TicketId FROM dbo.Tickets WHERE TicketNumber IN (%s)" % ",".join("'%s'" % n for n in names))}

HELD_APR_NAMES = ["H-APR-%d" % i for i in range(1, 7)] + ["H-BOTH-1", "O-APR-1"]
HELD_AI_NAMES = ["H-AI-%d" % i for i in range(1, 6)] + ["O-AI-1"]

def t11_pending_approvals():
    print("T11 pending approvals are held back (owned or not), listed separately, and never touched")
    restore(); pre = state(); pre_x = snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketAgentHandoffs"])
    plan, origin, cc, agents = oracle()
    appr = ids(HELD_APR_NAMES); ai = ids(HELD_AI_NAMES)
    decided = ids(["F-DEC-1", "F-DEC-2", "F-DEC-3"])
    check("oracle excludes all pending-approval tickets (incl. the owned one and the one that is also an AI conversation)", not (appr & set(plan)) and len(appr) == HELD_APPR)
    check("InProgress tickets with a pending approval / live AI conversation are out of scope by status and not counted as held back", not (ids(["N-APR-1", "N-AI-1"]) & (appr | ai | set(plan))))
    check("decided approvals (approved / rejected / cancelled) do NOT block", decided <= set(plan))
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC]))
    held_lines = [l for l in out.splitlines() if "PENDING APPROVAL" in l or "ACTIVE AI CONVERSATION" in l]
    nums = {l.split("|")[1] for l in held_lines}
    check("preview lists every held-back ticket once with its reason", len(held_lines) == HELD_APPR + HELD_AI and set(HELD_APR_NAMES + HELD_AI_NAMES) <= nums, len(held_lines))
    check("a ticket with both is listed under the approval and flagged 'also has an active AI conversation'", any("H-BOTH-1" in l and "PENDING APPROVAL" in l and "also has an active AI" in l for l in held_lines))
    check("held-back owned tickets show their current owner (who keeps them)", any("O-APR-1" in l and "FINEMP Person" in l for l in held_lines) and any("O-AI-1" in l and "SUPEMP Person" in l for l in held_lines))
    check("pending approval text shows type and since-date", any("H-APR-1" in l and "Accounting (since " in l for l in held_lines))
    plan_nums = {l.split("|")[2] for l in out.splitlines() if re.match(r"^\d+\|\d+\|[A-Z]-", l)}
    check("no held-back ticket appears in the preview's plan", not any(n.startswith("H-") or n in ("O-APR-1", "O-AI-1") for n in plan_nums))
    check("verdict row carries the held-back counts", re.search(r"\|%d\|%d\|0\|" % (HELD_APPR, HELD_AI), out) is not None)
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit succeeds with the six approved counts", rc == 0 and has(out, "COMMITTED"), out[-300:])
    post = state(); tk = {r[0]: r for r in post["Tickets"]}; pk = {r[0]: r for r in pre["Tickets"]}
    held = appr | ai
    check("every held-back ticket row is byte-identical (department, owner, status, everything)", all(tk[str(t)] == pk[str(t)] for t in held))
    hs = ",".join(map(str, held))
    check("held-back tickets: assignment rows byte-identical, and no assignment row, audit row or workflow event from the batch",
          all(r in post["TicketAssignments"] for r in pre["TicketAssignments"] if r[1] in {str(t) for t in held}) and
          scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE TicketId IN (%s) AND AssignedAtUtc=(SELECT OccurredAtUtc FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch')" % hs) == "0" and
          scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE CorrelationId='%s' AND EntityId IN (%s)" % (BATCH, hs)) == "0" and
          scalar("SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE CorrelationId='%s' AND TicketId IN (%s)" % (BATCH, hs)) == "0")
    check("decided-approval tickets WERE moved and assigned", all(tk[str(t)] != pk[str(t)] for t in decided))
    post_x = snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketAgentHandoffs"])
    check("TicketApprovals / TicketInteractions / TicketAgentHandoffs are byte-identical (nothing in approval flows or AI conversations touched)", post_x == pre_x)
    check("batch audit row records the held-back and reassigned counts", scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch' AND AfterValue LIKE '%%HeldBackPendingApproval=%d;HeldBackActiveAi=%d;Reassigned=%d'" % (HELD_APPR, HELD_AI, REASS)) == "1")
    rc, out = rollback(BATCH, dry=False)
    check("rollback releases exactly the batch", rc == 0 and has(out, "Released: %d" % ELIG), out[-300:])
    check("after rollback the whole Tickets table equals the pre-batch table (held-back tickets never moved)", state()["Tickets"] == pre["Tickets"])

def t11b_approval_decided_between_preview_and_run():
    print("T11b approval decided / requested between preview and run")
    restore()
    run_text("UPDATE dbo.TicketApprovals SET Status=2 WHERE TicketId=(SELECT TicketId FROM dbo.Tickets WHERE TicketNumber='H-APR-1')")   # approved -> now eligible
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED)
    check("an approval decided after the preview changes the counts -> run refused, nothing changed", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-250:])
    rc, out = execute(dry=False, approved=A(t=ELIG + 1, m=MOVED + 1, ha=HELD_APPR - 1))
    check("re-approved with the new numbers it commits and now moves that ticket", rc == 0 and scalar("SELECT CurrentOwnerEmployeeId FROM dbo.Tickets WHERE TicketNumber='H-APR-1'") != "NULL", out[-250:])
    restore()
    run_text("INSERT dbo.TicketApprovals (TicketId, ApprovalType, Status, TargetKind, RequestedByEmployeeId, RequestedAtUtc, IsCurrent, CorrelationId) "
             "SELECT TicketId, 1, 1, 1, 'b0000000-0000-0000-0000-000000000001', SYSUTCDATETIME(), 1, NEWID() FROM dbo.Tickets WHERE TicketNumber='O-FIN-1'")  # new pending approval on an OWNED eligible ticket
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED)
    check("a NEW pending approval after the preview (on an owned ticket) -> run refused, nothing changed", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before)

def t12_active_ai():
    print("T12 active AI conversations are held back (owned or not) and preserved")
    restore(); pre = state(); pre_x = snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketAgentHandoffs"])
    plan, origin, cc, agents = oracle()
    ai = ids(HELD_AI_NAMES)
    eligible_ai = ids(["F-AIEND-1", "F-AIEND-2", "F-AIHUM-1", "F-AIHUM-2", "F-AIHAND-1", "F-AITAKEN-1", "F-AIINACT-1"])
    check("AI tickets that are ended / need a human / have an open handoff / were taken / inactivity-closed ARE eligible", eligible_ai <= set(plan))
    check("live AI-only tickets are NOT in the plan (incl. the owned one)", not (ai & set(plan)) and len(ai) == HELD_AI)
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit succeeds", rc == 0 and has(out, "COMMITTED"), out[-300:])
    post = state(); tk = {r[0]: r for r in post["Tickets"]}; pk = {r[0]: r for r in pre["Tickets"]}
    check("active AI tickets byte-identical (department, owner, status, SLA, everything)", all(tk[str(t)] == pk[str(t)] for t in ai))
    check("AI interactions / handoffs / approvals byte-identical after the batch", snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketAgentHandoffs"]) == pre_x)
    restore()
    run_text("UPDATE dbo.TicketInteractions SET EndedAtUtc=SYSUTCDATETIME() WHERE TicketId=(SELECT TicketId FROM dbo.Tickets WHERE TicketNumber='O-AI-1')")
    rc, out = execute(dry=False, approved=APPROVED)
    check("an AI conversation that ended after the preview changes the counts -> refused", rc != 0 and has(out, "backlog changed since the preview"), out[-250:])
    restore()
    plan, origin, cc, agents = oracle()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    tid = sorted(plan); a_t, i_t = tid[0], tid[1]
    must(f"""SET QUOTED_IDENTIFIER ON;
      INSERT dbo.TicketApprovals (TicketId, ApprovalType, Status, TargetKind, RequestedByEmployeeId, RequestedAtUtc, IsCurrent, CorrelationId)
        VALUES({a_t}, 1, 1, 1, 'b0000000-0000-0000-0000-000000000001', DATEADD(minute,1,SYSUTCDATETIME()), 1, NEWID());
      INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, RequiresHumanFollowUp)
        VALUES({i_t}, 0, 2, 2, N'0500000001', N'late-conv', DATEADD(minute,1,SYSUTCDATETIME()), 0);""")
    before = state()
    rc, out = rollback(BATCH, dry=False)
    st = state(); tk = {r[0]: r for r in st["Tickets"]}; bk = {r[0]: r for r in before["Tickets"]}
    check("a ticket that entered an approval flow or got a new AI conversation after the batch is skipped, byte-identical",
          rc == 0 and has(out, "Released: %d" % (ELIG - 2)) and tk[str(a_t)] == bk[str(a_t)] and tk[str(i_t)] == bk[str(i_t)], out[-300:])

def window_run(inject_marker, extra_sql_for_window, wait=12, settle=5):
    """Run a real commit whose transaction pauses (WAITFOR) just before `inject_marker`; execute `extra_sql_for_window` from other sessions meanwhile."""
    slow = patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], BatchId="'%s'" % BATCH, **approval_params(APPROVED))
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

def t13_concurrent_safeguards():
    print("T13 concurrent assignment safeguards")
    marker = "    -- (g0) RE-CHECKS after the writes."
    early = "    -- (f) writes"
    appr_sql = lambda v: (f"INSERT dbo.TicketApprovals (TicketId, ApprovalType, Status, TargetKind, RequestedByEmployeeId, RequestedAtUtc, IsCurrent, CorrelationId) "
                          f"VALUES({v}, 1, 1, 1, 'b0000000-0000-0000-0000-000000000001', SYSUTCDATETIME(), 1, NEWID())")
    ai_sql = lambda v: (f"INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, RequiresHumanFollowUp) "
                        f"VALUES({v}, 0, 2, 2, N'0500000002', N'mid-run', SYSUTCDATETIME(), 0)")
    for label, mk, code in (("pending approval", appr_sql, "50340"), ("active AI conversation", ai_sql, "50341")):
        restore(); pre = digest(state()); plan, origin, cc, agents = oracle(); victim = [t for t in sorted(plan) if PREV[t][0]][3]    # an OWNED ticket
        (rcA, outA), oth = window_run(early, {"w": mk(victim)})
        (rcB, outB), tB = oth["w"]
        check("a %s created BEFORE the update lands on an owned ticket (competing insert succeeds)" % label, rcB == 0, outB[-200:])
        check("...so the re-check aborts the WHOLE run (Msg %s)" % code, rcA != 0 and ("Msg " + code) in outA, outA[-300:])
        after = digest(state())
        own = "TicketApprovals" if code == "50340" else "TicketInteractions"          # the competing session's own row stays; everything else must be identical
        check("...and nothing else was changed (tickets, assignments, audit, events, cursor identical)", all(after[t] == pre[t] for t in pre if t != own), [t for t in pre if pre[t] != after[t] and t != own])
    late = "    -- (g0) RE-CHECKS after the writes."
    for label, mk in (("pending approval", appr_sql), ("active AI conversation", ai_sql)):
        restore(); pre = digest(state()); plan, origin, cc, agents = oracle(); victim = sorted(plan)[7]
        (rcA, outA), oth = window_run(late, {"w": mk(victim)})
        (rcB, outB), tB = oth["w"]
        committed = rcA == 0 and has(outA, "COMMITTED")
        blocked_or_lost = (rcB != 0 and "1205" in outB) or tB > 6
        if committed:
            check("%s inserted AFTER the update: batch committed and the competing writer was blocked / lost the deadlock (%.1fs)" % (label, tB), blocked_or_lost, (round(tB, 1), outB[-150:]))
        else:
            check("%s inserted AFTER the update: batch aborted atomically (deadlock victim or re-check), nothing changed" % label,
                  digest(state()) == pre, outA[-200:])
        moved_and_flagged = scalar("SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.IsCurrent=1 AND a.AssignedAtUtc=ISNULL((SELECT OccurredAtUtc FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch'),'1900-01-01') "
                                   "AND EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId=a.TicketId AND ap.Status=1 AND ap.RequestedAtUtc < a.AssignedAtUtc)")
        check("...and no batch ticket carries a pending approval that predates the commit", moved_and_flagged == "0")
    # (b) a competing ASSIGNMENT row appears for an owned ticket between selection and the write (same owner re-assigned: the ticket row itself is not touched)
    restore(); pre = digest(state()); plan, origin, cc, agents = oracle(); victim = [t for t in sorted(plan) if PREV[t][1]][2]
    owner = PREV[victim][0] or FINEMP
    racing = (f"SET QUOTED_IDENTIFIER ON; UPDATE dbo.TicketAssignments SET IsCurrent=0 WHERE TicketId={victim} AND IsCurrent=1; "
              f"INSERT dbo.TicketAssignments(TicketId,AssignedEmployeeId,AssignedDepartmentId,AssignedAtUtc,AssigningActorEmployeeId,IsCurrent) "
              f"SELECT TicketId,'{owner}',CurrentDepartmentId,SYSUTCDATETIME(),'{owner}',1 FROM dbo.Tickets WHERE TicketId={victim}")
    (rcA, outA), oth = window_run(early, {"asg": racing})
    (rcR, outR), tR = oth["asg"]
    check("a competing assignment row inserted between selection and the write (competing writer succeeds)", rcR == 0, outR[-200:])
    check("...the run aborts on the superseded-rows check (Msg 50317); the competing person's row is NOT silently retired", rcA != 0 and "Msg 50317" in outA, outA[-300:])
    check("...whole run rolled back: no batch row, no ticket moved", scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch'") == "0" and
          scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE CurrentDepartmentId=%d AND TicketId IN (%s)" % (cc, ",".join(str(t) for t in plan if origin[t] != cc))) == "0")
    # (c) a new owner becomes ineligible (locked out) mid-run
    restore(); pre = digest(state())
    (rcA, outA), oth = window_run(marker, {"lock": f"UPDATE dbo.AspNetUsers SET LockoutEnd=DATEADD(year,1,SYSDATETIMEOFFSET()) WHERE Id='{A3}'"})
    check("an agent locked out mid-run aborts the WHOLE run (Msg 50342) and changes nothing", rcA != 0 and "Msg 50342" in outA and digest(state()) == pre, outA[-300:])
    # (d) deactivating an agent is blocked until the batch commits (their row is locked), so the pool cannot change under the run
    restore()
    (rcA, outA), oth = window_run(marker, {"deactivate": f"SET LOCK_TIMEOUT -1; UPDATE dbo.Employees SET DeactivatedAtUtc=SYSUTCDATETIME() WHERE EmployeeId='{A2}'"})
    (rcD, outD), tD = oth["deactivate"]
    check("deactivating an agent waits for the batch (blocked %.1fs) instead of changing the pool mid-run" % tD, tD > 4 and rcD == 0, (round(tD, 1), outD[-150:]))
    check("the batch itself committed", rcA == 0 and has(outA, "COMMITTED"), outA[-300:])
    # (e) live Call Center intake (same cursor lock as the app) waits, then sees the ADVANCED cursor
    restore()
    (rcA, outA), oth = window_run("    -- (h) report", {"intake": "SET LOCK_TIMEOUT -1; SET NOCOUNT ON; SELECT LOWER(CONVERT(nvarchar(36),LastAssignedEmployeeId)) AS c FROM dbo.DepartmentAssignmentRotations WITH (UPDLOCK, ROWLOCK) WHERE DepartmentId=(SELECT DepartmentId FROM dbo.Departments WHERE Code='CC')"})
    (rcI, outI), tI = oth["intake"]
    cursor = scalar("SELECT LOWER(CONVERT(nvarchar(36),LastAssignedEmployeeId)) FROM dbo.DepartmentAssignmentRotations")
    check("live intake's cursor lock waits for the batch (%.1fs) and then reads the advanced cursor" % tI, tI > 4 and cursor in outI.lower(), (round(tI, 1), outI[-120:]))
    # (f) exactly one eligible owner per transferred ticket, nothing owned by an excluded account
    bad = scalar("SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus=1 AND t.CurrentOwnerEmployeeId IN (SELECT EmployeeId FROM dbo.Employees WHERE DisplayName IN ('DEACT Person','LOCK Person','SVC Person','RO Person','SUPV Person','CHAIR Person','A4 Person'))")
    check("no Open ticket is owned by an excluded account", bad == "0")
    check("every batch ticket has exactly one owner and one current assignment", scalar("SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketId IN (SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent=1 AND AssignedAtUtc=(SELECT OccurredAtUtc FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch')) AND (t.CurrentOwnerEmployeeId IS NULL OR (SELECT COUNT(*) FROM dbo.TicketAssignments a WHERE a.TicketId=t.TicketId AND a.IsCurrent=1)<>1)") == "0")

def t14_switches():
    print("T14 @CsAgentRoleOnly and @IncludeLiveAiConversations (both default to the safe setting; Preview and Execute must agree)")
    restore()
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC]))
    check("default: Department Employee is excluded", any("DE Person" in l and "excluded" in l for l in out.splitlines()))
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], CsAgentRoleOnly=0))
    check("@CsAgentRoleOnly = 0 alone: DE is still excluded by its explicit denial of Tickets.Close (pool stays 3)", re.search(r"\n%d\|%d\|%d\|3\|%d\|%d\s*\n" % (ELIG, MOVED, REASS, HELD_APPR, HELD_AI), out) is not None, out[-300:])
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], CsAgentRoleOnly=0, ExcludeAgentsWithTicketDenials=0))
    check("@CsAgentRoleOnly = 0 and denials ignored: the engine's full pool (A1, A2, A3, A4, DE = 5 agents)", re.search(r"\n%d\|%d\|%d\|5\|%d\|%d\s*\n" % (ELIG, MOVED, REASS, HELD_APPR, HELD_AI), out) is not None, out[-300:])
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED, CsAgentRoleOnly=0, ExcludeAgentsWithTicketDenials=0)
    check("...Execute with the DEFAULT approved agent count (3) is refused: pool differs from what was approved", rc != 0 and has(out, "ApprovedAgentCount"), out[-200:])
    plan, origin, cc, agents = oracle(cs_only=False, exclude_denied=False)
    rc_out = execute(dry=False, batch=BATCH, approved=A(a=5), CsAgentRoleOnly=0, ExcludeAgentsWithTicketDenials=0)
    commit_vs_oracle("...with 5 agents it commits and equals the oracle", rc_out, plan)
    check("...the Department Employee and A4 now own tickets", any(a.startswith("f0000000") for a in plan.values()) and any(a.startswith("7b000000") for a in plan.values()))
    restore(); plan, origin, cc, agents = oracle()
    execute(dry=False, batch=BATCH, approved=APPROVED)
    check("default: the Department Employee and the denied agent A4 own nothing", scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE CurrentOwnerEmployeeId IN ('f0000000-0000-0000-0000-000000000009','%s')" % A4) == "0")
    restore()
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], IncludeLiveAiConversations=1))
    check("@IncludeLiveAiConversations = 1: the 6 AI-only tickets become eligible and are no longer listed as held back (approvals still held)",
          re.search(r"\n%d\|%d\|%d\|%d\|%d\|0\s*\n" % (ELIG + HELD_AI, MOVED + 6, REASS + 1, AGENTS, HELD_APPR), out) is not None and "ACTIVE AI CONVERSATION" not in out, out[-300:])
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED, IncludeLiveAiConversations=1)
    check("...Execute with the default (hold-back) approved counts is refused", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-200:])
    plan, origin, cc, agents = oracle(include_ai=True)
    rc_out = execute(dry=False, batch=BATCH, approved=A(t=ELIG + HELD_AI, m=MOVED + 6, r=REASS + 1, hi=0), IncludeLiveAiConversations=1)
    commit_vs_oracle("...with the matching counts it commits and equals the oracle", rc_out, plan)
    got = {int(r[0]): r[1].lower() for r in rows("SELECT TicketId, CONVERT(nvarchar(36),CurrentOwnerEmployeeId) FROM dbo.Tickets WHERE TicketStatus=1")}
    ai = ids(HELD_AI_NAMES); appr = ids(HELD_APR_NAMES)
    check("...AI tickets moved and owned by the batch, pending approvals STILL untouched", all(got[t] in agents for t in ai) and all(got[t] not in agents for t in appr))

def t15_owned_reassignment():
    print("T15 already-owned Open tickets: ownership replaced, history preserved, My Tickets, Resolve/Close, rollback")
    restore(); pre = state()
    plan, origin, cc, agents = oracle()
    owned = [t for t in plan if PREV[t][0]]
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("commit succeeds", rc == 0 and has(out, "COMMITTED"), out[-300:])
    check("the %d owned eligible tickets span 7 previous owners: FINEMP, SUPEMP, A1, SUPV, DEACT, LOCK, SVC" % len(owned), len({PREV[t][0] for t in owned}) == 7 and len(owned) == REASS)
    fin_left = scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE CurrentOwnerEmployeeId='%s' AND TicketStatus=1" % FINEMP)
    check("FINEMP keeps only the held-back (pending approval) Open ticket; every other Open ticket moved to a Call Center agent", fin_left == "1", fin_left)
    prev_rows = {t: PREV[t][1] for t in owned if PREV[t][1] is not None}
    ok = all(scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE TicketAssignmentId=%d AND TicketId=%d AND IsCurrent=0 AND AssignedEmployeeId='%s'" % (a, t, PREV[t][0])) == "1" for t, a in prev_rows.items())
    check("every previous owner's assignment row still exists, names that owner, and is no longer current (%d rows)" % len(prev_rows), ok)
    legacy = ids(["O-LEG-1", "O-LEG-2"]); drift = ids(["D-1", "D-2", "D-3"])
    check("legacy (owner without assignment row): previous owner recorded in audit, new current row created", all(PREV[t][1] is None and PREV[t][0] == FINEMP for t in legacy) and
          all(scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE CorrelationId='%s' AND Action='AutoAssign' AND EntityId='%d' AND BeforeValue LIKE 'PreviousOwnerId=%s;PreviousAssignmentId=none;%%'" % (BATCH, t, FINEMP)) == "1" for t in legacy))
    check("data drift (no owner, stale current row): the stale row is superseded and a clean current row exists", all(PREV[t][0] is None and PREV[t][1] is not None for t in drift) and
          all(scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE TicketId=%d AND IsCurrent=1" % t) == "1" for t in drift))
    cca1 = ids(["O-CCA1-%d" % i for i in range(1, 5)])
    check("tickets already in CC and owned by an eligible agent ARE dealt again by Round Robin under the default (literal) rule: a reassignment event, no transfer audit",
          cca1 <= set(plan) and all(scalar("SELECT COUNT(*) FROM dbo.TicketWorkflowEvents WHERE CorrelationId='%s' AND TicketId=%d AND FromDepartmentId=ToDepartmentId" % (BATCH, t)) == "1" and
                                   scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE CorrelationId='%s' AND Action='Transfer' AND EntityId='%d'" % (BATCH, t)) == "0" for t in cca1))
    mine = {}
    for t, a in plan.items(): mine.setdefault(a, set()).add(t)
    got = {a: {int(r[0]) for r in rows("SELECT TicketId FROM dbo.Tickets WHERE CurrentOwnerEmployeeId='%s' AND TicketStatus=1 AND TicketId IN (%s)" % (a, ",".join(map(str, plan))))} for a in mine}
    check("My Tickets: each agent's own-tickets list (owner = agent) contains exactly the tickets dealt to them", got == mine)
    check("every receiving agent is a current CC member, active, unlocked, a CS Agent, and has NO explicit denial of Tickets.View / Resolve / Close (the assignee can then Resolve and Close)",
          scalar("SELECT COUNT(*) FROM dbo.Employees e JOIN dbo.AspNetUsers u ON u.Id=e.EmployeeId JOIN dbo.UserDepartmentAssignments d ON d.EmployeeId=e.EmployeeId AND d.DepartmentId=%d "
                 "WHERE e.EmployeeId IN (%s) AND e.DeactivatedAtUtc IS NULL AND NOT (ISNULL(u.LockoutEnd,SYSDATETIMEOFFSET())>SYSDATETIMEOFFSET()) AND EXISTS (SELECT 1 FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id=ur.RoleId WHERE ur.UserId=e.EmployeeId AND r.Name='CS Agent') "
                 "AND NOT EXISTS (SELECT 1 FROM dbo.UserPermissionOverrides o WHERE o.EmployeeId=e.EmployeeId AND o.Effect=2 AND o.PermissionKey IN ('Tickets.View','Tickets.Resolve','Tickets.Close'))" % (cc, ",".join("'%s'" % a for a in mine))) == str(len(mine)))
    check("each ticket is still Open: Resolve/Close stay governed by the existing lifecycle rules (an Open ticket must first be started)",
          scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE TicketId IN (%s) AND TicketStatus<>1" % ",".join(map(str, plan))) == "0")
    check("previous owners hold superseded rows only (the app's 'previous owner' rule removes their Resolve/Close/Transfer rights)",
          all(scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE TicketId=%d AND AssignedEmployeeId='%s' AND IsCurrent=1" % (t, PREV[t][0])) == ("1" if plan[t] == PREV[t][0] else "0") for t in owned))
    rc, out = rollback(BATCH, dry=False)
    post = state()
    check("rollback gives every owned ticket its previous owner and its previous current assignment row back", rc == 0 and all(
          scalar("SELECT ISNULL(LOWER(CONVERT(nvarchar(36),CurrentOwnerEmployeeId)),'none') FROM dbo.Tickets WHERE TicketId=%d" % t) == PREV[t][0] for t in owned) and
          all(scalar("SELECT COUNT(*) FROM dbo.TicketAssignments WHERE TicketAssignmentId=%d AND IsCurrent=1" % a) == "1" for a in prev_rows.values()), out[-300:])
    check("...and the whole Tickets / TicketAssignments pre-images are restored", post["Tickets"] == pre["Tickets"] and all(r in post["TicketAssignments"] for r in pre["TicketAssignments"]))

def t16_denials():
    print("T16 agents with an explicit denial of Tickets.View / Resolve / Close")
    restore()
    plan, origin, cc, agents = oracle()
    check("default pool excludes A4 (Deny Tickets.Resolve) and keeps A3 (a Grant is not a denial)", A4 not in agents and A3 in agents and len(agents) == AGENTS)
    rc, out = execute(dry=False, batch=BATCH, approved=APPROVED)
    check("default commit: the denied agent A4 receives no ticket (and no ticket is dealt to anyone who cannot resolve it)", rc == 0 and scalar("SELECT COUNT(*) FROM dbo.Tickets WHERE CurrentOwnerEmployeeId='%s'" % A4) == "0", out[-200:])
    restore()
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], ExcludeAgentsWithTicketDenials=0))
    check("@ExcludeAgentsWithTicketDenials = 0: A4 joins the pool (4 CS Agents) and the preview still lists its denial", re.search(r"\n%d\|%d\|%d\|4\|%d\|%d\s*\n" % (ELIG, MOVED, REASS, HELD_APPR, HELD_AI), out) is not None
          and any(l.startswith("4|A4 Person|") or ("|A4 Person|" in l and "Tickets.Resolve" in l) for l in out.splitlines()), out[-300:])
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED, ExcludeAgentsWithTicketDenials=0)
    check("Execute with the denial switch off but 3 approved agents is refused (Preview and Execute must agree)", rc != 0 and has(out, "ApprovedAgentCount") and digest(state()) == before, out[-200:])
    plan, origin, cc, agents = oracle(exclude_denied=False)
    rc_out = execute(dry=False, batch=BATCH, approved=A(a=4), ExcludeAgentsWithTicketDenials=0)
    commit_vs_oracle("with the matching count it commits and equals the oracle (A4 included)", rc_out, plan)
    restore()
    rc, out = run_text("DROP TABLE dbo.UserPermissionOverrides")
    plan, origin, cc, agents = oracle()
    check("without the UserPermissionOverrides table (migration not applied yet) nobody has a denial: A4 is in the pool", A4 in agents and len(agents) == 4)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC]))
    check("...the Preview still runs and is READY", rc == 0 and "READY" in out, out[-300:])
    rc, out = execute(dry=True)
    check("...and so does the Execute dry run", rc == 0 and has(out, "DRY RUN complete"), out[-300:])

def t17_keep_in_place():
    print("T17 @ReassignTicketsAlreadyOwnedByEligibleCcAgent = 0 leaves CC tickets that an eligible CC agent already owns")
    restore(); pre = state()
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], ReassignTicketsAlreadyOwnedByEligibleCcAgent=0))
    plan, origin, cc, agents = oracle(reassign_owned=False)
    n, mv, rs = counts_of(plan, origin, cc)
    check("preview: 4 tickets kept in place, eligible %d, owner replaced %d" % (ELIG - 4, REASS - 4), (n, mv, rs) == (ELIG - 4, MOVED, REASS - 4) and
          re.search(r"\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|%d\|4\|" % (OPEN_TOTAL, OPEN_OWNED, OPEN_TOTAL - OPEN_OWNED, ELIG - 4, MOVED, ELIG - 4 - MOVED, REASS - 4, ELIG - REASS, HELD_APPR, HELD_AI), out) is not None, out[:800])
    before = digest(state())
    rc, out = execute(dry=False, approved=APPROVED, ReassignTicketsAlreadyOwnedByEligibleCcAgent=0)
    check("Execute with the literal-rule counts is refused when the switch differs", rc != 0 and has(out, "backlog changed since the preview") and digest(state()) == before, out[-200:])
    rc_out = execute(dry=False, batch=BATCH, approved=(n, mv, rs, len(agents), HELD_APPR, HELD_AI), ReassignTicketsAlreadyOwnedByEligibleCcAgent=0)
    commit_vs_oracle("with the matching counts it commits and equals the oracle", rc_out, plan)
    kept = ids(["O-CCA1-%d" % i for i in range(1, 5)])
    post = state(); tk = {r[0]: r for r in post["Tickets"]}; pk = {r[0]: r for r in pre["Tickets"]}
    check("the 4 kept tickets (CC, owned by eligible agent A1) are byte-identical, with their assignment rows and no batch audit / event",
          not (kept & set(plan)) and all(tk[str(t)] == pk[str(t)] for t in kept) and all(r in post["TicketAssignments"] for r in pre["TicketAssignments"] if r[1] in {str(t) for t in kept}) and
          scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE CorrelationId='%s' AND EntityId IN (%s)" % (BATCH, ",".join(map(str, kept)))) == "0")
    check("CC tickets owned by a NON-pool employee (the supervisor) are still dealt", all(t in plan for t in ids(["O-CCSUPV-1", "O-CCSUPV-2"])))


def t18_stale_ai_conversations():
    print("T18 stale / human-touched 'active AI' conversations: diagnostic, proposed correction, protection kept for genuinely active ones")
    restore()
    # age the fixture's flagged conversations to 5 days, then add the scenarios (all Open, unowned, in SUP unless stated)
    must("""SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON;
      DECLARE @SUP int = (SELECT DepartmentId FROM dbo.Departments WHERE Code='SUP');
      UPDATE i SET CreatedAtUtc = DATEADD(day,-5,SYSUTCDATETIME()) FROM dbo.TicketInteractions i JOIN dbo.Tickets t ON t.TicketId=i.TicketId WHERE t.TicketNumber LIKE 'H-AI-%' OR t.TicketNumber IN ('H-BOTH-1','O-AI-1');
      DECLARE @Z TABLE (n nvarchar(20), ch tinyint, mins int, endedmins int NULL);
      INSERT @Z VALUES (N'Z-REC',2,5,NULL),(N'Z-12H',2,720,NULL),(N'Z-3D',2,4320,NULL),(N'Z-PHONE2D',1,2880,NULL),(N'Z-MSG',2,4320,NULL),(N'Z-HUM',2,4320,NULL),(N'Z-HAND',2,2,NULL),(N'Z-APR',2,4320,NULL),(N'Z-OLDEND',2,4320,1440);
      INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
      SELECT n, @SUP, @SUP, NULL, 1, 0, 0, 1, N'stale-ai scenario', 0, DATEADD(minute,-mins-1,SYSUTCDATETIME()) FROM @Z;
      INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc)
      VALUES (N'Z-NOINT', @SUP, @SUP, NULL, 1, 0, 0, 1, N'no interaction row', 0, DATEADD(day,-9,SYSUTCDATETIME()));
      INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, InteractionStartedAtUtc, LastCustomerInteractionAtUtc, RequiresHumanFollowUp)
      SELECT t.TicketId, 1, 2, z.ch, N'0500000000', N'conv-' + z.n, DATEADD(minute,-z.mins,SYSUTCDATETIME()), DATEADD(minute,-z.mins,SYSUTCDATETIME()), DATEADD(minute,-z.mins,SYSUTCDATETIME()), 0 FROM @Z z JOIN dbo.Tickets t ON t.TicketNumber=z.n;
      -- Z-OLDEND also has a newer, ENDED conversation
      INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, InteractionStartedAtUtc, EndedAtUtc, EndReason, RequiresHumanFollowUp)
      SELECT TicketId, 0, 2, 2, N'0500000000', N'conv-oldend-2', DATEADD(minute,-1500,SYSUTCDATETIME()), DATEADD(minute,-1500,SYSUTCDATETIME()), DATEADD(minute,-1440,SYSUTCDATETIME()), N'customer left', 0 FROM dbo.Tickets WHERE TicketNumber=N'Z-OLDEND';
      -- Z-MSG: old conversation but a customer line 30 minutes ago;  Z-HUM: old conversation with a HumanAgent line
      INSERT dbo.TicketInteractionMessages (TicketInteractionId, Sequence, Sender, SenderName, SentAtUtc, Body)
      SELECT i.TicketInteractionId, 1, 1, N'cust', DATEADD(minute,-30,SYSUTCDATETIME()), N'hello' FROM dbo.TicketInteractions i JOIN dbo.Tickets t ON t.TicketId=i.TicketId WHERE t.TicketNumber=N'Z-MSG';
      INSERT dbo.TicketInteractionMessages (TicketInteractionId, Sequence, Sender, SenderName, SentAtUtc, Body)
      SELECT i.TicketInteractionId, 1, 2, N'agent', DATEADD(minute,-4300,SYSUTCDATETIME()), N'hi' FROM dbo.TicketInteractions i JOIN dbo.Tickets t ON t.TicketId=i.TicketId WHERE t.TicketNumber=N'Z-HUM';
      -- Z-HAND: an interaction-handler row (an agent connected)
      INSERT dbo.TicketInteractionHandlers (TicketInteractionId, TicketId, HandledByUserId, GenesysAgentUserId, Source, RecordedAtUtc)
      SELECT i.TicketInteractionId, i.TicketId, '00000000-0000-0000-0000-000000000002', N'g-1', 1, SYSUTCDATETIME() FROM dbo.TicketInteractions i JOIN dbo.Tickets t ON t.TicketId=i.TicketId WHERE t.TicketNumber=N'Z-HAND';
      -- Z-APR: stale but a pending approval
      INSERT dbo.TicketApprovals (TicketId, ApprovalType, Status, TargetKind, RequestedByEmployeeId, RequestedAtUtc, IsCurrent, CorrelationId)
      SELECT TicketId, 1, 1, 1, 'b0000000-0000-0000-0000-000000000001', DATEADD(day,-2,SYSUTCDATETIME()), 1, NEWID() FROM dbo.Tickets WHERE TicketNumber=N'Z-APR';""")
    st = ai_state()
    num = {int(r[0]): r[1] for r in rows("SELECT TicketId, TicketNumber FROM dbo.Tickets")}
    got_states = {num[t]: v[0] for t, v in st.items()}
    expect = {"Z-REC": "GENUINELY_ACTIVE", "Z-12H": "SILENT_BUT_UNDER_THRESHOLD", "Z-3D": "STALE_NO_END_EVENT", "Z-PHONE2D": "STALE_NO_END_EVENT", "Z-MSG": "GENUINELY_ACTIVE",
              "Z-HUM": "HUMAN_TOUCHED_NOT_FLAGGED", "Z-HAND": "HUMAN_TOUCHED_NOT_FLAGGED", "Z-APR": "STALE_NO_END_EVENT", "Z-OLDEND": "STALE_NO_END_EVENT"}
    check("oracle sanity: the scenarios classify as designed (and Z-NOINT is not flagged)", all(got_states.get(k) == v for k, v in expect.items()) and "Z-NOINT" not in got_states, got_states)
    # ---- the diagnostic is read-only and agrees with the oracle
    DIAG = os.path.join(ROOT, "Diagnose_TigerCS_PROD_ActiveAiConversations_ReadOnly.sql")
    before = digest(state())
    rc, out = run_text(open(DIAG, encoding="utf-8").read())
    check("diagnostic exits 0 and changed nothing (all table digests equal)", rc == 0 and digest(state()) == before, out[-300:])
    from collections import Counter
    oc = Counter(v[0] for v in st.values())
    ok = all(re.search(r"^%s\|%d\|" % (k, oc[k]), out, re.M) for k in oc)
    check("diagnostic counts by state equal the independent oracle %s" % dict(oc), ok, out[:1500])
    flagged = len(st); base, pend, ai_only = held_sets()
    check("diagnostic headline: flagged active-AI = %d, of which also pending approval = %d" % (flagged, sum(1 for t in st if t in pend)),
          re.search(r"\|%d\|%d\|\d+\|\d+\|\d+\|\d+\s*\n" % (len(base), flagged), out) is not None, out[:700])
    eligible_now = len([t for t in base if t not in pend and t not in st])
    h24 = sum(1 for t, v in st.items() if t not in pend and (v[1] or v[2] > 24))
    check("diagnostic projection for 24 h: eligible now %d -> projected %d" % (eligible_now, eligible_now + h24),
          re.search(r"^24 hours\|24\|%d\|\d+\|\d+\|%d\|" % (eligible_now, eligible_now + h24), out, re.M) is not None, out[out.find("Label|"):][:900])
    check("diagnostic lists the missing-record counts (Z-NOINT among the Open tickets with no interaction row)", re.search(r"^\d+\|\d+\|0\|", out[out.find("OpenTickets_NoInteractionRow"):], re.M) is not None)
    # ---- Preview: defaults unchanged, switches reproduce the oracle
    base_plan, base_origin, cc, agents = oracle()
    n0, m0, r0 = counts_of(base_plan, base_origin, cc)
    rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC]))
    check("switches OFF (default): the protection is unchanged - no ticket reclassified, counts as before", "READY|NULL|0|0|0" in out and "ReclassifiedAs" in out and not re.search(r"^[A-Z]-?[A-Z0-9-]+\|\w+\|(STALE|HUMAN_TOUCHED)\|", out, re.M), out[:600])
    for label, sa, hu in (("stale > 24 h only", 24, 0), ("stale > 6 h only", 6, 0), ("human-touched only", None, 1), ("stale > 24 h + human-touched", 24, 1)):
        plan, origin, cc, agents = oracle(stale_after=sa, human=bool(hu))
        n, mv, rs = counts_of(plan, origin, cc); _, pend2, ai2 = held_sets(stale_after=sa, human=bool(hu))
        sw = dict(StaleAiAfterHours="NULL" if sa is None else sa, TreatHumanTouchedAiAsHistorical=hu)
        rc, out = run_text(patch(PREVIEW, ServiceIdsConfirmed=1, services=[SVC], **sw))
        check("Preview %s: eligible %d (was %d), moved %d, owner replaced %d, still held AI %d, approvals %d" % (label, n, n0, mv, rs, len(ai2), len(pend2)),
              rc == 0 and re.search(r"\n%d\|%d\|%d\|%d\|%d\|%d\s*\n" % (n, mv, rs, len(agents), len(pend2), len(ai2)), out) is not None, out[-400:])
        check("...and Execute (dry run, same switches, matching counts) agrees", execute(dry=False if False else True, approved=(n, mv, rs, len(agents), len(pend2), len(ai2)), **sw)[0] == 0)
    # ---- commit with both switches: plan equals oracle, interactions untouched, genuinely active stay protected
    plan, origin, cc, agents = oracle(stale_after=24, human=True)
    n, mv, rs = counts_of(plan, origin, cc); _, pend2, ai2 = held_sets(stale_after=24, human=True)
    appr = (n, mv, rs, len(agents), len(pend2), len(ai2)); sw = dict(StaleAiAfterHours=24, TreatHumanTouchedAiAsHistorical=1)
    pre = state(); pre_x = snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketInteractionMessages", "TicketInteractionHandlers", "TicketAgentHandoffs"])
    rc, out = execute(dry=False, approved=APPROVED, **sw)
    check("Execute with the switches but the OLD approved counts is refused (the correction changes the numbers, so it cannot slip in unseen)", rc != 0 and has(out, "backlog changed since the preview"), out[-250:])
    rc_out = execute(dry=False, batch=BATCH, approved=appr, **sw)
    commit_vs_oracle("Execute with the switches and the matching counts commits and equals the oracle (%d tickets)" % n, rc_out, plan)
    ids_ = lambda names: ids(names)
    moved = ids_(["Z-3D", "Z-PHONE2D", "Z-OLDEND", "Z-HUM", "Z-HAND"]); held = ids_(["Z-REC", "Z-12H", "Z-MSG", "Z-APR"])
    post = state(); tk = {r[0]: r for r in post["Tickets"]}; pk = {r[0]: r for r in pre["Tickets"]}
    check("stale (chat, phone, superseded) and human-touched conversations were moved and assigned", moved <= set(plan) and all(tk[str(t)] != pk[str(t)] for t in moved))
    check("genuinely active / recently silent / recent-message conversations and the pending-approval ticket stay byte-identical (protection kept)", not (held & set(plan)) and all(tk[str(t)] == pk[str(t)] for t in held))
    check("NO conversation record was modified (TicketInteractions, messages, handlers, handoffs, approvals byte-identical)",
          snapshot_tables(["TicketApprovals", "TicketInteractions", "TicketInteractionMessages", "TicketInteractionHandlers", "TicketAgentHandoffs"]) == pre_x)
    rc, out = rollback(BATCH, dry=False)
    check("rollback restores the pre-batch Tickets table", rc == 0 and state()["Tickets"] == pre["Tickets"], out[-250:])
    # ---- a NEW conversation on a stale ticket created mid-run must abort the run (Msg 50341)
    restore()
    must("SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON; DECLARE @SUP int=(SELECT DepartmentId FROM dbo.Departments WHERE Code='SUP');"
         "INSERT dbo.Tickets (TicketNumber, OriginatingDepartmentId, CurrentDepartmentId, CurrentOwnerEmployeeId, TicketStatus, VerificationStatus, EscalationLevel, SlaState, RequestSummary, ReopenCount, CreatedAtUtc) VALUES (N'Z-3D',@SUP,@SUP,NULL,1,0,0,1,N'x',0,DATEADD(day,-3,SYSUTCDATETIME()));"
         "INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, InteractionStartedAtUtc, RequiresHumanFollowUp) SELECT TicketId,1,2,2,N'05',N'c-z3d',DATEADD(day,-3,SYSUTCDATETIME()),DATEADD(day,-3,SYSUTCDATETIME()),0 FROM dbo.Tickets WHERE TicketNumber=N'Z-3D'")
    plan, origin, cc, agents = oracle(stale_after=24); n, mv, rs = counts_of(plan, origin, cc); _, pend2, ai2 = held_sets(stale_after=24)
    victim = scalar("SELECT TicketId FROM dbo.Tickets WHERE TicketNumber='Z-3D'")
    slow = patch(EXECUTE, DryRun=0, ServiceIdsConfirmed=1, services=[SVC], BatchId="'%s'" % BATCH, StaleAiAfterHours=24, **approval_params((n, mv, rs, len(agents), len(pend2), len(ai2))))
    marker = "    -- (f) writes"; assert marker in slow            # the window before the update, where only update locks are held
    slow = slow.replace(marker, "    WAITFOR DELAY '00:00:10';\n" + marker, 1)
    res = {}; th = threading.Thread(target=lambda: res.__setitem__("A", run_text(slow))); th.start(); time.sleep(4)
    rcN, outN = run_text("SET QUOTED_IDENTIFIER ON; INSERT dbo.TicketInteractions (TicketId, IsOriginatingInteraction, Source, ChannelId, CustomerPhone, GenesysConversationId, CreatedAtUtc, InteractionStartedAtUtc, RequiresHumanFollowUp) "
                         "VALUES (%s,0,2,2,N'05',N'new-live',SYSUTCDATETIME(),SYSUTCDATETIME(),0)" % victim)
    th.join(); rcA, outA = res["A"]
    check("a brand-new conversation on a 'stale' ticket during the run aborts the whole run (Msg 50341)", rcA != 0 and "Msg 50341" in outA and scalar("SELECT COUNT(*) FROM dbo.AuditEntries WHERE Action='BacklogDistributionBatch'") == "0", (rcN, outA[-250:]))

TESTS = [t01_preview_readonly, t01b_preview_as_datareader, t02_dry_run, t03_gates, t04_commit, t05_rollback_clean, t06_rollback_touched, t06b_include_worked, t07_concurrency,
         t08_race_between_preview_and_run, t09_cursor_and_pool_edges, t11_pending_approvals, t11b_approval_decided_between_preview_and_run, t12_active_ai, t13_concurrent_safeguards,
         t14_switches, t15_owned_reassignment, t16_denials, t17_keep_in_place, t18_stale_ai_conversations, t10_scale]

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
