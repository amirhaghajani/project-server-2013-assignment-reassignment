# Debugging Project Server 2013 APIs: PSI (SOAP) vs. CSOM vs. REST

*A 10-Hour Journey Through Legacy Enterprise Software in the Age of AI*

---

Working with legacy systems like SharePoint 2013 in the age of AI sometimes feels like a punishment.

While the software industry is busy discussing AI agents, modern frameworks, and autonomous coding assistants, some of us are still dealing with decade-old enterprise systems, deprecated APIs, undocumented behaviors, and unexpected errors.

Recently, I spent more than 10 hours solving what initially seemed like a simple problem in Microsoft Project Server 2013: replacing task assignments from one enterprise resource with another across multiple projects.

I explored three different approaches: PSI (SOAP), CSOM, and REST.

Some operations appeared to succeed but didn't actually persist. Others returned confusing errors. And some approaches that looked perfectly reasonable simply didn't work.

Eventually, I found a working solution using CSOM.

I'm sharing my debugging journey, including the approaches that failed, the reasons behind those failures, and the implementation that finally worked.

Maybe someone else is currently being punished by Project Server 2013 too. Hopefully, this article will save them a few hours of frustration.

After all, modern tools can help us write code faster, but understanding how an existing system actually works is still an essential engineering skill.

## TL;DR

After more than 10 hours of debugging, I found a working CSOM-based solution for bulk-reassigning task assignments in Project Server 2013.

The key was understanding the correct sequence of operations:

1. Add the enterprise resource to the project team using `AddEnterpriseResource()`.
2. Persist the resource addition using `WaitForQueue(_draftProject.Update())`.
3. Create the new assignments and delete the old ones, then persist the changes with another `WaitForQueue(_draftProject.Update())`.
4. Publish and check in the project using `WaitForQueue(_draftProject.Publish(true))`.

I also investigated PSI (SOAP) and REST approaches, documented the errors I encountered, and found ways to handle CSOM request-size limitations when loading assignments.

The final assignment modification workflow uses CSOM. SQL Server is used separately for project enumeration.

The most important lesson: in Project Server 2013, the order of operations and the queue persistence model matter.

---

## The Goal

Given a source resource and a destination resource, find all projects where the source resource has task assignments and replace those assignments with the destination resource, then save and publish each project.

**Environment:**
- Project Server 2013 (on-premises, `EPM2013`)
- C# .NET Framework 4.8 console app
- CSOM: `Microsoft.ProjectServer.Client.dll` v15.0.0.0
- Auth: `CredentialCache.DefaultNetworkCredentials`

---

## The Journey

### Attempt 1: CSOM `AssignmentCreationInformation` (The Naive Approach)

**Code:**
```csharp
_draftProject.Assignments.Add(new AssignmentCreationInformation
{
    TaskId = task.Id,
    ResourceId = destinationResourceId,
    Start = DateTime.Now,
    Finish = DateTime.Now.AddDays(1)
});
_context.ExecuteQuery();
```

**Result:** Assignments were created (count went from 26 to 38) but **all had no resource**. The `Resource` property was null.

**Why:** The destination enterprise resource was **not registered in the project's resource pool**. When `DraftAssignmentCollection.Add` receives a `ResourceId` that doesn't correspond to a project resource, the server creates the assignment but leaves the resource empty. The `ResourceId` wasn't ignored — it just couldn't be resolved because the resource wasn't in the project.

---

### Attempt 2: Add Resource to Project Team First

**Logic:** The resource must be in the project's resource pool before it can be assigned.

**What we tried:**
- `_draftProject.ProjectResources.AddEnterpriseResource(enterpriseResource)` + `ExecuteQuery()`
- `_draftProject.ProjectResources.Add(new ProjectResourceCreationInformation { ... })` + `ExecuteQuery()`
- Various combinations with `Update()`, `Publish(true)`, `WaitForQueue()`

**Result:** The resource was added client-side (`ServerObjectIsNull: False`) but **never persisted to the server**. The `ProjectResources` count stayed at 8 (not 9). Even `WaitForQueue(Update())` and `Publish(true)` didn't help.

**Key diagnostic:** The `ProjectResources` collection is **lazily loaded**. When we checked `Count` after the add, it showed 1 (only the new resource) or 8 (server-side), depending on whether we reloaded. The resource was never actually in the server-side draft pool.

---

### Attempt 3: PSI (Project Server Interface)

**What we tried:**
- `ResourceAssignments.asmx` `QueueCreateAssignment` → 404 (WSDL) / 400 (ASMX)
- `Project.asmx` `QueueAddToProject` → 400 / "GeneralUnhandledException"

**Why it failed:** Project Server 2013's PSI is a legacy SOAP service that's not fully exposed on all PWA web applications. The WSDL endpoint returned 404, and the ASMX endpoint returned 400 because the DataSet format didn't match the expected strongly-typed `ProjectDataSet`.

---

### Attempt 4: REST API

**What we tried:**
- `POST /_api/ProjectServer/Projects('<guid>')/Draft/ProjectResources/addEnterpriseResource(resource)` → 400: "The method AddEnterpriseResource cannot be invoked as its parameter resource is not supported."
- `POST .../addEnterpriseResourceById('<guid>')` → 404: method doesn't exist in 2013.

**Why it failed:** Project Server 2013's REST API can't pass complex entity objects (like `PS.EnterpriseResource`) as method parameters. `addEnterpriseResourceById` was introduced in Project Server 2016+.

---

### Attempt 5: Back to CSOM — The Correct Pattern

The breakthrough came from understanding three CSOM behaviors:

#### Fix 1: `AddEnterpriseResource` + `WaitForQueue(Update())`

**Wrong:**
```csharp
_draftProject.ProjectResources.AddEnterpriseResource(enterpriseResource);
_context.ExecuteQuery();  // ← This doesn't persist!
```

**Right:**
```csharp
_draftProject.ProjectResources.AddEnterpriseResource(enterpriseResource);
_context.WaitForQueue(_draftProject.Update(), DefaultTimeoutSeconds);
```

`WaitForQueue` handles `ExecuteQuery` internally and **waits for the queue job to complete**, ensuring the resource is persisted.

#### Fix 2: Don't Call `Publish(true)` Mid-Flow

Calling `Publish(true)` checks the project back in. If we then try to modify assignments, the draft is gone.

**Wrong:**
```csharp
_draftProject.ProjectResources.AddEnterpriseResource(enterpriseResource);
_context.WaitForQueue(_draftProject.Publish(true), ...);  // ← Checks in!
// ... later ...
_draftProject.Assignments.Add(...);  // ← Fails, draft is gone
```

**Right:**
```csharp
_draftProject.ProjectResources.AddEnterpriseResource(enterpriseResource);
_context.WaitForQueue(_draftProject.Update(), ...);  // ← Saves, stays checked out
// ... later ...
_draftProject.Assignments.Add(...);
_context.WaitForQueue(_draftProject.Update(), ...);
_context.WaitForQueue(_draftProject.Publish(true), ...);  // ← Final publish
```

#### Fix 3: `WaitForQueue` Must Be Called Directly

**Wrong:**
```csharp
_draftProject.Update();
_context.ExecuteQuery();  // ← Returns uninitialized QueueJob
_context.WaitForQueue(job, ...);  // ← Throws "JobState not initialized"
```

**Right:**
```csharp
_context.WaitForQueue(_draftProject.Update(), DefaultTimeoutSeconds);
```

`WaitForQueue` calls `ExecuteQuery` internally and properly initializes the `QueueJob`.

#### Fix 4: Use `DeleteObject()` for Assignments

**Wrong:**
```csharp
_draftProject.Assignments.Remove(oldAsg);
_context.ExecuteQuery();
```

**Right:**
```csharp
oldAsg.DeleteObject();
_context.ExecuteQuery();
```

`DeleteObject()` is the CSOM way to delete an assignment.

#### Fix 5: Don't Set `Id` on `AssignmentCreationInformation`

**Wrong:**
```csharp
new AssignmentCreationInformation
{
    Id = Guid.NewGuid(),  // ← Let the server assign it
    TaskId = task.Id,
    ResourceId = destinationResourceId,
    ...
}
```

**Right:**
```csharp
new AssignmentCreationInformation
{
    TaskId = task.Id,
    ResourceId = destinationResourceId,
    ...
}
```

The server generates the assignment UID automatically.

---

### Attempt 6: Batch Loading for Large Projects

**Error:** `ServerException: The request uses too many resources` on projects with 58/98 assignments.

**Why:** Loading all assignments with nested `Resource` and `Task` objects in a single CSOM request exceeds the server's request-size limit.

**Fix:** Batch the loading in chunks of 20:
```csharp
const int batchSize = 20;
for (int offset = 0; offset < allAssignments.Count; offset += batchSize)
{
    var batch = allAssignments.Skip(offset).Take(batchSize).ToList();

    foreach (var assignment in batch)
    {
        _context.Load(assignment, a => a.Id, a => a.Start, a => a.Finish, a => a.Notes);
        _context.Load(assignment.Resource);
        _context.Load(assignment.Task);
    }
    _context.ExecuteQuery();

    foreach (var assignment in batch)
    {
        if (assignment.Resource != null && assignment.Resource.ServerObjectIsNull != true)
            _context.Load(assignment.Resource, r => r.Id);
        if (assignment.Task != null && assignment.Task.ServerObjectIsNull != true)
            _context.Load(assignment.Task, t => t.Id, t => t.Start, t => t.Finish);
    }
    _context.ExecuteQuery();
}
```

---

## What Didn't Work (and Why)

| Approach | Why It Failed |
|----------|--------------|
| CSOM `AssignmentCreationInformation.ResourceId` without adding resource to team | Resource not in project pool; assignment created with no resource |
| `AddEnterpriseResource` + `ExecuteQuery()` | Doesn't persist; `WaitForQueue(Update())` is required |
| `ProjectResources.Add` with fresh GUID | Creates a local resource, but assignment still has no resource |
| `AddEnterpriseResource` + `Publish(true)` mid-flow | Checks in the project; draft is gone for subsequent updates |
| `ExecuteQuery()` before `WaitForQueue()` | Returns uninitialized `QueueJob`; `WaitForQueue` throws |
| PSI `ResourceAssignments.asmx` | 404 (WSDL) / 400 (ASMX) — service not exposed |
| PSI `Project.asmx` `QueueAddToProject` | 400 / "GeneralUnhandledException" — wrong DataSet format |
| REST `addEnterpriseResource(resource)` | 400: "parameter resource is not supported" — 2013 REST can't pass complex entities |
| REST `addEnterpriseResourceById` | 404: method introduced in 2016+ |
| `DraftProjectResource.Assignments.Add` | Null object error — newly added resource is a pending-replace |
| `DraftTask.Assignments.Add` | Same as project-level; no resource resolution |
| `FieldValues["RES_UID"]` | Field key doesn't exist; `FieldValues` only contains work fields |
| `ProjectResources.GetByGuid` | Returns null for newly added resources |
| `ProjectResources.Count` | Lazy-loaded; doesn't reflect server-side pool |

---

## What Worked (The Final Solution)

### Architecture

```
Program.cs
  └─ For each project:
       ├─ ProjectServerProject.CheckOutProject()
       ├─ ProjectServerProject.AddResourceToProject(destinationResourceId)
       │    ├─ AddEnterpriseResource(enterpriseResource)
       │    └─ WaitForQueue(_draftProject.Update())
       ├─ ProjectServerProject.ReplaceAssignmentResource(sourceResourceId, destinationResourceId)
       │    ├─ Load assignments (batched, 20 at a time)
       │    ├─ Filter source assignments
       │    ├─ Add destination assignments
       │    ├─ Delete source assignments
       │    └─ WaitForQueue(_draftProject.Update())
       └─ ProjectServerProject.CheckInProject()
            └─ WaitForQueue(_draftProject.Publish(true))
```

### Key Code: `AddResourceToProject`

```csharp
public bool AddResourceToProject(Guid resourceId)
{
    var enterpriseResource = _context.EnterpriseResources.GetByGuid(resourceId);
    _context.Load(enterpriseResource);
    _context.ExecuteQuery();

    _context.Load(_draftProject.ProjectResources);
    _context.ExecuteQuery();

    if (_draftProject.ProjectResources.Any(pr => pr.Id == resourceId))
    {
        return false;
    }

    _draftProject.ProjectResources.AddEnterpriseResource(enterpriseResource);

    var updateState = _context.WaitForQueue(_draftProject.Update(), DefaultTimeoutSeconds);
    JobStateLog(updateState, "Updating team for project " + _projectId);
    ThrowIfJobFailed(updateState, "Update", _projectId);

    return true;
}
```

### Key Code: `ReplaceAssignmentResource`

```csharp
public int ReplaceAssignmentResource(Guid sourceResourceId, Guid destinationResourceId)
{
    _context.Load(_draftProject.Assignments);
    _context.ExecuteQuery();

    var allAssignments = _draftProject.Assignments.ToList();

    // Batch load assignments with Resource/Task (20 at a time)
    const int batchSize = 20;
    for (int offset = 0; offset < allAssignments.Count; offset += batchSize)
    {
        var batch = allAssignments.Skip(offset).Take(batchSize).ToList();

        foreach (var assignment in batch)
        {
            _context.Load(assignment, a => a.Id, a => a.Start, a => a.Finish, a => a.Notes);
            _context.Load(assignment.Resource);
            _context.Load(assignment.Task);
        }
        _context.ExecuteQuery();

        foreach (var assignment in batch)
        {
            if (assignment.Resource != null && assignment.Resource.ServerObjectIsNull != true)
                _context.Load(assignment.Resource, r => r.Id);
            if (assignment.Task != null && assignment.Task.ServerObjectIsNull != true)
                _context.Load(assignment.Task, t => t.Id, t => t.Start, t => t.Finish);
        }
        _context.ExecuteQuery();
    }

    var sourceAssignments = allAssignments
        .Where(a => a.Resource != null && a.Resource.ServerObjectIsNull != true && a.Resource.Id == sourceResourceId)
        .ToList();

    if (sourceAssignments.Count == 0)
    {
        return 0;
    }

    var existingDestTaskIds = new HashSet<Guid>(
        allAssignments
            .Where(a => a.Resource != null && a.Resource.ServerObjectIsNull != true && a.Resource.Id == destinationResourceId && a.Task != null && a.Task.ServerObjectIsNull != true)
            .Select(a => a.Task.Id)
    );

    var toAdd = new List<Tuple<DraftTask, DateTime, DateTime, string>>();
    foreach (var oldAsg in sourceAssignments)
    {
        if (oldAsg.Task == null || oldAsg.Task.ServerObjectIsNull == true || existingDestTaskIds.Contains(oldAsg.Task.Id))
            continue;

        var start = oldAsg.Start;
        var finish = oldAsg.Finish;
        if (start == DateTime.MinValue) start = oldAsg.Task.Start;
        if (start == DateTime.MinValue) start = DateTime.Now;
        if (finish == DateTime.MinValue || finish < start) finish = oldAsg.Task.Finish;
        if (finish == DateTime.MinValue || finish < start) finish = start.AddDays(1);

        toAdd.Add(Tuple.Create(oldAsg.Task, start, finish, oldAsg.Notes));
    }

    foreach (var item in toAdd)
    {
        _draftProject.Assignments.Add(new AssignmentCreationInformation
        {
            TaskId = item.Item1.Id,
            ResourceId = destinationResourceId,
            Start = item.Item2,
            Finish = item.Item3,
            Notes = item.Item4
        });
    }

    foreach (var oldAsg in sourceAssignments)
    {
        oldAsg.DeleteObject();
    }

    if (toAdd.Count > 0 || sourceAssignments.Count > 0)
    {
        var updateState = _context.WaitForQueue(_draftProject.Update(), DefaultTimeoutSeconds);
        JobStateLog(updateState, "Updating draft assignments for project " + _projectId);
        ThrowIfJobFailed(updateState, "Update", _projectId);
    }

    return sourceAssignments.Count;
}
```

### Key Code: `CheckInProject`

```csharp
public void CheckInProject()
{
    var jobState = _context.WaitForQueue(_draftProject.Publish(true), DefaultTimeoutSeconds);
    JobStateLog(jobState, "Publishing project " + _projectId);
    ThrowIfJobFailed(jobState, "Publish", _projectId);
}
```

---

## Key Lessons

1. **`WaitForQueue` is your friend.** It handles `ExecuteQuery` internally and waits for the queue job to complete. Never call `ExecuteQuery()` before `WaitForQueue()`.

2. **`Publish(true)` checks in the project.** Don't call it mid-flow if you need to keep the draft for further modifications.

3. **`Update()` saves the draft.** Call it before `Publish(true)` to persist property changes.

4. **Batch large requests.** Loading 100+ assignments with nested objects in a single request exceeds the server's limit. Batch in chunks of 20.

5. **Use SQL for project enumeration.** CSOM `context.Projects` can hit the "too many resources" limit. Use `MSP_EpmAssignment` / `MSP_EpmProject_UserView` SQL queries instead.

6. **`ServerObjectIsNull` guards.** When accessing navigation properties, check `ServerObjectIsNull != true` before accessing `.Id`.

7. **Don't set `Id` on `AssignmentCreationInformation`.** Let the server generate it.

8. **Use `DeleteObject()` for assignments.** Not `Remove()`.

9. **`DefaultNetworkCredentials`.** Use this for on-prem Project Server authentication.

10. **The resource must be in the project pool first.** `AssignmentCreationInformation.ResourceId` only resolves if the enterprise resource is already registered in the project's resource pool. Add it with `AddEnterpriseResource` + `WaitForQueue(Update())` before creating assignments. Without this, the server creates the assignment but leaves the resource empty.

---

## Acknowledgments

Thanks to the Project Server community and the various blog posts/Stack Overflow answers that helped along the way. The key breakthrough was understanding the CSOM queue/persistence model in Project Server 2013.

---

## ⚠️ Before You Run This

This tool **modifies and publishes real project data**. It checks out, edits, and republishes every project it touches, which cannot be undone easily.

**Always test on a non-production environment first, and run it against a single project before running it on all projects.**

---

*Article written from the debugging journey of building `ConsoleUpdateProjectServerTasksAssignment` — a tool to bulk-reassign Project Server 2013 task assignments from one resource to another across multiple projects.*