using System;
using System.Management.Automation;
using System.Text;
using System.Threading;

namespace ConsoleUpdateProjectServerTasksAssignment.Classes
{
    internal class ProjectServerCheckInAllProject
    {
        public void forceCheckInAllProjects()
        {
            using (PowerShell PowerShellInstance = PowerShell.Create())
            {
                var script = getScript();

                PowerShellInstance.AddScript(script);

                PowerShellInstance.AddParameter("ProjectServerURL", AllTaskConfig.PwaInstanceUrl);
                PowerShellInstance.AddParameter("DatabaseServer", AllTaskConfig.ProjectServerDataBaseServer);
                PowerShellInstance.AddParameter("DraftDB", AllTaskConfig.ProjectServerDataBaseName);

                PSDataCollection<PSObject> outputCollection = new PSDataCollection<PSObject>();
                outputCollection.DataAdded += outputCollection_DataAdded;

                PowerShellInstance.Streams.Error.DataAdded += Error_DataAdded;

                IAsyncResult result = PowerShellInstance.BeginInvoke<PSObject, PSObject>(null, outputCollection);

                while (result.IsCompleted == false)
                {
                    Console.WriteLine("Waiting for pipeline to finish...");
                    Thread.Sleep(1000);
                }

                Console.WriteLine("Execution has stopped. The pipeline state: " + PowerShellInstance.InvocationStateInfo.State);

                if (PowerShellInstance.InvocationStateInfo.State == PSInvocationState.Failed)
                {
                    if (PowerShellInstance.InvocationStateInfo.Reason != null && PowerShellInstance.InvocationStateInfo.Reason.Message != null)
                    {
                        Console.WriteLine(PowerShellInstance.InvocationStateInfo.Reason.Message);
                        throw new Exception("Error in Checkin All ProjectServer Project PowerShell - " + PowerShellInstance.InvocationStateInfo.Reason.Message);
                    }
                }

                foreach (PSObject outputItem in outputCollection)
                {
                    Console.WriteLine(outputItem.BaseObject.ToString());
                }

                if (PowerShellInstance.Streams.Error.Count > 0)
                {
                    var sb = new StringBuilder("Error in Checkin All ProjectServer Project PowerShell - ");
                    foreach (var item in PowerShellInstance.Streams.Error)
                    {
                        if (item.Exception != null) sb.Append(item.Exception.Message + " " + Environment.NewLine);
                    }
                    throw new Exception(sb.ToString());
                }
            }
        }

        void outputCollection_DataAdded(object sender, DataAddedEventArgs e)
        {
            Console.WriteLine("Object added to output.");
        }

        void Error_DataAdded(object sender, DataAddedEventArgs e)
        {
            Console.WriteLine("An error was written to the Error stream!");
        }

        string getScript()
        {
            return  "<#" + Environment.NewLine +

".SYNOPSIS" + Environment.NewLine +
"Forces a Checkin of all projects in Microsoft Project Server 2010" + Environment.NewLine +

".DESCRIPTION" + Environment.NewLine +
"The list of currently checkedout projects is queried from the Draft database." + Environment.NewLine +
"After the list of Project UIDs is retrieved the script makes a connection to the PSI Projects WebService to initiate Force " + Environment.NewLine +
"Checkin of all the projects. The Checkin requests enter the Project Server queue and will be executed there." + Environment.NewLine +

".PARAMETER ProjectServerURL	" + Environment.NewLine +
"URL of the Project Server instance to be connected to (example: http://projectserver/pwa" + Environment.NewLine +

".PARAMETER DatabaseServer" + Environment.NewLine +
"Name of the SQL Server (or database instance) containing the Draft database (example: SQLSRV1\\INSTANCE1)" + Environment.NewLine +

".PARAMETER DraftDB" + Environment.NewLine +
"Name of the Draft Database (example: PWA_Draft)" + Environment.NewLine +

".EXAMPLE" + Environment.NewLine +
".\\SOLVIN_EasyCheckin.ps1 -ProjectServerURL http://projectserver/pwa -DatabaseServer SQLSRV1 -DraftDB PWA_Draft" + Environment.NewLine +
"Force CheckIn all projects currently checked out" + Environment.NewLine +

".NOTES" + Environment.NewLine +
"You need to have Read permissions to the Draft Database and Project Server Administrative permissions to run this Script." + Environment.NewLine +

"#>" + Environment.NewLine +


"#######define parameters" + Environment.NewLine +
"PARAM(" + Environment.NewLine +
"    [parameter(Mandatory=$true)]" + Environment.NewLine +
"	[string] " + Environment.NewLine +
"	$ProjectServerURL " + Environment.NewLine +
"	," + Environment.NewLine +
"	[parameter(Mandatory=$true)]" + Environment.NewLine +
"	[string] " + Environment.NewLine +
"	$DatabaseServer " + Environment.NewLine +
"	," + Environment.NewLine +
"	[parameter(Mandatory=$true)]" + Environment.NewLine +
"	[string] " + Environment.NewLine +
"	$DraftDB " + Environment.NewLine +
"   )" + Environment.NewLine +

"#connect to database with current userid / password" + Environment.NewLine +

"$connection= new-object system.data.sqlclient.sqlconnection #Set new object to connect to sql database " + Environment.NewLine +

"$connection.ConnectionString =\"server=$Databaseserver;database=$DraftDB;trusted_connection=True\" # Connectiongstring \"setting for local machine database with window authentication " + Environment.NewLine +

"Write \"connection information:\" " + Environment.NewLine +

"$connection #List connection information " + Environment.NewLine +

"Write \"connect to database successful.\" " + Environment.NewLine +

"$connection.open() #Connecting successful " + Environment.NewLine +

"#########query drop paths############################################################ " + Environment.NewLine +

"$SqlCmd = New-Object System.Data.SqlClient.SqlCommand #setting object to use sql commands " + Environment.NewLine +

"$SqlQuery = \"SELECT [Proj_UID] FROM [draft].[MSP_PROJECTS] WHERE proj_checkoutby is not null and proj_type in (0,5,6)\" #setting query \"get drop paths\"  " + Environment.NewLine +

"$SqlCmd.CommandText = $SqlQuery # get query " + Environment.NewLine +

"$SqlAdapter = New-Object System.Data.SqlClient.SqlDataAdapter # " + Environment.NewLine +

"$SqlAdapter.SelectCommand = $SqlCmd" + Environment.NewLine +

"$SqlCmd.Connection = $connection" + Environment.NewLine +

"$DataSet = New-Object System.Data.DataSet " + Environment.NewLine +

"$SqlAdapter.Fill($DataSet)  " + Environment.NewLine +

"$connection.Close() " + Environment.NewLine +

"	$svcPSProxy = New-WebServiceProxy -uri \"$ProjectServerURL/_vti_bin/PSI/Project.asmx?wsdl\" -useDefaultCredential" + Environment.NewLine +

"	foreach ($row in $DataSet.Tables[0])" + Environment.NewLine +
"	{ " + Environment.NewLine +
"		if ($row.ItemArray.Count -gt 0)" + Environment.NewLine +
"	    {" + Environment.NewLine +
"			Write \"Checkin Project \" + $row[0]" + Environment.NewLine +
"			$projId = [System.Guid]$row[0]" + Environment.NewLine +
"			$svcPSProxy.QueueCheckInProject([System.Guid]::NewGuid() , $projId, \"true\",[System.Guid]::NewGuid(),\"SOLVIN Easy CheckIn\")" + Environment.NewLine +
"		}" + Environment.NewLine +
"	}"
;
        }
    }
}