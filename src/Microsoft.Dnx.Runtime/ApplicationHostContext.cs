// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using NuGet;

namespace Microsoft.Dnx.Runtime
{
    public class ApplicationHostContext
    {
        private readonly LockFile _lockFile;
        private readonly Lazy<List<DiagnosticMessage>> _lockFileDiagnostics =
            new Lazy<List<DiagnosticMessage>>();

        public ApplicationHostContext(string projectDirectory,
                                      string packagesDirectory,
                                      FrameworkName targetFramework,
                                      bool skipLockFileValidation = false)
        {
            ProjectDirectory = projectDirectory;
            RootDirectory = ProjectResolver.ResolveRootDirectory(ProjectDirectory);
            var projectResolver = new ProjectResolver(ProjectDirectory, RootDirectory);
            FrameworkReferenceResolver = new FrameworkReferenceResolver();

            PackagesDirectory = packagesDirectory ?? PackageDependencyProvider.ResolveRepositoryPath(RootDirectory);

            var referenceAssemblyDependencyResolver = new ReferenceAssemblyDependencyResolver(FrameworkReferenceResolver);
            var gacDependencyResolver = new GacDependencyResolver();
            var projectDependencyProvider = new ProjectReferenceDependencyProvider(projectResolver);
            var unresolvedDependencyProvider = new UnresolvedDependencyProvider();
            DependencyWalker dependencyWalker = null;

            var projectName = PathUtility.GetDirectoryName(ProjectDirectory);

            Project project;
            if (projectResolver.TryResolveProject(projectName, out project))
            {
                Project = project;
            }
            else
            {
                throw new InvalidOperationException(
                    string.Format("Unable to resolve project '{0}' from {1}", projectName, ProjectDirectory));
            }

            var projectLockJsonPath = Path.Combine(ProjectDirectory, LockFileReader.LockFileName);
            var lockFileExists = File.Exists(projectLockJsonPath);
            var validLockFile = false;

            if (lockFileExists)
            {
                var lockFileReader = new LockFileReader();
                _lockFile = lockFileReader.Read(projectLockJsonPath);
                validLockFile = _lockFile.IsValidForProject(project);

                // When the only invalid part of a lock file is version number,
                // we shouldn't skip lock file validation because we want to leave all dependencies unresolved, so that
                // VS can be aware of this version mismatch error and automatically do restore
                skipLockFileValidation = skipLockFileValidation && (_lockFile.Version == Constants.LockFileVersion);

                if (validLockFile || skipLockFileValidation)
                {
                    var nugetDependencyProvider = new PackageDependencyProvider(PackagesDirectory, _lockFile);

                    dependencyWalker = new DependencyWalker(new IDependencyProvider[] {
                        projectDependencyProvider,
                        nugetDependencyProvider,
                        referenceAssemblyDependencyResolver,
                        gacDependencyResolver,
                        unresolvedDependencyProvider
                    });
                }
            }

            if ((!validLockFile && !skipLockFileValidation) || !lockFileExists)
            {
                // We don't add NuGetDependencyProvider to DependencyWalker
                // It will leave all NuGet packages unresolved and give error message asking users to run "dnu restore"
                dependencyWalker = new DependencyWalker(new IDependencyProvider[] {
                    projectDependencyProvider,
                    referenceAssemblyDependencyResolver,
                    gacDependencyResolver,
                    unresolvedDependencyProvider
                });
            }

            dependencyWalker.Walk(Project.Name, Project.Version, targetFramework);

            LibraryManager = new LibraryManager(dependencyWalker.Libraries);
        }

        public Project Project { get; private set; }

        public LibraryManager LibraryManager { get; private set; }

        // TODO: Remove
        public FrameworkReferenceResolver FrameworkReferenceResolver { get; private set; }

        public string RootDirectory { get; private set; }

        public string ProjectDirectory { get; private set; }

        public string PackagesDirectory { get; private set; }

        public IEnumerable<DiagnosticMessage> GetLockFileDiagnostics()
        {
            if (_lockFileDiagnostics.IsValueCreated)
            {
                return _lockFileDiagnostics.Value;
            }

            if (_lockFile == null)
            {
                _lockFileDiagnostics.Value.Add(new DiagnosticMessage(
                    $"The expected lock file doesn't exist. Please run \"dnu restore\" to generate a new lock file.",
                    Path.Combine(Project.ProjectDirectory, LockFileReader.LockFileName),
                    DiagnosticMessageSeverity.Error));
            }
            else
            {
                _lockFileDiagnostics.Value.AddRange(_lockFile.GetDiagnostics(Project));
            }
            return _lockFileDiagnostics.Value;
        }

        public IEnumerable<DiagnosticMessage> GetAllDiagnostics()
        {
            return GetLockFileDiagnostics()
                .Concat(LibraryManager.GetDependencyDiagnostics(Project.ProjectFilePath));
        }
    }
}