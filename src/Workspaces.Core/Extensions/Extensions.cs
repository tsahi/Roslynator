﻿// Copyright (c) Josef Pihrt. All rights reserved. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.Telemetry;
using Microsoft.CodeAnalysis.Text;
using Roslynator.CodeMetrics;

namespace Roslynator
{
    internal static class Extensions
    {
        public static MemberDeclarationKind GetMemberDeclarationKind(this ISymbol symbol)
        {
            switch (symbol.Kind)
            {
                case SymbolKind.Event:
                    {
                        return MemberDeclarationKind.Event;
                    }
                case SymbolKind.Field:
                    {
                        var fieldSymbol = (IFieldSymbol)symbol;

                        return (fieldSymbol.IsConst)
                            ? MemberDeclarationKind.Const
                            : MemberDeclarationKind.Field;
                    }
                case SymbolKind.Method:
                    {
                        var methodSymbol = (IMethodSymbol)symbol;

                        switch (methodSymbol.MethodKind)
                        {
                            case MethodKind.Ordinary:
                                return MemberDeclarationKind.OrdinaryMethod;
                            case MethodKind.Constructor:
                                return MemberDeclarationKind.Constructor;
                            case MethodKind.Destructor:
                                return MemberDeclarationKind.Destructor;
                            case MethodKind.StaticConstructor:
                                return MemberDeclarationKind.StaticConstructor;
                            case MethodKind.Conversion:
                                return MemberDeclarationKind.ConversionOperator;
                            case MethodKind.UserDefinedOperator:
                                return MemberDeclarationKind.Operator;
                        }

                        break;
                    }
                case SymbolKind.Property:
                    {
                        var propertySymbol = (IPropertySymbol)symbol;

                        return (propertySymbol.IsIndexer)
                            ? MemberDeclarationKind.Indexer
                            : MemberDeclarationKind.Property;
                    }
            }

            Debug.Fail(symbol.ToDisplayString(SymbolDisplayFormats.Test));

            return MemberDeclarationKind.None;
        }

        public static SymbolSpecialKind GetSpecialKind(this ISymbol symbol)
        {
            switch (symbol.Kind)
            {
                case SymbolKind.NamedType:
                    {
                        var namedType = (INamedTypeSymbol)symbol;

                        switch (namedType.TypeKind)
                        {
                            case TypeKind.Class:
                                return SymbolSpecialKind.Class;
                            case TypeKind.Delegate:
                                return SymbolSpecialKind.Delegate;
                            case TypeKind.Enum:
                                return SymbolSpecialKind.Enum;
                            case TypeKind.Interface:
                                return SymbolSpecialKind.Interface;
                            case TypeKind.Struct:
                                return SymbolSpecialKind.Struct;
                        }

                        Debug.Fail(namedType.TypeKind.ToString());
                        return SymbolSpecialKind.None;
                    }
                case SymbolKind.Event:
                    {
                        return SymbolSpecialKind.Event;
                    }
                case SymbolKind.Field:
                    {
                        return (((IFieldSymbol)symbol).IsConst)
                            ? SymbolSpecialKind.Const
                            : SymbolSpecialKind.Field;
                    }
                case SymbolKind.Method:
                    {
                        return SymbolSpecialKind.Method;
                    }
                case SymbolKind.Property:
                    {
                        return (((IPropertySymbol)symbol).IsIndexer)
                            ? SymbolSpecialKind.Indexer
                            : SymbolSpecialKind.Property;
                    }
            }

            Debug.Fail(symbol.Kind.ToString());
            return SymbolSpecialKind.None;
        }

        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        {
            if (dictionary.TryGetValue(key, out TValue value))
                return value;

            return default;
        }

        public static bool StartsWith(this string s, string value1, string value2, StringComparison comparisonType)
        {
            return s.StartsWith(value1, comparisonType) || s.StartsWith(value2, comparisonType);
        }

        public static bool HasFixAllProvider(this CodeFixProvider codeFixProvider, FixAllScope fixAllScope)
        {
            return codeFixProvider.GetFixAllProvider()?.GetSupportedFixAllScopes().Contains(fixAllScope) == true;
        }

        public static Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsAsync(
            this Compilation compilation,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            CompilationWithAnalyzersOptions analysisOptions,
            CancellationToken cancellationToken = default)
        {
            var compilationWithAnalyzers = new CompilationWithAnalyzers(compilation, analyzers, analysisOptions);

            return compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);
        }

        public static void Add(this AnalyzerTelemetryInfo telemetryInfo, AnalyzerTelemetryInfo telemetryInfoToAdd)
        {
            telemetryInfo.CodeBlockActionsCount += telemetryInfoToAdd.CodeBlockActionsCount;
            telemetryInfo.CodeBlockEndActionsCount += telemetryInfoToAdd.CodeBlockEndActionsCount;
            telemetryInfo.CodeBlockStartActionsCount += telemetryInfoToAdd.CodeBlockStartActionsCount;
            telemetryInfo.CompilationActionsCount += telemetryInfoToAdd.CompilationActionsCount;
            telemetryInfo.CompilationEndActionsCount += telemetryInfoToAdd.CompilationEndActionsCount;
            telemetryInfo.CompilationStartActionsCount += telemetryInfoToAdd.CompilationStartActionsCount;
            telemetryInfo.ExecutionTime += telemetryInfoToAdd.ExecutionTime;
            telemetryInfo.OperationActionsCount += telemetryInfoToAdd.OperationActionsCount;
            telemetryInfo.OperationBlockActionsCount += telemetryInfoToAdd.OperationBlockActionsCount;
            telemetryInfo.OperationBlockEndActionsCount += telemetryInfoToAdd.OperationBlockEndActionsCount;
            telemetryInfo.OperationBlockStartActionsCount += telemetryInfoToAdd.OperationBlockStartActionsCount;
            telemetryInfo.SemanticModelActionsCount += telemetryInfoToAdd.SemanticModelActionsCount;
            telemetryInfo.SymbolActionsCount += telemetryInfoToAdd.SymbolActionsCount;
            telemetryInfo.SyntaxNodeActionsCount += telemetryInfoToAdd.SyntaxNodeActionsCount;
            telemetryInfo.SyntaxTreeActionsCount += telemetryInfoToAdd.SyntaxTreeActionsCount;
        }

        public static ConsoleColor GetColor(this DiagnosticSeverity diagnosticSeverity)
        {
            switch (diagnosticSeverity)
            {
                case DiagnosticSeverity.Hidden:
                    return ConsoleColor.DarkGray;
                case DiagnosticSeverity.Info:
                    return ConsoleColor.Cyan;
                case DiagnosticSeverity.Warning:
                    return ConsoleColor.Yellow;
                case DiagnosticSeverity.Error:
                    return ConsoleColor.Red;
                default:
                    throw new InvalidOperationException($"Unknown diagnostic severity '{diagnosticSeverity}'.");
            }
        }

        public static async Task<CodeMetricsInfo> CountLinesAsync(
            this ICodeMetricsService service,
            Project project,
            LinesOfCodeKind kind,
            CodeMetricsOptions options = null,
            CancellationToken cancellationToken = default)
        {
            CodeMetricsInfo codeMetrics = default;

            foreach (Document document in project.Documents)
            {
                if (!document.SupportsSyntaxTree)
                    continue;

                CodeMetricsInfo documentMetrics = await service.CountLinesAsync(document, kind, options, cancellationToken).ConfigureAwait(false);

                codeMetrics = codeMetrics.Add(documentMetrics);
            }

            return codeMetrics;
        }

        public static async Task<CodeMetricsInfo> CountLinesAsync(
            this ICodeMetricsService service,
            Document document,
            LinesOfCodeKind kind,
            CodeMetricsOptions options = null,
            CancellationToken cancellationToken = default)
        {
            SyntaxTree tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);

            if (tree == null)
                return default;

            if (!options.IncludeGeneratedCode
                && GeneratedCodeUtility.IsGeneratedCode(tree, service.SyntaxFacts.IsComment, cancellationToken))
            {
                return default;
            }

            SyntaxNode root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

            SourceText sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            switch (kind)
            {
                case LinesOfCodeKind.Physical:
                    return service.CountPhysicalLines(root, sourceText, options, cancellationToken);
                case LinesOfCodeKind.Logical:
                    return service.CountLogicalLines(root, sourceText, options, cancellationToken);
                default:
                    throw new InvalidOperationException();
            }
        }

        public static OperationCanceledException GetOperationCanceledException(this AggregateException aggregateException)
        {
            OperationCanceledException operationCanceledException = null;

            foreach (Exception ex in aggregateException.InnerExceptions)
            {
                if (ex is OperationCanceledException operationCanceledException2)
                {
                    if (operationCanceledException == null)
                        operationCanceledException = operationCanceledException2;
                }
                else if (ex is AggregateException aggregateException2)
                {
                    foreach (Exception ex2 in aggregateException2.InnerExceptions)
                    {
                        if (ex2 is OperationCanceledException operationCanceledException3)
                        {
                            if (operationCanceledException == null)
                                operationCanceledException = operationCanceledException3;
                        }
                        else
                        {
                            return null;
                        }
                    }

                    return operationCanceledException;
                }
                else
                {
                    return null;
                }
            }

            return null;
        }

        public static ConsoleColor GetColor(this WorkspaceDiagnosticKind kind)
        {
            switch (kind)
            {
                case WorkspaceDiagnosticKind.Failure:
                    return ConsoleColor.Red;
                case WorkspaceDiagnosticKind.Warning:
                    return ConsoleColor.Yellow;
                default:
                    throw new InvalidOperationException($"Unknown value '{kind}'.");
            }
        }
    }
}