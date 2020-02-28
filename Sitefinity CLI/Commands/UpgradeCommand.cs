﻿using EnvDTE;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Sitefinity_CLI.PackageManagement;
using Sitefinity_CLI.VisualStudio;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace Sitefinity_CLI.Commands
{
    [HelpOption]
    [Command(Constants.UpgradeCommandName, Description = "Upgrade Sitefinity project/s to a newer version of Sitefinity.")]
    internal class UpgradeCommand
    {
        [Argument(0, Description = Constants.ProjectOrSolutionPathOptionDescription)]
        [Required(ErrorMessage = "You must specify a path to a solution file.")]
        public string SolutionPath { get; set; }

        [Argument(1, Description = Constants.VersionToOptionDescription)]
        [Required(ErrorMessage = "You must specify the Sitefinity version to upgrade to.")]
        public string Version { get; set; }

        public UpgradeCommand(
            ISitefinityPackageManager sitefinityPackageManager,
            ICsProjectFileEditor csProjectFileEditor,
            ILogger<object> logger,
            IVisualStudioWorker visualStudioWorker)
        {
            this.sitefinityPackageManager = sitefinityPackageManager;
            this.csProjectFileEditor = csProjectFileEditor;
            this.logger = logger;
            this.visualStudioWorker = visualStudioWorker;
            this.processedPackagesPerProjectCache = new Dictionary<string, HashSet<string>>();
        }

        protected async Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            try
            {
                await this.ExecuteUpgrade();

                return 0;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex.Message);

                return 1;
            }
            ////finally
            ////{
            ////    this.visualStudioWorker.Dispose();
            ////}
        }

        private async Task ExecuteUpgrade()
        {
            if (!File.Exists(this.SolutionPath))
            {
                throw new FileNotFoundException(string.Format(Constants.FileNotFoundMessage, this.SolutionPath));
            }

            this.logger.LogInformation("Searching the provided project/s for Sitefinity references...");

            IEnumerable<string> projectFilePaths = this.GetProjectsToUpgrade(this.SolutionPath);

            if (!projectFilePaths.Any())
            {
                Utils.WriteLine(Constants.NoProjectsFoundToUpgradeWarningMessage, ConsoleColor.Yellow);
            }

            this.logger.LogInformation(string.Format(Constants.NumberOfProjectsWithSitefinityReferencesFoundSuccessMessage, projectFilePaths.Count()));

            this.logger.LogInformation(string.Format("Collecting Sitefinity NuGet package tree for version \"{0}\"...", this.Version));
            NuGetPackage newSitefinityPackage = await this.sitefinityPackageManager.GetSitefinityPackageTree(this.Version);

            await this.sitefinityPackageManager.Restore(this.SolutionPath);
            await this.sitefinityPackageManager.Install(newSitefinityPackage.Id, newSitefinityPackage.Version, this.SolutionPath);

            await this.GeneratePowershellConfig(projectFilePaths, newSitefinityPackage);

            var updaterPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, PowershellFolderName, "Updater.ps1");
            this.visualStudioWorker.Initialize(this.SolutionPath);
            this.visualStudioWorker.ExecuteScript(updaterPath);
            this.EnsureOperationSuccess();

            this.visualStudioWorker.Dispose();

            this.SyncProjectReferencesWithPackages(projectFilePaths, Path.GetDirectoryName(this.SolutionPath));

            this.logger.LogInformation(string.Format("Successfully updated '{0}' to version '{1}'", this.SolutionPath, this.Version));
        }

        private string DetectSitefinityVersion(string sitefinityProjectPath)
        {
            CsProjectFileReference sitefinityReference = this.csProjectFileEditor.GetReferences(sitefinityProjectPath)
                .FirstOrDefault(r => this.IsSitefinityReference(r));

            if (sitefinityReference != null)
            {
                string versionPattern = @"Version=(.*?),";
                Match versionMatch = Regex.Match(sitefinityReference.Include, versionPattern);

                if (versionMatch.Success)
                {
                    return versionMatch.Groups[1].Value;
                }
            }

            return null;
        }

        private bool ContainsSitefinityRefKeyword(CsProjectFileReference projectReference)
        {
            return projectReference.Include.Contains(TelerikSitefinityReferenceKeyWords) || projectReference.Include.Contains(ProgressSitefinityReferenceKeyWords);
        }

        private void EnsureOperationSuccess()
        {
            this.logger.LogInformation("Waiting for operation to complete...");

            var resultFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PowershellFolderName, "result.log");
            var progressFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PowershellFolderName, "progress.log");
            File.Delete(resultFile);
            int waitStep = 500;
            int iterations = 0;
            var lastProgressUpdate = string.Empty;
            while (true)
            {
                if (!File.Exists(resultFile))
                {
                    if (iterations % 2 == 0 && File.Exists(progressFile))
                    {
                        try
                        {
                            var progressInfo = this.ReadAllTextFromFile(progressFile).Replace("\r\n", string.Empty);
                            if (lastProgressUpdate != progressInfo)
                            {
                                lastProgressUpdate = progressInfo;
                                this.logger.LogInformation(progressInfo);
                            }
                        }
                        catch { }
                    }

                    iterations++;

                    // Last operation is still not executed and the file is not created.
                    System.Threading.Thread.Sleep(waitStep);
                    continue;
                }

                System.Threading.Thread.Sleep(waitStep);
                var result = this.ReadAllTextFromFile(resultFile);
                if (result != "success")
                {
                    this.logger.LogError(string.Format("Powershell upgrade failed. {0}", result));
                    throw new Exception("Powershell upgrade failed");
                }

                break;
            }

            this.logger.LogInformation("Operation completed successfully!");
        }

        private string ReadAllTextFromFile(string path)
        {
            using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var textReader = new StreamReader(fileStream))
            {
                return textReader.ReadToEnd();
            }
        }

        private async Task GeneratePowershellConfig(IEnumerable<string> projectFilePaths, NuGetPackage newSitefinityPackage)
        {
            this.logger.LogInformation("Exporting powershell config...");

            this.packagesPerProject = new Dictionary<string, List<NuGetPackage>>();

            var powerShellXmlConfig = new XmlDocument();
            var powerShellXmlConfigNode = powerShellXmlConfig.CreateElement("config");
            powerShellXmlConfig.AppendChild(powerShellXmlConfigNode);

            foreach (string projectFilePath in projectFilePaths)
            {
                var projectNode = powerShellXmlConfig.CreateElement("project");
                var projectNameAttr = powerShellXmlConfig.CreateAttribute("name");
                projectNameAttr.Value = projectFilePath.Split(new string[] { "\\", ".csproj" }, StringSplitOptions.RemoveEmptyEntries).Last();
                projectNode.Attributes.Append(projectNameAttr);
                powerShellXmlConfigNode.AppendChild(projectNode);

                packagesPerProject[projectFilePath] = new List<NuGetPackage>();

                var currentSitefinityVersion = this.DetectSitefinityVersion(projectFilePath);

                if (string.IsNullOrEmpty(currentSitefinityVersion))
                {
                    this.logger.LogInformation(string.Format("Skip upgrade for project: \"{0}\". Current Sitefinity version was not detected.", projectFilePath));
                    continue;
                }

                this.logger.LogInformation(string.Format("Detected sitefinity version for \"{0}\" - \"{1}\"", projectFilePath, currentSitefinityVersion));

                this.logger.LogInformation(string.Format("Collecting Sitefinity NuGet package tree for \"{0}\"...", projectFilePath));
                NuGetPackage currentSitefinityPackage = await this.sitefinityPackageManager.GetSitefinityPackageTree(currentSitefinityVersion);

                this.IteratePackages(projectFilePath, currentSitefinityPackage, newSitefinityPackage, (package) =>
                {
                    var packageNode = powerShellXmlConfig.CreateElement("package");
                    var nameAttr = powerShellXmlConfig.CreateAttribute("name");
                    nameAttr.Value = package.Id;
                    var versionAttr = powerShellXmlConfig.CreateAttribute("version");
                    versionAttr.Value = package.Version;
                    packageNode.Attributes.Append(nameAttr);
                    packageNode.Attributes.Append(versionAttr);
                    projectNode.AppendChild(packageNode);

                    packagesPerProject[projectFilePath].Add(package);
                });
            }

            powerShellXmlConfig.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PowershellFolderName, "config.xml"));

            this.logger.LogInformation("Successfully exported powershell config!");
        }

        private void IteratePackages(string projectFilePath, NuGetPackage currentSitefinityPackage, NuGetPackage newSitefinityPackage, Action<NuGetPackage> action)
        {
            this.processedPackagesPerProjectCache[projectFilePath] = new HashSet<string>();
            var packagesQueue = new Queue<NuGetPackage>();
            packagesQueue.Enqueue(newSitefinityPackage);

            while (packagesQueue.Any())
            {
                var package = packagesQueue.Dequeue();

                bool isPackageAlreadyProcessed = this.processedPackagesPerProjectCache[projectFilePath].Contains(package.Id);
                if (isPackageAlreadyProcessed)
                {
                    continue;
                }

                this.processedPackagesPerProjectCache[projectFilePath].Add(package.Id);

                bool packageExists = this.sitefinityPackageManager.PackageExists(package.Id, projectFilePath);
                if (packageExists)
                {
                    action(package);
                }

                if (package.Dependencies != null)
                {
                    var dependenciesOfDependency = new List<NuGetPackage>();
                    foreach (NuGetPackage nuGetPackageDependency in package.Dependencies)
                    {
                        // Package manager can't update packages that are dependencies of other packages.
                        var parentDependencies = currentSitefinityPackage.Dependencies.Where(directDependency => directDependency.Dependencies.Any(d => d.Id == nuGetPackageDependency.Id));
                        if (!parentDependencies.Any())
                        {
                            packagesQueue.Enqueue(nuGetPackageDependency);
                        }
                        else
                        {
                            dependenciesOfDependency.Add(nuGetPackageDependency);
                        }
                    }

                    // Finally include the dependencies that are also dependencies in other packages
                    foreach (var dependencyOfDependency in dependenciesOfDependency)
                    {
                        packagesQueue.Enqueue(dependencyOfDependency);
                    }
                }
            }
        }

        private void SyncProjectReferencesWithPackages(IEnumerable<string> projectFilePaths, string solutionFolder)
        {
            foreach (string projectFilePath in projectFilePaths)
            {
                var packages = new List<NuGetPackage>(this.packagesPerProject[projectFilePath]);
                packages.Reverse();
                this.sitefinityPackageManager.SyncReferencesWithPackages(projectFilePath, solutionFolder, packages);
            }
        }

        private IEnumerable<string> GetProjectsToUpgrade(string projectOrSolutionPath)
        {
            IList<string> projectFiles = new List<string>();

            if (projectOrSolutionPath.EndsWith(Constants.CsprojFileExtension, StringComparison.InvariantCultureIgnoreCase))
            {
                projectFiles.Add(projectOrSolutionPath);
            }
            else if (projectOrSolutionPath.EndsWith(Constants.SlnFileExtension, StringComparison.InvariantCultureIgnoreCase))
            {
                projectFiles = SolutionFileEditor.GetProjects(projectOrSolutionPath)
                    .Select(sp => sp.AbsolutePath)
                    .Where(ap => ap.EndsWith(Constants.CsprojFileExtension, StringComparison.InvariantCultureIgnoreCase))
                    .ToList();
            }
            else
            {
                throw new Exception(string.Format(Constants.FileNotProjectOrSolutionMessage, projectOrSolutionPath));
            }

            projectFiles = projectFiles.Where(pf => this.HasSitefinityReferences(pf)).ToList();

            return projectFiles;
        }

        private bool HasSitefinityReferences(string projectFilePath)
        {
            IEnumerable<CsProjectFileReference> references = this.csProjectFileEditor.GetReferences(projectFilePath);
            bool hasSitefinityReferences = references.Any(r => this.IsSitefinityReference(r));

            return hasSitefinityReferences;
        }

        private bool IsSitefinityReference(CsProjectFileReference reference)
        {
            return this.ContainsSitefinityRefKeyword(reference) && reference.Include.Contains($"PublicKeyToken={SitefinityPublicKeyToken}");
        }

        private readonly ISitefinityPackageManager sitefinityPackageManager;

        private readonly ICsProjectFileEditor csProjectFileEditor;

        private readonly ILogger<object> logger;

        private readonly IVisualStudioWorker visualStudioWorker;

        private readonly IDictionary<string, HashSet<string>> processedPackagesPerProjectCache;

        private Dictionary<string, List<NuGetPackage>> packagesPerProject;

        private const string TelerikSitefinityReferenceKeyWords = "Telerik.Sitefinity";

        private const string ProgressSitefinityReferenceKeyWords = "Progress.Sitefinity";

        private const string PowershellFolderName = "PowerShell";

        private const string SitefinityPublicKeyToken = "b28c218413bdf563";
    }
}
