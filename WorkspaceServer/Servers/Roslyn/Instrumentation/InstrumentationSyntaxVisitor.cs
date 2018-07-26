﻿using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace WorkspaceServer.Servers.Roslyn.Instrumentation
{
    public class InstrumentationSyntaxVisitor : CSharpSyntaxRewriter
    {
        public AugmentationMap Augmentations { get; }
        public VariableLocationMap VariableLocations { get; }

        private readonly SemanticModel _semanticModel;

        private readonly IEnumerable<TextSpan> _replacementRegions;

        private readonly Document _document;

        public InstrumentationSyntaxVisitor(Document document, IEnumerable<TextSpan> replacementRegions = null)
        {
            _semanticModel = document.GetSemanticModelAsync().Result;
            _replacementRegions = replacementRegions;
            _document = document;
            VariableLocations = new VariableLocationMap();
            Augmentations = new AugmentationMap();

            Visit(_semanticModel.SyntaxTree.GetRoot());
        }

        private void RecordVariableLocations(IEnumerable<CSharpSyntaxNode> statements)
        {
            IEnumerable<ISymbol> distinctLocalVariables = GetDistinctLocalVariables(statements);

            foreach (var variable in distinctLocalVariables)
            {
                var variableReferences = SymbolFinder.FindReferencesAsync(variable, _document.Project.Solution).Result;

                var variableUsageLocations = variableReferences
                  .SelectMany(reference => reference.Locations)
                  .Select(referenceLocation => referenceLocation.Location.GetLineSpan().Span)
                  .Select(span => VariableLocation.FromSpan(variable, span));

                var declaringSpan = GetDeclaringSpan(variable);
                var declaringLocation = VariableLocation.FromSpan(variable, declaringSpan);

                var allLocations = variableUsageLocations.Concat(new[] { declaringLocation });

                VariableLocations.AddLocations(variable, allLocations);
            }

        }

        private LinePositionSpan GetDeclaringSpan(ISymbol variable)
        {
            var declaringReference = variable.DeclaringSyntaxReferences.First();
            var declSpan = declaringReference.Span;
            var tree = declaringReference.SyntaxTree;
            var betterSpan = tree.GetLineSpan(declSpan);
            return betterSpan.Span;
        }

        private IEnumerable<ISymbol> GetDistinctLocalVariables(IEnumerable<CSharpSyntaxNode> statements)
        {
            return statements.SelectMany(statement => _semanticModel.LookupSymbols(statement.GetLocation().SourceSpan.Start))
                            .Where(symbol => IsVariable(symbol))
                            .Distinct();
        }

        private bool IsVariable(ISymbol symbol) => symbol.Kind == SymbolKind.Local 
            || symbol.Kind == SymbolKind.Field
            || symbol.Kind == SymbolKind.Parameter;

        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            FilterAndInstrument(node.Statements.ToArray());

            // recurse 
            return base.VisitBlock(node);
        }

        public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            FilterAndInstrument(node.Body);
            return base.VisitSimpleLambdaExpression(node);
        }

        public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            FilterAndInstrument(node.Body);
            return base.VisitParenthesizedLambdaExpression(node);
        }

        private void FilterAndInstrument(params CSharpSyntaxNode[] nodes)
        {
            var filteredStatements = FilterStatementsByRegions(nodes).ToList();

            if (filteredStatements.Count > 0)
            {
                RecordAugmentations(filteredStatements);
                RecordVariableLocations(filteredStatements);
            }
        }
        private void RecordAugmentations(IEnumerable<CSharpSyntaxNode> statements)
        {
            // get the parent assigned variables, and static status
            // it should be the same for all statements, as we're operating at the block level here
            var parentAssigned = GetAssignedVariablesAtParent(statements.First());
            var isInStaticMethod = _semanticModel.GetEnclosingSymbol(statements.First().SpanStart).IsStatic;
            var dataFlowIn = _semanticModel.AnalyzeDataFlow(statements.First()).DataFlowsIn;
            var dataflow = _semanticModel.AnalyzeDataFlow(statements.First());

            var filteredStatements = FilterStatementsByRegions(statements).ToList();

            for (int i = 0; i < statements.Count(); i++)
            {
                var currentDataflow = _semanticModel.AnalyzeDataFlow(filteredStatements.ElementAt(i));
                var statement = filteredStatements[i];
                var prevAssigned = i > 0 ? _semanticModel.AnalyzeDataFlow(filteredStatements[0], filteredStatements[i - 1]).AlwaysAssigned : Enumerable.Empty<ISymbol>();
                var assigned = prevAssigned.Union(parentAssigned).Union(dataFlowIn);
                
                // if the node has children, figure out what variables are valid inside, so the child nodes will be aware of them later on
                var validForChildren = Enumerable.Empty<ISymbol>();
                if (statement.ChildNodes().Any(n => n is BlockSyntax))
                {
                    var dataFlow = _semanticModel.AnalyzeDataFlow(statement);
                    validForChildren = dataFlow.AlwaysAssigned.Union(dataFlow.DataFlowsIn);
                }
           
                var symbols = _semanticModel.LookupSymbols(statement.FullSpan.Start);
                var locals = symbols.Where(s => s.Kind == SymbolKind.Local && assigned.Contains(s));
                var fields = symbols.Where(s => s.Kind == SymbolKind.Field && (s.IsStatic || !isInStaticMethod));
                var param = symbols.Where(s => s.Kind == SymbolKind.Parameter);
                
                var augmentation = new Augmentation(statement, locals, fields, param, validForChildren);

                this.Augmentations.Data[statement] = (augmentation);
            }
        }

        private IEnumerable<CSharpSyntaxNode> FilterStatementsByRegions(IEnumerable<CSharpSyntaxNode> statements)
        {
            return _replacementRegions != null ? statements.Where(s => _replacementRegions.Any(r => r.OverlapsWith(s.Span))) : statements;
        }

        private IEnumerable<ISymbol> GetAssignedVariablesAtParent(CSharpSyntaxNode statement)
        {
            if (statement.Parent is BlockSyntax block && Augmentations.Data.ContainsKey(block.Parent))
            {
                var parentAugmentation = Augmentations.Data[block.Parent];
                if (parentAugmentation != null)
                {
                    return parentAugmentation.Locals.Union(parentAugmentation.InternalLocals);
                }
            }

            return Enumerable.Empty<ISymbol>();
        }
    }
}