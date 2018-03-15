﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime.Tree;
using NLog;
using Rubberduck.Parsing.Inspections;
using Rubberduck.Parsing.Inspections.Abstract;
using Rubberduck.Parsing.Inspections.Resources;
using Rubberduck.Parsing.VBA;
using Rubberduck.Settings;
using Rubberduck.UI;
using Rubberduck.UI.Inspections;
using Rubberduck.VBEditor;

namespace Rubberduck.Inspections
{
    namespace Rubberduck.Inspections
    {
        public class Inspector : IInspector
        {
            private const int _maxDegreeOfInspectionParallelism = -1;

            private readonly IGeneralConfigService _configService;
            private readonly List<IInspection> _inspections;
            private const int AGGREGATION_THRESHOLD = 128;

            public Inspector(IGeneralConfigService configService, IInspectionProvider inspectionProvider)
            {
                _inspections = inspectionProvider.Inspections.ToList();

                _configService = configService;
                configService.SettingsChanged += ConfigServiceSettingsChanged;
            }

            private void ConfigServiceSettingsChanged(object sender, EventArgs e)
            {
                var config = _configService.LoadConfiguration();
                UpdateInspectionSeverity(config);
            }

            private void UpdateInspectionSeverity(Configuration config)
            {
                foreach (var inspection in _inspections)
                {
                    foreach (var setting in config.UserSettings.CodeInspectionSettings.CodeInspections)
                    {
                        if (inspection.Name == setting.Name)
                        {
                            inspection.Severity = setting.Severity;
                        }
                    }
                }
            }

            public async Task<IEnumerable<IInspectionResult>> FindIssuesAsync(RubberduckParserState state, CancellationToken token)
            {
                if (state == null || !state.AllUserDeclarations.Any())
                {
                    return new IInspectionResult[] { };
                }
                token.ThrowIfCancellationRequested();

                state.OnStatusMessageUpdate(RubberduckUI.CodeInspections_Inspecting);
                var allIssues = new ConcurrentBag<IInspectionResult>();
                token.ThrowIfCancellationRequested();

                var config = _configService.LoadConfiguration();
                UpdateInspectionSeverity(config);
                token.ThrowIfCancellationRequested();

                var parseTreeInspections = _inspections
                    .Where(inspection => inspection.Severity != CodeInspectionSeverity.DoNotShow)
                    .OfType<IParseTreeInspection>()
                    .ToArray();
                token.ThrowIfCancellationRequested();

                foreach (var listener in parseTreeInspections.Select(inspection => inspection.Listener))
                {
                    listener.ClearContexts();
                }

                // Prepare ParseTreeWalker based inspections
                var passes = Enum.GetValues(typeof (ParsePass)).Cast<ParsePass>();
                foreach (var parsePass in passes)
                {
                    try
                    {
                        WalkTrees(config.UserSettings.CodeInspectionSettings, state, parseTreeInspections.Where(i => i.Pass == parsePass), parsePass);
                    }
                    catch (Exception e)
                    {
                        LogManager.GetCurrentClassLogger().Warn(e);
                    }
                }
                token.ThrowIfCancellationRequested();

                var inspectionsToRun = _inspections.Where(inspection => inspection.Severity != CodeInspectionSeverity.DoNotShow && RequiredLibrariesArePresent(inspection, state));
                token.ThrowIfCancellationRequested();

                try
                {
                    await Task.Run(() => RunInspectionsInParallel(inspectionsToRun, allIssues, token));
                }
                catch (AggregateException exception)
                {
                    if (exception.Flatten().InnerExceptions.All(ex => ex is OperationCanceledException))
                    {
                        //This eliminates the stack trace, but for the cancellation, this is irrelevant.
                        throw exception.InnerException ?? exception;
                    }

                    LogManager.GetCurrentClassLogger().Error(exception);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    LogManager.GetCurrentClassLogger().Error(e);
                }

                var issuesByType = allIssues.GroupBy(issue => issue.Inspection.Name)
                                            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToList());
                var results = issuesByType.Where(kv => kv.Value.Count <= AGGREGATION_THRESHOLD)
                    .SelectMany(kv => kv.Value)
                    .Union(issuesByType.Where(kv => kv.Value.Count > AGGREGATION_THRESHOLD)
                    .Select(kv => new AggregateInspectionResult(kv.Value.OrderBy(i => i.QualifiedSelection).First(), kv.Value.Count)))
                    .ToList();

                state.OnStatusMessageUpdate(RubberduckUI.ResourceManager.GetString("ParserState_" + state.Status, CultureInfo.CurrentUICulture)); // should be "Ready"
                return results;
            }

            private static bool RequiredLibrariesArePresent(IInspection inspection, RubberduckParserState state)
            {
                var requiredLibraries = inspection.GetType().GetCustomAttributes<RequiredLibraryAttribute>().ToArray();

                if (!requiredLibraries.Any())
                {
                    return true;
                }

                var projectNames = state.DeclarationFinder.Projects.Where(project => project.IsUserDefined == false).Select(project => project.ProjectName).ToArray();

                return requiredLibraries.All(library => projectNames.Contains(library.LibraryName));
            }

            private static void RunInspectionsInParallel(IEnumerable<IInspection> inspectionsToRun,
                ConcurrentBag<IInspectionResult> allIssues, CancellationToken token)
            {
                var options = new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = _maxDegreeOfInspectionParallelism
                };

                Parallel.ForEach(inspectionsToRun,
                    options,
                    inspection => RunInspection(inspection, allIssues, token)
                );
            }

            private static void RunInspection(IInspection inspection, ConcurrentBag<IInspectionResult> allIssues, CancellationToken token)
            {
                try
                {
                    var inspectionResults = inspection.GetInspectionResults(token);

                    token.ThrowIfCancellationRequested();

                    foreach (var inspectionResult in inspectionResults)
                    {
                        allIssues.Add(inspectionResult);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    LogManager.GetCurrentClassLogger().Warn(e);
                }
            }

            private void WalkTrees(CodeInspectionSettings settings, RubberduckParserState state, IEnumerable<IParseTreeInspection> inspections, ParsePass pass)
            {
                var listeners = inspections
                    .Where(i => i.Severity != CodeInspectionSeverity.DoNotShow
                        && i.Pass == pass
                        && !IsDisabled(settings, i))
                    .Select(inspection => inspection.Listener)
                    .ToList();

                if (!listeners.Any())
                {
                    return;
                }

                List<KeyValuePair<QualifiedModuleName, IParseTree>> trees;
                switch (pass)
                {
                    case ParsePass.AttributesPass:
                        trees = state.AttributeParseTrees;
                        break;
                    case ParsePass.CodePanePass:
                        trees = state.ParseTrees;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(pass), pass, null);
                }

                foreach (var componentTreePair in trees)
                {
                    foreach (var listener in listeners)
                    {
                        listener.CurrentModuleName = componentTreePair.Key;
                    }

                    ParseTreeWalker.Default.Walk(new CombinedParseTreeListener(listeners), componentTreePair.Value);
                }
            }

            private bool IsDisabled(CodeInspectionSettings config, IInspection inspection)
            {
                var setting = config.GetSetting(inspection.GetType());
                return setting != null && setting.Severity == CodeInspectionSeverity.DoNotShow;
            }

            public void Dispose()
            {
                if (_configService != null)
                {
                    _configService.SettingsChanged -= ConfigServiceSettingsChanged;
                }

                _inspections.Clear();
            }
        }
    }
}
