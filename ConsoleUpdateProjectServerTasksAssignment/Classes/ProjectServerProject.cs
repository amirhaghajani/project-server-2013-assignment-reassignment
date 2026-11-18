using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using Microsoft.ProjectServer.Client;
using JobState = Microsoft.ProjectServer.Client.JobState;

namespace ConsoleUpdateProjectServerTasksAssignment.Classes
{
    internal class ProjectServerProject
    {
        #region Members
        private ProjectContext _context;
        private PublishedProject _project;
        private readonly Guid _projectId;
        private DraftProject _draftProject;
        private const int DefaultTimeoutSeconds = 300;
        #endregion

        #region Constructor
        public ProjectServerProject(Guid projectId)
        {
            _projectId = projectId;
            _context = CreateContext();
            _project = _context.Projects.GetByGuid(projectId);
            if (_project == null)
            {
                throw new Exception("Failed to retrieve project " + projectId + ". Make sure the project exists in Project Server.");
            }
        }
        #endregion

        #region Public Methods
        public static ProjectContext CreateContext()
        {
            var context = new ProjectContext(AllTaskConfig.PwaInstanceUrl);
            context.Credentials = CredentialCache.DefaultNetworkCredentials;
            return context;
        }

        public static List<Guid> GetAllProjectIds()
        {
            var projectIds = new List<Guid>();

            using (var con = new SqlConnection(AllTaskConfig.ConnectionString_ProjectWebApp2013))
            using (var cmd = new SqlCommand("SELECT ProjectUID FROM MSP_EpmProject_UserView WHERE ProjectType = 0", con))
            {
                con.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        projectIds.Add(reader.GetGuid(0));
                    }
                }
            }

            return projectIds;
        }

        public static List<Guid> GetProjectIdsWithSourceAssignment(Guid sourceResourceId)
        {
            var projectIds = new List<Guid>();

            using (var con = new SqlConnection(AllTaskConfig.ConnectionString_ProjectWebApp2013))
            using (var cmd = new SqlCommand("SELECT DISTINCT ProjectUID FROM MSP_EpmAssignment WHERE ResourceUID = @resourceId", con))
            {
                cmd.Parameters.AddWithValue("@resourceId", sourceResourceId);
                con.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        projectIds.Add(reader.GetGuid(0));
                    }
                }
            }

            return projectIds;
        }

        public static string GetEnterpriseResourceName(Guid resourceId)
        {
            using (var context = CreateContext())
            {
                var resource = context.EnterpriseResources.GetByGuid(resourceId);
                context.Load(resource, r => r.Id, r => r.Name);
                context.ExecuteQuery();
                return resource.Name + " (" + resource.Id + ")";
            }
        }

        public bool IsCheckedOut
        {
            get
            {
                _context.Load(_project, p => p.IsCheckedOut);
                _context.ExecuteQuery();
                return _project.IsCheckedOut;
            }
        }

        public void CheckOutProject()
        {
            _project = _context.Projects.GetByGuid(_projectId);
            _context.Load(_project, p => p.Id, p => p.LastPublishedDate);
            _context.ExecuteQuery();
            _draftProject = _project.CheckOut();
        }

        public bool AddResourceToProject(Guid resourceId)
        {
            Console.WriteLine("  Checking if resource is in team...");
            var enterpriseResource = _context.EnterpriseResources.GetByGuid(resourceId);
            _context.Load(enterpriseResource);
            _context.ExecuteQuery();

            _context.Load(_draftProject.ProjectResources);
            _context.ExecuteQuery();

            if (_draftProject.ProjectResources.Any(pr => pr.Id == resourceId))
            {
                Console.WriteLine("  Resource is already in team.");
                return false;
            }

            Console.WriteLine("  Adding resource to project team...");
            _draftProject.ProjectResources.AddEnterpriseResource(enterpriseResource);

            var updateState = _context.WaitForQueue(_draftProject.Update(), DefaultTimeoutSeconds);
            JobStateLog(updateState, "Updating team for project " + _projectId);
            ThrowIfJobFailed(updateState, "Update", _projectId);

            return true;
        }

        public void CheckInWithoutPublish()
        {
            var jobState = _context.WaitForQueue(_draftProject.CheckIn(true), DefaultTimeoutSeconds);
            JobStateLog(jobState, "Checking in project " + _projectId);
            ThrowIfJobFailed(jobState, "Check-in", _projectId);
        }

        public void CheckInProject()
        {
            var jobState = _context.WaitForQueue(_draftProject.Publish(true), DefaultTimeoutSeconds);
            JobStateLog(jobState, "Publishing project " + _projectId);
            ThrowIfJobFailed(jobState, "Publish", _projectId);
        }

        public int ReplaceAssignmentResource(Guid sourceResourceId, Guid destinationResourceId)
        {
            _context.Load(_draftProject.Assignments);
            _context.ExecuteQuery();

            var allAssignments = _draftProject.Assignments.ToList();
            Console.WriteLine("  Total assignments: " + allAssignments.Count);

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
                    {
                        _context.Load(assignment.Resource, r => r.Id);
                    }
                    if (assignment.Task != null && assignment.Task.ServerObjectIsNull != true)
                    {
                        _context.Load(assignment.Task, t => t.Id, t => t.Start, t => t.Finish);
                    }
                }
                _context.ExecuteQuery();
            }

            var sourceAssignments = allAssignments
                .Where(a => a.Resource != null && a.Resource.ServerObjectIsNull != true && a.Resource.Id == sourceResourceId)
                .ToList();

            Console.WriteLine("  Source assignments: " + sourceAssignments.Count);

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
            var toReplace = new List<DraftAssignment>();
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
                toReplace.Add(oldAsg);
            }

            Console.WriteLine("  To add: " + toAdd.Count + " destination assignment(s), to remove: " + toReplace.Count + " source assignment(s)");

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

            foreach (var oldAsg in toReplace)
            {
                oldAsg.DeleteObject();
            }

            if (toAdd.Count > 0 || toReplace.Count > 0)
            {
                var updateState = _context.WaitForQueue(_draftProject.Update(), DefaultTimeoutSeconds);
                JobStateLog(updateState, "Updating draft assignments for project " + _projectId);
                ThrowIfJobFailed(updateState, "Update", _projectId);
            }

            return toReplace.Count;
        }
        #endregion

        private void CreateAssignmentsViaPsi(Guid resourceId, List<Guid> taskIds)
        {
            using (var ps = System.Management.Automation.PowerShell.Create())
            {
                var script = @"
Param($ProjectServerURL, $ProjectUid, $ResourceUid, $TaskUids)
$svc = New-WebServiceProxy -Uri ""$ProjectServerURL/_vti_bin/PSI/ResourceAssignments.asmx?wsdl"" -UseDefaultCredential
$ds = New-Object System.Data.DataSet
$dt = New-Object System.Data.DataTable
$dt.TableName = ""ResourceAssignment""
$dt.Columns.Add(""PROJ_UID"", [System.Guid])
$dt.Columns.Add(""TASK_UID"", [System.Guid])
$dt.Columns.Add(""RES_UID"", [System.Guid])
$dt.Columns.Add(""ASSN_UID"", [System.Guid])
$dt.Columns.Add(""ASSN_START_DATE"", [System.DateTime])
$dt.Columns.Add(""ASSN_FINISH_DATE"", [System.DateTime])
$dt.Columns.Add(""ASSN_WORK"", [System.Decimal])
foreach ($taskUid in $TaskUids) {
    $row = $dt.NewRow()
    $row[""PROJ_UID""] = [System.Guid]$ProjectUid
    $row[""TASK_UID""] = [System.Guid]$taskUid
    $row[""RES_UID""] = [System.Guid]$ResourceUid
    $row[""ASSN_UID""] = [System.Guid]::NewGuid()
    $row[""ASSN_START_DATE""] = [System.DateTime]::Now
    $row[""ASSN_FINISH_DATE""] = [System.DateTime]::Now.AddDays(1)
    $row[""ASSN_WORK""] = [System.Decimal]8
    $dt.Rows.Add($row)
}
$ds.Tables.Add($dt)
$svc.QueueCreateAssignment($ds, $false)
Start-Sleep -Seconds 10
";
                ps.AddScript(script);
                ps.AddParameter("ProjectServerURL", AllTaskConfig.PwaInstanceUrl);
                ps.AddParameter("ProjectUid", _projectId.ToString());
                ps.AddParameter("ResourceUid", resourceId.ToString());
                ps.AddParameter("TaskUids", taskIds.Select(t => t.ToString()).ToArray());

                ps.Invoke();

                if (ps.Streams.Error.Count > 0)
                {
                    var sb = new System.Text.StringBuilder("PSI create assignment failed: ");
                    foreach (var error in ps.Streams.Error)
                    {
                        sb.Append(error.ToString()).Append(" ");
                    }
                    throw new Exception(sb.ToString());
                }
            }
        }

        #region Private Methods
        private static void ThrowIfJobFailed(JobState jobState, string operation, Guid projectId)
        {
            if (jobState == JobState.Failed ||
                jobState == JobState.FailedNotBlocking ||
                jobState == JobState.CorrelationBlocked)
            {
                throw new Exception(operation + " failed for project " + projectId + ". Job state: " + jobState);
            }
        }

        private static void JobStateLog(JobState jobState, string jobDescription)
        {
            switch (jobState)
            {
                case JobState.Success:
                    Console.WriteLine(jobDescription + " is successfully done.");
                    break;
                case JobState.ReadyForProcessing:
                case JobState.Processing:
                case JobState.ProcessingDeferred:
                    Console.WriteLine(jobDescription + " is taking longer than usual.");
                    break;
                case JobState.Failed:
                case JobState.FailedNotBlocking:
                case JobState.CorrelationBlocked:
                    Console.WriteLine(jobDescription + " failed. The job is in state: " + jobState);
                    break;
                default:
                    Console.WriteLine("Unknown error, job is in state " + jobState);
                    break;
            }
        }
        #endregion
    }
}