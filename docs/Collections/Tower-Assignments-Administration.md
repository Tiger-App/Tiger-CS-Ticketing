# Administration -> Collections Tower Assignments

The monthly tower -> collector mapping is managed under **Administration -> Collections Tower Assignments** (`/Admin/CollectionsTowerAssignments`), not under Collections.
The page file is still `Pages/Collections/TowerAssignments.cshtml`: it only declares the new route, so the `/Admin` folder rule (System Administrator only) does not apply to it
and it carries its own gate (`TowerAssignmentsPolicy`: CS Manager, Department Head, System Administrator).

## Access
* Web (display only): `TowerAssignmentsPolicy` hides the nav entry and refuses the page to anyone else.
* Api (enforced): `CollectionsTeam.CanManageTowersAsync` - CS Manager, a Department Head who belongs to the Collections department, or the System Administrator. Reading the
  month and the history needs the same authority; plain Collections employees get 403.
* Unchanged: manual ticket Assign / Reassign by Collections employees (`CollectionsTeam.CanAssignTicketsAsync`) and all ticket routing.

## "An error occurred while processing your request." on the page
That text is the title of the Api's default 500 ProblemDetails (`AddProblemDetails` + `UseExceptionHandler`); the page shows it as its load error. The Api has no
auto-migrate, so a local database that has not had `20261104130835_AddCollectionsTowerAssignments` applied has no `CollectionsTowerAssignments` /
`CollectionsTowerAssignmentHistory` tables and the first query fails with `Invalid object name`. Apply the migration to the local database:

```bash
dotnet ef database update --project src/TigerCS.Infrastructure --startup-project src/TigerCS.Infrastructure
```

(or run `AddCollectionsTowerAssignments.sql` against the UAT/local `TigerCsTicketing` database).
