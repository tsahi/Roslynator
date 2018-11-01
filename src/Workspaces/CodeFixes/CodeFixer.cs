﻿// Copyright (c) Josef Pihrt. All rights reserved. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Roslynator.Formatting;
using static Roslynator.Logger;

namespace Roslynator.CodeFixes
{
    public class CodeFixer
    {
        private readonly AnalyzerAssemblyList _analyzerAssemblies = new AnalyzerAssemblyList();

        private readonly AnalyzerAssemblyList _analyzerReferences = new AnalyzerAssemblyList();

        private static readonly CompilationWithAnalyzersOptions _defaultCompilationWithAnalyzersOptions = new CompilationWithAnalyzersOptions(
            options: default(AnalyzerOptions),
            onAnalyzerException: null,
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false);

        public CodeFixer(Solution solution, IEnumerable<string> analyzerAssemblies = null, IFormatProvider formatProvider = null, CodeFixerOptions options = null)
        {
            Workspace = solution.Workspace;
            Options = options ?? CodeFixerOptions.Default;

            if (analyzerAssemblies != null)
                _analyzerAssemblies.LoadFrom(analyzerAssemblies);

            FormatProvider = formatProvider;
        }

        public Workspace Workspace { get; }

        public CodeFixerOptions Options { get; }

        public IFormatProvider FormatProvider { get; }

        private Solution CurrentSolution => Workspace.CurrentSolution;

        public async Task FixSolutionAsync(CancellationToken cancellationToken = default)
        {
            ImmutableArray<ProjectId> projects = CurrentSolution
                .GetProjectDependencyGraph()
                .GetTopologicallySortedProjects(cancellationToken)
                .ToImmutableArray();

            foreach (string id in Options.IgnoredDiagnosticIds.OrderBy(f => f))
                WriteLine($"Ignore diagnostic '{id}'", Verbosity.Detailed);

            foreach (string id in Options.IgnoredCompilerDiagnosticIds.OrderBy(f => f))
                WriteLine($"Ignore compiler diagnostic '{id}'", Verbosity.Detailed);

            var results = new List<ProjectFixResult>();

            Stopwatch stopwatch = Stopwatch.StartNew();

            TimeSpan lastElapsed = TimeSpan.Zero;

            for (int i = 0; i < projects.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Project project = CurrentSolution.GetProject(projects[i]);

                string language = project.Language;

                if (Options.IsSupportedProject(project))
                {
                    WriteLine($"Fix '{project.Name}' {$"{i + 1}/{projects.Length}"}", ConsoleColor.Cyan, Verbosity.Minimal);

                    ProjectFixResult result = await FixProjectAsync(project, cancellationToken).ConfigureAwait(false);

                    results.Add(result);

                    if (result.Kind == ProjectFixKind.CompilerError)
                        break;

                    if (Options.FileBannerLines.Length > 0)
                        await AddFileBannerAsync(project, Options.FileBannerLines, cancellationToken).ConfigureAwait(false);

                    if (Options.Format)
                        await FormatProjectAsync(project, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    WriteLine($"Skip '{project.Name}' {$"{i + 1}/{projects.Length}"}", ConsoleColor.DarkGray, Verbosity.Minimal);

                    results.Add(ProjectFixResult.Skipped);
                }

                TimeSpan elapsed = stopwatch.Elapsed;

                WriteLine($"Done fixing '{project.Name}' in {elapsed - lastElapsed:mm\\:ss\\.ff}", Verbosity.Normal);

                lastElapsed = elapsed;
            }

            stopwatch.Stop();

            IEnumerable<DiagnosticDescriptor> supportedDiagnostics = results
                .SelectMany(f => f.Analyzers)
                .Distinct()
                .SelectMany(f => f.SupportedDiagnostics)
                .Distinct(DiagnosticDescriptorComparer.Id);

            DiagnosticDescriptor[] fixedDiagnostics = results
                .SelectMany(f => f.FixedDiagnosticIds)
                .Distinct()
                .Join(supportedDiagnostics, id => id, d => d.Id, (_, d) => d)
                .OrderBy(f => f.Id)
                .ToArray();

            if (fixedDiagnostics.Length > 0)
            {
                WriteLine(Verbosity.Normal);
                WriteLine("Fixed diagnostics:", Verbosity.Normal);

                int maxIdLength = fixedDiagnostics.Max(f => f.Id.Length);

                foreach (DiagnosticDescriptor diagnosticDescriptor in fixedDiagnostics)
                {
                    WriteLine($"  {diagnosticDescriptor.Id.PadRight(maxIdLength)} {diagnosticDescriptor.Title}", Verbosity.Normal);
                }
            }

            WriteLine(Verbosity.Minimal);
            WriteLine($"Done fixing solution '{CurrentSolution.FilePath}' in {stopwatch.Elapsed:mm\\:ss\\.ff}", ConsoleColor.Green, Verbosity.Minimal);
        }

        public async Task<ProjectFixResult> FixProjectAsync(Project project, CancellationToken cancellationToken = default)
        {
            string language = project.Language;

            (ImmutableArray<DiagnosticAnalyzer> analyzers, ImmutableArray<CodeFixProvider> fixers) = Utilities.GetAnalyzersAndFixers(
                project: project,
                analyzerAssemblies: _analyzerAssemblies,
                analyzerReferences: _analyzerReferences,
                options: Options);

            if (!analyzers.Any())
            {
                WriteLine($"  No analyzers found to analyze '{project.Name}'", ConsoleColor.DarkGray, Verbosity.Normal);
                return ProjectFixResult.NoAnalyzers;
            }

            if (!fixers.Any())
            {
                WriteLine($"  No fixers found to fix '{project.Name}'", ConsoleColor.DarkGray, Verbosity.Normal);
                return new ProjectFixResult(ImmutableArray<string>.Empty, analyzers, fixers, ProjectFixKind.NoFixers);
            }

            Dictionary<string, ImmutableArray<DiagnosticAnalyzer>> analyzersById = analyzers
                .SelectMany(f => f.SupportedDiagnostics.Select(d => (id: d.Id, analyzer: f)))
                .GroupBy(f => f.id, f => f.analyzer)
                .ToDictionary(g => g.Key, g => g.Select(analyzer => analyzer).Distinct().ToImmutableArray());

            Dictionary<string, ImmutableArray<CodeFixProvider>> fixersById = fixers
                .Where(f => f.GetFixAllProvider() != null)
                .SelectMany(f => f.FixableDiagnosticIds.Select(id => (id, fixer: f)))
                .GroupBy(f => f.id)
                .ToDictionary(f => f.Key, g => g.Select(f => f.fixer).ToImmutableArray());

            WriteAnalyzers(analyzers, ConsoleColor.DarkGray);
            WriteFixers(fixers, ConsoleColor.DarkGray);

            var fixKind = ProjectFixKind.Success;

            ImmutableHashSet<string>.Builder diagnosticIds = ImmutableHashSet.CreateBuilder(StringComparer.Ordinal);

            ImmutableArray<Diagnostic> previousDiagnostics = ImmutableArray<Diagnostic>.Empty;
            ImmutableArray<Diagnostic> previousPreviousDiagnostics = ImmutableArray<Diagnostic>.Empty;

            int iterationCount = 1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                project = CurrentSolution.GetProject(project.Id);

                WriteLine($"  Compile '{project.Name}'{((iterationCount > 1) ? $" iteration {iterationCount}" : "")}", Verbosity.Normal);

                Compilation compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

                ImmutableArray<Diagnostic> compilerDiagnostics = compilation.GetDiagnostics(cancellationToken);

                if (!VerifyCompilerDiagnostics(compilerDiagnostics))
                {
                    fixKind = ProjectFixKind.CompilerError;
                    break;
                }

                var compilationWithAnalyzers = new CompilationWithAnalyzers(compilation, analyzers, _defaultCompilationWithAnalyzersOptions);

                WriteLine($"  Analyze '{project.Name}'", Verbosity.Normal);

                ImmutableArray<Diagnostic> diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);

                foreach (string message in diagnostics
                    .Where(f => f.IsAnalyzerExceptionDiagnostic())
                    .Select(f => f.ToString())
                    .Distinct())
                {
                    WriteLine(message, ConsoleColor.Yellow, Verbosity.Diagnostic);
                }

                diagnostics = diagnostics
                    .Where(f => Options.IsSupportedDiagnostic(f)
                        && analyzersById.ContainsKey(f.Id)
                        && fixersById.ContainsKey(f.Id))
                    .Concat(compilerDiagnostics
                        .Where(f => f.Severity != DiagnosticSeverity.Error
                            && !Options.IgnoredCompilerDiagnosticIds.Contains(f.Id)
                            && fixersById.ContainsKey(f.Id)))
                    .ToImmutableArray();

                int length = diagnostics.Length;

                if (length == 0)
                    break;

                if (length == previousDiagnostics.Length
                    && !diagnostics.Except(previousDiagnostics, DiagnosticDeepEqualityComparer.Instance).Any())
                {
                    break;
                }

                if (length == previousPreviousDiagnostics.Length
                    && !diagnostics.Except(previousPreviousDiagnostics, DiagnosticDeepEqualityComparer.Instance).Any())
                {
                    WriteLine("  Infinite loop detected: Reported diagnostics have been previously fixed.", ConsoleColor.Yellow, Verbosity.Minimal);

                    string baseDirectoryPath = Path.GetDirectoryName(project.FilePath);

                    WriteLine(Verbosity.Detailed);
                    WriteLine("  Diagnostics:", Verbosity.Detailed);
                    WriteDiagnostics(diagnostics, baseDirectoryPath: baseDirectoryPath, formatProvider: FormatProvider, indentation: "    ", verbosity: Verbosity.Detailed);
                    WriteLine(Verbosity.Detailed);
                    WriteLine("  Previous diagnostics:", Verbosity.Detailed);
                    WriteDiagnostics(previousDiagnostics, baseDirectoryPath: baseDirectoryPath, formatProvider: FormatProvider, indentation: "    ", verbosity: Verbosity.Detailed);
                    WriteLine(Verbosity.Detailed);

                    fixKind = ProjectFixKind.InfiniteLoop;
                    break;
                }

                WriteLine($"  Found {length} {((length == 1) ? "diagnostic" : "diagnostics")} in '{project.Name}'", Verbosity.Normal);

                foreach (DiagnosticDescriptor diagnosticDescriptor in diagnostics
                    .Select(f => f.Descriptor)
                    .Distinct(DiagnosticDescriptorComparer.Id)
                    .OrderBy(f => f.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string diagnosticId = diagnosticDescriptor.Id;

                    ProjectFixKind fixKind2 = await FixDiagnosticsAsync(
                        diagnosticId,
                        CurrentSolution.GetProject(project.Id),
                        (diagnosticDescriptor.CustomTags.Contains(WellKnownDiagnosticTags.Compiler))
                            ? default(ImmutableArray<DiagnosticAnalyzer>)
                            : analyzersById[diagnosticId],
                        fixersById[diagnosticId],
                        cancellationToken).ConfigureAwait(false);

                    diagnosticIds.Add(diagnosticId);

                    if (fixKind2 == ProjectFixKind.CompilerError)
                    {
                        fixKind = fixKind2;
                        break;
                    }
                }

                if (iterationCount == Options.MaxIterations)
                    break;

                previousPreviousDiagnostics = previousDiagnostics;
                previousDiagnostics = diagnostics;
                iterationCount++;
            }

            return new ProjectFixResult(diagnosticIds.ToImmutableArray(), analyzers, fixers, fixKind);
        }

        private async Task<ProjectFixKind> FixDiagnosticsAsync(
            string diagnosticId,
            Project project,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            ImmutableArray<CodeFixProvider> fixers,
            CancellationToken cancellationToken)
        {
            ImmutableArray<Diagnostic> previousDiagnostics = ImmutableArray<Diagnostic>.Empty;

            while (true)
            {
                Compilation compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

                ImmutableArray<Diagnostic> compilerDiagnostics = compilation.GetDiagnostics(cancellationToken);

                if (!VerifyCompilerDiagnostics(compilerDiagnostics))
                    return ProjectFixKind.CompilerError;

                ImmutableArray<Diagnostic> diagnostics = default;

                if (analyzers.IsDefault)
                {
                    diagnostics = compilerDiagnostics;
                }
                else
                {
                    var compilationWithAnalyzers = new CompilationWithAnalyzers(compilation, analyzers, _defaultCompilationWithAnalyzersOptions);

                    diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                }

                diagnostics = diagnostics
                    .Where(f => f.Id == diagnosticId && f.Severity >= Options.MinimalSeverity)
                    .ToImmutableArray();

                int length = diagnostics.Length;

                if (length == 0)
                    return ProjectFixKind.Success;

                if (length == previousDiagnostics.Length
                    && !diagnostics.Except(previousDiagnostics, DiagnosticDeepEqualityComparer.Instance).Any())
                {
                    break;
                }

                previousDiagnostics = diagnostics;

                if (Options.BatchSize > 0
                    && length > Options.BatchSize)
                {
                    diagnostics = ImmutableArray.CreateRange(diagnostics, 0, Options.BatchSize, f => f);
                }

                await FixDiagnosticsAsync(diagnosticId, project, diagnostics, fixers, cancellationToken).ConfigureAwait(false);

                if (Options.BatchSize <= 0
                    || length <= Options.BatchSize)
                {
                    break;
                }

                project = CurrentSolution.GetProject(project.Id);
            }

            return ProjectFixKind.Success;
        }

        private async Task FixDiagnosticsAsync(
            string diagnosticId,
            Project project,
            ImmutableArray<Diagnostic> diagnostics,
            ImmutableArray<CodeFixProvider> fixers,
            CancellationToken cancellationToken)
        {
            WriteLine($"  Fix {diagnostics.Length,4} {diagnosticId,10} '{diagnostics[0].Descriptor.Title}'", diagnostics[0].Severity.GetColor(), Verbosity.Normal);

            WriteDiagnostics(diagnostics, baseDirectoryPath: Path.GetDirectoryName(project.FilePath), formatProvider: FormatProvider, indentation: "    ", verbosity: Verbosity.Detailed);

            CodeFixProvider fixer = null;
            CodeAction codeAction = null;

            for (int i = 0; i < fixers.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                CodeAction codeAction2 = await GetFixAsync(
                    diagnosticId,
                    project,
                    diagnostics,
                    fixers[i],
                    cancellationToken).ConfigureAwait(false);

                if (codeAction2 != null)
                {
                    if (codeAction == null)
                    {
                        if (Options.DiagnosticFixerMap.IsEmpty
                            || !Options.DiagnosticFixerMap.TryGetValue(diagnosticId, out string fullTypeName)
                            || string.Equals(fixers[i].GetType().FullName, fullTypeName, StringComparison.Ordinal))
                        {
                            codeAction = codeAction2;
                            fixer = fixers[i];
                        }
                    }
                    else if (Options.DiagnosticFixerMap.IsEmpty
                        || !Options.DiagnosticFixerMap.ContainsKey(diagnosticId))
                    {
                        WriteLine($"  Diagnostic '{diagnosticId}' is fixable with multiple fixers", ConsoleColor.Yellow, Verbosity.Diagnostic);
                        WriteLine($"    Fixer 1: '{fixer.GetType().FullName}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
                        WriteLine($"    Fixer 2: '{fixers[i].GetType().FullName}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
                        return;
                    }
                }
            }

            if (codeAction != null)
            {
                ImmutableArray<CodeActionOperation> operations = await codeAction.GetOperationsAsync(cancellationToken).ConfigureAwait(false);

                if (operations.Length == 1)
                {
                    operations[0].Apply(Workspace, cancellationToken);
                }
                else if (operations.Length > 1)
                {
                    WriteLine($@"Code action has multiple operations
  Title: {codeAction.Title}
  Equivalence key: {codeAction.EquivalenceKey}", ConsoleColor.Yellow, Verbosity.Diagnostic);
                }
            }
        }

        private async Task<CodeAction> GetFixAsync(
            string diagnosticId,
            Project project,
            ImmutableArray<Diagnostic> diagnostics,
            CodeFixProvider fixer,
            CancellationToken cancellationToken)
        {
            FixAllProvider fixAll = fixer.GetFixAllProvider();

            if (!fixAll.GetSupportedFixAllDiagnosticIds(fixer).Any(f => f == diagnosticId))
                return null;

            if (!fixAll.GetSupportedFixAllScopes().Any(f => f == FixAllScope.Project))
                return null;

            HashSet<(string fullName, string diagnosticId, string equivalenceKey1, string equivalenceKey2)> multipleActionsInfos = null;

            foreach (Diagnostic diagnostic in diagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!diagnostic.Location.IsInSource)
                    continue;

                Document document = project.GetDocument(diagnostic.Location.SourceTree);

                Debug.Assert(document != null, "");

                if (document == null)
                    continue;

                CodeAction action = null;

                var context = new CodeFixContext(
                    document,
                    diagnostic,
                    (a, _) =>
                    {
                        if (action == null)
                        {
                            if (Options.DiagnosticFixMap.IsEmpty
                                || !Options.DiagnosticFixMap.TryGetValue(diagnostic.Id, out string equivalenceKey)
                                || string.Equals(a.EquivalenceKey, equivalenceKey, StringComparison.Ordinal))
                            {
                                action = a;
                            }
                        }
                        else if (!string.Equals(a.EquivalenceKey, action.EquivalenceKey, StringComparison.Ordinal)
                            && (Options.DiagnosticFixMap.IsEmpty || !Options.DiagnosticFixMap.ContainsKey(diagnostic.Id)))
                        {
                            string fullName = fixer.GetType().FullName;

                            (string, string, string, string) multipleActionsInfo = (fullName, diagnosticId, action.EquivalenceKey, a.EquivalenceKey);

                            if (multipleActionsInfos == null)
                                multipleActionsInfos = new HashSet<(string fullName, string diagnosticId, string equivalenceKey1, string equivalenceKey2)>();

                            if (multipleActionsInfos.Add(multipleActionsInfo))
                            {
                                WriteLine($"  Fixer '{fullName}' registered multiple actions to fix diagnostic '{diagnosticId}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
                                WriteLine($"    Equivalence Key 1: '{action.EquivalenceKey}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
                                WriteLine($"    Equivalence Key 2: '{a.EquivalenceKey}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
                            }

                            action = null;
                        }
                    },
                    cancellationToken);

                await fixer.RegisterCodeFixesAsync(context).ConfigureAwait(false);

                if (action == null)
                    continue;

                var fixAllContext = new FixAllContext(
                    document,
                    fixer,
                    FixAllScope.Project,
                    action.EquivalenceKey,
                    new string[] { diagnosticId },
                    new FixAllDiagnosticProvider(diagnostics),
                    cancellationToken);

                CodeAction fixAllAction = await fixAll.GetFixAsync(fixAllContext).ConfigureAwait(false);

                if (fixAllAction == null
                    && diagnosticId.StartsWith("RCS"))
                {
                    WriteLine($"Fixer '{fixer.GetType().FullName}' registered no action for diagnostics:", ConsoleColor.DarkGray, Verbosity.Diagnostic);
                    WriteDiagnostics(diagnostics, baseDirectoryPath: Path.GetDirectoryName(project.FilePath), formatProvider: FormatProvider, indentation: "  ", maxCount: 10, verbosity: Verbosity.Diagnostic);
                }

                return fixAllAction;
            }

            return null;
        }

        private bool VerifyCompilerDiagnostics(ImmutableArray<Diagnostic> diagnostics)
        {
            using (IEnumerator<Diagnostic> en = diagnostics
                .Where(f => f.Severity == DiagnosticSeverity.Error
                    && !Options.IgnoredCompilerDiagnosticIds.Contains(f.Id))
                .GetEnumerator())
            {
                if (en.MoveNext())
                {
                    const int maxCount = 10;

                    int count = 0;

                    do
                    {
                        count++;

                        if (count <= maxCount)
                        {
                            //TODO: format diagnostic
                            WriteDiagnostic(en.Current, verbosity: Verbosity.Normal);
                        }
                        else
                        {
                            break;
                        }
                    }
                    while (en.MoveNext());

                    count = 0;

                    bool plus = false;

                    while (en.MoveNext())
                    {
                        count++;

                        if (count == 1000)
                        {
                            plus = true;
                            break;
                        }
                    }

                    if (count > maxCount)
                    {
                        WriteLine($"and {count}{((plus) ? "+" : "")} more diagnostics", verbosity: Verbosity.Normal);
                    }

                    if (!Options.IgnoreCompilerErrors)
                    {
#if DEBUG
                        Console.Write("Stop (Y/N)? ");

                        if (char.ToUpperInvariant((char)Console.Read()) == 'Y')
                            return false;
#else
                        return false;
#endif
                    }
                }
            }

            return true;
        }

        private async Task AddFileBannerAsync(
            Project project,
            ImmutableArray<string> banner,
            CancellationToken cancellationToken)
        {
            bool hasChanges = false;

            string solutionDirectory = Path.GetDirectoryName(project.Solution.FilePath);

            foreach (DocumentId documentId in project.DocumentIds)
            {
                Document document = project.GetDocument(documentId);

                if (GeneratedCodeUtility.IsGeneratedCodeFile(document.FilePath))
                    continue;

                SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

                SyntaxFactsService syntaxFacts = SyntaxFactsService.GetService(project.Language);

                if (syntaxFacts.BeginsWithAutoGeneratedComment(root))
                    continue;

                if (syntaxFacts.BeginsWithBanner(root, banner))
                    continue;

                SyntaxTriviaList leading = root.GetLeadingTrivia();

                SyntaxTriviaList newLeading = leading.InsertRange(0, banner.SelectMany(f => syntaxFacts.ParseLeadingTrivia(syntaxFacts.SingleLineCommentStart + f + Environment.NewLine)));

                if (!syntaxFacts.IsEndOfLineTrivia(leading.LastOrDefault()))
                    newLeading = newLeading.AddRange(syntaxFacts.ParseLeadingTrivia(Environment.NewLine));

                SyntaxNode newRoot = root.WithLeadingTrivia(newLeading);

                Document newDocument = document.WithSyntaxRoot(newRoot);

                WriteLine($"  Add banner to '{PathUtilities.TrimStart(document.FilePath, solutionDirectory)}'", ConsoleColor.DarkGray, Verbosity.Detailed);

                project = newDocument.Project;

                hasChanges = true;
            }

            if (hasChanges
                && !Workspace.TryApplyChanges(project.Solution))
            {
                Debug.Fail($"Cannot apply changes to solution '{project.Solution.FilePath}'");
                WriteLine($"Cannot apply changes to solution '{project.Solution.FilePath}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
            }
        }

        private async Task FormatProjectAsync(Project project, CancellationToken cancellationToken)
        {
            project = CurrentSolution.GetProject(project.Id);

            WriteLine($"  Format  '{project.Name}'", Verbosity.Normal);

            Project newProject = await CodeFormatter.FormatProjectAsync(project, cancellationToken).ConfigureAwait(false);

            string solutionDirectory = Path.GetDirectoryName(project.Solution.FilePath);

            bool hasChanges = false;

            foreach (DocumentId documentId in newProject
                .GetChanges(project)
                .GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                Document newDocument = newProject.GetDocument(documentId);

                // https://github.com/dotnet/roslyn/issues/30674
                if (project.Language != LanguageNames.VisualBasic
                    || (await newDocument.GetTextChangesAsync(project.GetDocument(documentId)).ConfigureAwait(false)).Any())
                {
                    hasChanges = true;

                    WriteLine($"  Format '{PathUtilities.TrimStart(newDocument.FilePath, solutionDirectory)}'", ConsoleColor.DarkGray, Verbosity.Detailed);
#if DEBUG
                    await Utilities.VerifySyntaxEquivalenceAsync(project.GetDocument(newDocument.Id), newDocument, cancellationToken).ConfigureAwait(false);
#endif
                }
            }

            if (hasChanges
                && !Workspace.TryApplyChanges(newProject.Solution))
            {
                Debug.Fail($"Cannot apply changes to solution '{newProject.Solution.FilePath}'");
                WriteLine($"Cannot apply changes to solution '{newProject.Solution.FilePath}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
            }
        }
    }
}