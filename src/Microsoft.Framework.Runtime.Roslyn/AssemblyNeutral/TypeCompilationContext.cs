// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.Emit;

namespace Microsoft.Framework.Runtime.Roslyn
{
    public class TypeCompilationContext
    {
        public TypeCompilationContext(AssemblyNeutralWorker worker, ITypeSymbol typeSymbol)
        {
            Worker = worker;
            TypeSymbol = typeSymbol;
            Requires = new Dictionary<TypeCompilationContext, AssemblyNeutralWorker.OrderingState.StrengthKind>();
        }

        public AssemblyNeutralWorker Worker { get; private set; }
        public ITypeSymbol TypeSymbol { get; private set; }

        public IDictionary<TypeCompilationContext, AssemblyNeutralWorker.OrderingState.StrengthKind> Requires { get; set; }

        public string AssemblyName
        {
            get
            {
                return TypeSymbol.ContainingNamespace + "." + TypeSymbol.MetadataName;
            }
        }

        public CSharpCompilation Compilation { get; private set; }

        public byte[] OutputBytes { get; private set; }
        public MetadataReference Reference { get; private set; }
        public EmitResult EmitResult { get; private set; }

        public CSharpCompilation ShallowCompilation { get; private set; }
        public byte[] ShallowBytes { get; private set; }
        public MetadataReference ShallowReference { get; private set; }
        public EmitResult ShallowEmitResult { get; private set; }


        public void SymbolUsage(Action<ISymbol> shallowUsage, Action<ISymbol> deepUsage)
        {
            DoTypeSymbol(deepUsage, TypeSymbol.BaseType);

            foreach (var symbol in TypeSymbol.AllInterfaces)
            {
                DoTypeSymbol(deepUsage, symbol);
            }

            AttributeDataSymbolUsage(TypeSymbol.GetAttributes(), deepUsage);

            foreach (var member in TypeSymbol.GetMembers())
            {
                AttributeDataSymbolUsage(member.GetAttributes(), deepUsage);

                var propertyMember = member as IPropertySymbol;
                var fieldMember = member as IFieldSymbol;
                var methodMember = member as IMethodSymbol;

                if (propertyMember != null)
                {
                    DoTypeSymbol(shallowUsage, propertyMember.Type);
                }

                if (fieldMember != null)
                {
                    DoTypeSymbol(shallowUsage, fieldMember.Type);
                }

                if (methodMember != null)
                {
                    DoTypeSymbol(shallowUsage, methodMember.ReturnType);
                    AttributeDataSymbolUsage(methodMember.GetReturnTypeAttributes(), deepUsage);
                    foreach (var parameter in methodMember.Parameters)
                    {
                        DoTypeSymbol(shallowUsage, parameter.Type);
                        AttributeDataSymbolUsage(parameter.GetAttributes(), deepUsage);
                    }
                }
            }
        }

        private void DoTypeSymbol(Action<ISymbol> usage, ITypeSymbol typeSymbol)
        {
            usage(typeSymbol);

            var namedTypeSymbol = typeSymbol as INamedTypeSymbol;
            if (namedTypeSymbol != null)
            {
                usage(namedTypeSymbol.OriginalDefinition);

                foreach (var arg in namedTypeSymbol.TypeArguments)
                {
                    if (arg is ITypeParameterSymbol)
                    {
                        // Unspecified type paramters can be skipped
                        continue;
                    }

                    // TODO: Stack guard (or just use a stack)
                    DoTypeSymbol(usage, arg);
                }
            }
        }

        private void AttributeDataSymbolUsage(IEnumerable<AttributeData> attributeDatas, Action<ISymbol> deepUsage)
        {
            foreach (var attributeData in attributeDatas)
            {
                AttributeDataSymbolUsage(attributeData, deepUsage);
            }
        }

        private void AttributeDataSymbolUsage(AttributeData attributeData, Action<ISymbol> deepUsage)
        {
            deepUsage(attributeData.AttributeClass);

            foreach (var argument in attributeData.ConstructorArguments)
            {
                DoTypeSymbol(deepUsage, argument.Type);
            }

            foreach (var argument in attributeData.NamedArguments)
            {
                DoTypeSymbol(deepUsage, argument.Value.Type);
            }
        }

        public EmitResult Generate(IDictionary<string, MetadataReference> existingReferences)
        {
            Compilation = CSharpCompilation.Create(
                assemblyName: AssemblyName,
                options: Worker.OriginalCompilation.Options,
                references: Worker.OriginalCompilation.References);

            foreach (var other in Requires.Keys)
            {
                if (other.EmitResult != null && !other.EmitResult.Success)
                {
                    // Skip this reference if it hasn't beed emitted
                    continue;
                }

                // If we're already referencing this assembly then skip it
                if (existingReferences.ContainsKey(other.AssemblyName))
                {
                    continue;
                }

                Compilation = Compilation.AddReferences(other.RealOrShallowReference());
            }

            foreach (var syntaxReference in TypeSymbol.DeclaringSyntaxReferences)
            {
                var node = syntaxReference.GetSyntax();
                var tree = syntaxReference.SyntaxTree;
                var root = tree.GetRoot();

                var nodesToRemove = GetNodesToRemove(root, node).ToArray();

                // what it looks like when removed
                var newRoot = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepDirectives);
                var newTree = SyntaxFactory.SyntaxTree(newRoot, options: tree.Options, path: tree.FilePath, encoding: Encoding.UTF8);

                // update compilation with code removed
                Compilation = Compilation.AddSyntaxTrees(newTree);
            }

            var outputStream = new MemoryStream();
            EmitResult = Compilation.Emit(outputStream);
            if (!EmitResult.Success)
            {
                return EmitResult;
            }

            OutputBytes = outputStream.ToArray();
            Reference = MetadataReference.CreateFromImage(OutputBytes);

            return EmitResult;
        }

        private MetadataReference GenerateShallowReference()
        {
            ShallowCompilation = CSharpCompilation.Create(
                assemblyName: AssemblyName,
                options: Worker.OriginalCompilation.Options,
                references: Worker.OriginalCompilation.References);

            foreach (var other in Requires)
            {
                if (other.Value == AssemblyNeutralWorker.OrderingState.StrengthKind.DeepUsage)
                {
                    ShallowCompilation = ShallowCompilation.AddReferences(other.Key.RealOrShallowReference());
                }
            }

            foreach (var syntaxReference in TypeSymbol.DeclaringSyntaxReferences)
            {
                var node = syntaxReference.GetSyntax();
                var tree = syntaxReference.SyntaxTree;
                var root = tree.GetRoot();

                var nodesToRemove = GetNodesToRemove(root, node);

                foreach (var member in TypeSymbol.GetMembers())
                {
                    foreach (var memberSyntaxReference in member.DeclaringSyntaxReferences)
                    {
                        if (memberSyntaxReference.SyntaxTree == tree)
                        {
                            nodesToRemove = nodesToRemove.Concat(new[] { memberSyntaxReference.GetSyntax() });
                        }
                    }
                }

                var newRoot = root.RemoveNodes(nodesToRemove.ToArray(), SyntaxRemoveOptions.KeepDirectives);
                var newTree = SyntaxFactory.SyntaxTree(newRoot, options: tree.Options, path: tree.FilePath, encoding: Encoding.UTF8);

                ShallowCompilation = ShallowCompilation.AddSyntaxTrees(newTree);
            }
            var shallowOutputStream = new MemoryStream();
            ShallowEmitResult = ShallowCompilation.Emit(shallowOutputStream);
            ShallowBytes = shallowOutputStream.ToArray();
            ShallowReference = MetadataReference.CreateFromImage(ShallowBytes);
            return ShallowReference;
        }

        public MetadataReference RealOrShallowReference()
        {
            return Reference ?? ShallowReference ?? GenerateShallowReference();
        }

        private IEnumerable<SyntaxNode> GetNodesToRemove(SyntaxNode root, SyntaxNode target)
        {
            for (var scan = target; scan != root; scan = scan.Parent)
            {
                var child = scan;
                var parent = child.Parent;
                foreach (var remove in parent.ChildNodes())
                {
                    if (remove == child)
                    {
                        continue;
                    }

                    if (remove.IsKind(SyntaxKind.UsingDirective))
                    {
                        continue;
                    }

                    if (parent.IsKind(SyntaxKind.NamespaceDeclaration) && (
                        (parent as NamespaceDeclarationSyntax).Name == remove))
                    {
                        continue;
                    }

                    yield return remove;
                }
            }
        }
    }
}
