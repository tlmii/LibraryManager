﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;

namespace Microsoft.Web.LibraryManager
{
    /// <summary>
    /// Finds conflicts between different libraries, based on files brought in by each library.
    /// </summary>
    internal class LibrariesValidator
    {
        /// <summary>
        /// Validates set of libraries given the dependencies, default destination and default provider
        /// </summary>
        /// <param name="dependencies"></param>
        /// <param name="defaultDestination"></param>
        /// <param name="defaultProvider"></param>
        public LibrariesValidator(IDependencies dependencies, string defaultDestination, string defaultProvider)
        {
            _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
            _defaultDestination = defaultDestination;
            _defaultProvider = defaultProvider;
        }

        private string _defaultDestination;
        private string _defaultProvider;
        private IDependencies _dependencies;

        /// <summary>
        /// Returns a collection of ILibraryOperationResult that represents the status for validation of each 
        ///  library 
        /// </summary>
        /// <param name="libraries"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IEnumerable<ILibraryOperationResult>> GetLibrariesErrorsAsync(
            IEnumerable<ILibraryInstallationState> libraries,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IEnumerable<ILibraryOperationResult> validateLibraries = await ValidatePropertiesAsync(libraries, cancellationToken);

            if (!validateLibraries.All(t => t.Success))
            {
                return validateLibraries;
            }

            IEnumerable<ILibraryOperationResult> expandLibraries= await ExpandLibrariesAsync(libraries, cancellationToken);
            if (!expandLibraries.All(t => t.Success))
            {
                return expandLibraries;
            }

            libraries = expandLibraries.Select(l => l.InstallationState);


            return new List<ILibraryOperationResult> { GetConflictErrors(GetFilesConflicts(libraries, cancellationToken)) };

        }

        /// <summary>
        /// Validates the values of each Library property and returns a collection of ILibraryOperationResult for each of them 
        /// </summary>
        /// <param name="libraries"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IEnumerable<ILibraryOperationResult>> ValidatePropertiesAsync(IEnumerable<ILibraryInstallationState> libraries, CancellationToken cancellationToken)
        {
            List<ILibraryOperationResult> validationStatus = new List<ILibraryOperationResult>();

            foreach (ILibraryInstallationState library in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ILibraryOperationResult result = await library.IsValidAsync(_dependencies);
                if (!result.Success)
                {
                    validationStatus.Add(result);
                }
                else
                {
                    validationStatus.Add(LibraryOperationResult.FromSuccess(library));
                }
            }

            return validationStatus;
        }

        /// <summary>
        /// Expands the files properties for each library 
        /// </summary>
        /// <param name="libraries"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IEnumerable<ILibraryOperationResult>> ExpandLibrariesAsync(IEnumerable<ILibraryInstallationState> libraries, CancellationToken cancellationToken)
        {
            List<ILibraryOperationResult> expandedLibraries = new List<ILibraryOperationResult>();

            foreach (ILibraryInstallationState library in libraries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string installDestination = string.IsNullOrEmpty(library.DestinationPath) ? _defaultDestination : library.DestinationPath;
                string providerId = string.IsNullOrEmpty(library.ProviderId) ? _defaultProvider : library.ProviderId;

                IProvider provider = _dependencies.GetProvider(providerId);
                if (provider == null)
                {
                    return new List<ILibraryOperationResult> { LibraryOperationResult.FromError(PredefinedErrors.ProviderIsUndefined())};
                }

                ILibraryOperationResult desiredState = await provider.UpdateStateAsync(library, cancellationToken);
                if (!desiredState.Success)
                {
                    return new List<ILibraryOperationResult> { desiredState };
                }

                expandedLibraries.Add(desiredState);
            }

            return expandedLibraries;
        }

        /// <summary>
        /// Detects files conflicts in between libraries in the given collection 
        /// </summary>
        /// <param name="libraries"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>A colletion of FileConflict for each conflict</returns>
        public IEnumerable<FileConflict> GetFilesConflicts(IEnumerable<ILibraryInstallationState> libraries, CancellationToken cancellationToken)
        {
            Dictionary<string, List<ILibraryInstallationState>> _fileToLibraryMap = new Dictionary<string, List<ILibraryInstallationState>>(RelativePathEqualityComparer.Instance);

            foreach (ILibraryInstallationState library in libraries)
            {
                string destinationPath = library.DestinationPath;

                IEnumerable<string> files = library.Files.Select(f => Path.Combine(destinationPath, f));

                foreach (string file in files)
                {
                    if (!_fileToLibraryMap.ContainsKey(file))
                    {
                        _fileToLibraryMap[file] = new List<ILibraryInstallationState>();
                    }

                    _fileToLibraryMap[file].Add(library);
                }
            }

            return _fileToLibraryMap
                    .Where(f => f.Value.Count > 1)
                    .Select(f => new FileConflict(f.Key, f.Value));

        }

        /// <summary>
        /// Generates a single ILibraryOperationResult with a collection of IErros based on the collection of FileConflict
        /// </summary>
        /// <param name="fileConflicts"></param>
        /// <returns></returns>
        public ILibraryOperationResult GetConflictErrors(IEnumerable<FileConflict> fileConflicts)
        {
            if (fileConflicts.Any())
            {
                var errors = new List<IError>();
                foreach (FileConflict conflictingLibraryGroup in fileConflicts)
                {
                    errors.Add(PredefinedErrors.ConflictingLibrariesInManifest(conflictingLibraryGroup.File, conflictingLibraryGroup.Libraries.Select(l => l.LibraryId).ToList()));
                }

                return new LibraryOperationResult(errors.ToArray());
            }

            return LibraryOperationResult.FromSuccess(null);
        }
    }


}
