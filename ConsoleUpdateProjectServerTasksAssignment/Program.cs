using ConsoleUpdateProjectServerTasksAssignment.Classes;
using System;
using System.Collections.Generic;

namespace ConsoleUpdateProjectServerTasksAssignment
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;

                Console.WriteLine("Update Project Server task assignments");
                Console.WriteLine("=====================================");

                var projectIds = ReadProjectIds(args);
                var sourceResourceId = ReadResourceUid(args, 1, "source", "6B8631D8-5BE8-E611-80E2-000C2988D4F6");
                var destinationResourceId = ReadResourceUid(args, 2, "destination", "95035403-D84F-EE11-8173-000C2988D4EC");

                Console.WriteLine();
                Console.WriteLine("Source resource: " + ProjectServerProject.GetEnterpriseResourceName(sourceResourceId));
                Console.WriteLine("Destination resource: " + ProjectServerProject.GetEnterpriseResourceName(destinationResourceId));

                if (sourceResourceId == destinationResourceId)
                {
                    Console.WriteLine();
                    Console.WriteLine("Source and destination resources are the same. Nothing to do.");
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Force checking in all projects...");
                new ProjectServerCheckInAllProject().forceCheckInAllProjects();

                if (projectIds == null)
                {
                    Console.WriteLine();
                    Console.WriteLine("Resolving projects with source resource assignments...");
                    projectIds = ProjectServerProject.GetProjectIdsWithSourceAssignment(sourceResourceId);
                }

                if (projectIds.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("No projects found. Nothing to do.");
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Processing " + projectIds.Count + " project(s)...");
                Console.WriteLine();

                var published = 0;
                var skipped = 0;
                var failed = 0;

                foreach (var projectId in projectIds)
                {
                    ProjectServerProject project = null;
                    try
                    {
                        project = new ProjectServerProject(projectId);

                        if (project.IsCheckedOut)
                        {
                            Console.WriteLine("[" + projectId + "] is checked out. Skipping.");
                            skipped++;
                            continue;
                        }

                        project.CheckOutProject();
                        var resourceAdded = project.AddResourceToProject(destinationResourceId);

                        if (resourceAdded)
                        {
                            Console.WriteLine("[" + projectId + "] destination resource added to team.");
                        }

                        var replaced = project.ReplaceAssignmentResource(sourceResourceId, destinationResourceId);

                        if (replaced > 0)
                        {
                            project.CheckInProject();
                            Console.WriteLine("[" + projectId + "] " + replaced + " assignment(s) updated and published.");
                            published++;
                        }
                        else
                        {
                            project.CheckInWithoutPublish();
                            Console.WriteLine("[" + projectId + "] no source assignment found. Skipped.");
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (project != null)
                        {
                            try
                            {
                                project.CheckInWithoutPublish();
                            }
                            catch
                            {
                            }
                        }

                        Console.WriteLine("[" + projectId + "] FAILED: " + FormatException(ex));
                        Console.WriteLine(ex.StackTrace);
                        failed++;
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Done. Published: " + published + ", Skipped: " + skipped + ", Failed: " + failed + ".");

                if (failed > 0)
                {
                    Environment.ExitCode = 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("ERROR: " + FormatException(ex));
                Environment.ExitCode = 1;
            }
        }

        private static string FormatException(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            var current = ex;
            while (current != null)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(current.GetType().Name + ": " + current.Message);
                current = current.InnerException;
            }
            return sb.ToString();
        }

        private static List<Guid> ReadProjectIds(string[] args)
        {
            var input = args.Length > 0 ? args[0] : Prompt("Project UID(s) (comma-separated, or 'all')", "BB9B22E6-7A5B-E911-91C1-00339181022A");

            if (string.IsNullOrWhiteSpace(input) || input.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("All projects will be processed.");
                return null;
            }

            var ids = new List<Guid>();
            var tokens = input.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                var value = token.Trim();
                if (Guid.TryParse(value, out var id))
                {
                    if (!ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
                else
                {
                    Console.WriteLine("Ignoring invalid project UID: " + value);
                }
            }

            return ids;
        }

        private static Guid ReadResourceUid(string[] args, int index, string label, string defaultValue)
        {
            if (args.Length > index && Guid.TryParse(args[index], out var argId))
            {
                return argId;
            }

            while (true)
            {
                var input = Prompt("Enter " + label + " resource UID", defaultValue);

                if (Guid.TryParse(input, out var id))
                {
                    return id;
                }

                Console.WriteLine("'" + input + "' is not a valid GUID. Please try again.");
            }
        }

        private static string Prompt(string message, string defaultValue)
        {
            Console.WriteLine();
            Console.Write(message + " [" + defaultValue + "]: ");
            var input = Console.ReadLine();
            if (input == null)
            {
                throw new Exception("No input available.");
            }
            var trimmed = input.Trim();
            return string.IsNullOrEmpty(trimmed) ? defaultValue : trimmed;
        }
    }
}