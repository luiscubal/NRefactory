//
// ParameterCouldBeDeclaredWithBaseTypeIssue.cs
//
// Author:
//       Simon Lindgren <simon.n.lindgren@gmail.com>
//
// Copyright (c) 2012 Simon Lindgren
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System.Collections.Generic;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.Semantics;
using System.Linq;
using System;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using System.Diagnostics;
using ICSharpCode.NRefactory.Refactoring;

namespace ICSharpCode.NRefactory.CSharp.Refactoring
{
	[IssueDescription("Parameter can be declared with base type",
		Description = "Finds parameters that can be demoted to a base class.",
		Category = IssueCategories.PracticesAndImprovements,
		Severity = Severity.Hint,
		SuppressMessageCategory="Microsoft.Design",
		SuppressMessageCheckId="CA1011:ConsiderPassingBaseTypesAsParameters"
	)]
	public class ParameterCanBeDeclaredWithBaseTypeIssue : GatherVisitorCodeIssueProvider
	{
		bool tryResolve;

		public ParameterCanBeDeclaredWithBaseTypeIssue() : this (true)
		{
		}

		public ParameterCanBeDeclaredWithBaseTypeIssue(bool tryResolve)
		{
			this.tryResolve = tryResolve;
		}

		#region ICodeIssueProvider implementation
		protected override IGatherVisitor CreateVisitor(BaseRefactoringContext context)
		{
			return new GatherVisitor(context, tryResolve);
		}
		#endregion

		class GatherVisitor : GatherVisitorBase<ParameterCanBeDeclaredWithBaseTypeIssue>
		{
			bool tryResolve;
			
			public GatherVisitor(BaseRefactoringContext context, bool tryResolve) : base (context)
			{
				this.tryResolve = tryResolve;
			}

			public override void VisitMethodDeclaration(MethodDeclaration methodDeclaration)
			{
				methodDeclaration.Attributes.AcceptVisitor(this);
				if (HasEntryPointSignature(methodDeclaration) || methodDeclaration.HasModifier(Modifiers.Public) || methodDeclaration.HasModifier(Modifiers.Protected))
					return;
				var eligibleParameters = methodDeclaration.Parameters
					.Where(p => p.ParameterModifier != ParameterModifier.Out && p.ParameterModifier != ParameterModifier.Ref)
					.ToList();
				if (eligibleParameters.Count == 0)
					return;
				var declarationResolveResult = ctx.Resolve(methodDeclaration) as MemberResolveResult;
				if (declarationResolveResult == null)
					return;
				var member = declarationResolveResult.Member;
				if (member.IsOverride || member.IsOverridable || member.ImplementedInterfaceMembers.Any())
					return;

				var collector = new TypeCriteriaCollector(ctx);
				methodDeclaration.AcceptVisitor(collector);

				foreach (var parameter in eligibleParameters) {
					ProcessParameter(parameter, methodDeclaration.Body, collector);
				}
			}

			bool HasEntryPointSignature(MethodDeclaration methodDeclaration)
			{
				if (!methodDeclaration.Modifiers.HasFlag(Modifiers.Static))
					return false;
				var returnType = ctx.Resolve(methodDeclaration.ReturnType).Type;
				if (!returnType.IsKnownType(KnownTypeCode.Int32) && !returnType.IsKnownType(KnownTypeCode.Void))
					return false;
				var parameterCount = methodDeclaration.Parameters.Count;
				if (parameterCount == 0)
					return true;
				if (parameterCount != 1)
					return false;
				var parameterType = ctx.Resolve(methodDeclaration.Parameters.First()).Type as ArrayType;
				if (parameterType == null || !parameterType.ElementType.IsKnownType(KnownTypeCode.String))
					return false;
				return true;
			}

			bool FilterOut(IType current, IType newType)
			{
				// Filter out some strange framework types like _Exception
				return newType.Namespace.StartsWith("System.", StringComparison.Ordinal) && 
					   newType.Name.StartsWith("_", StringComparison.Ordinal) ? true : false;
			}

			void ProcessParameter(ParameterDeclaration parameter, AstNode rootResolutionNode, TypeCriteriaCollector collector)
			{
				var localResolveResult = ctx.Resolve(parameter) as LocalResolveResult;
				if (localResolveResult == null)
					return;
				var variable = localResolveResult.Variable;
				var typeKind = variable.Type.Kind;
				if (!(typeKind == TypeKind.Class ||
					  typeKind == TypeKind.Struct ||
					  typeKind == TypeKind.Interface ||
					  typeKind == TypeKind.Array) ||
				    parameter.Type is PrimitiveType ||
					!collector.UsedVariables.Contains(variable)) {
					return;
				}

				var candidateTypes = localResolveResult.Type.GetAllBaseTypes().ToList();
				TypesChecked += candidateTypes.Count;
				var criterion = collector.GetCriterion(variable);

				var possibleTypes = 
					(from type in candidateTypes
					 where !type.Equals(localResolveResult.Type) && criterion.SatisfiedBy(type)
					 select type).ToList();

				TypeResolveCount += possibleTypes.Count;
				var validTypes = 
					(from type in possibleTypes
					 where (!tryResolve || TypeChangeResolvesCorrectly(ctx, parameter, rootResolutionNode, type)) && !FilterOut (variable.Type, type)
					 select type).ToList();
				if (validTypes.Any()) {
					AddIssue(new CodeIssue(parameter, ctx.TranslateString("Parameter can be declared with base type"), GetActions(parameter, validTypes)) {
						IssueMarker = IssueMarker.DottedLine
					});
					MembersWithIssues++;
				}
			}

			internal int TypeResolveCount = 0;
			internal int TypesChecked = 0;
			internal int MembersWithIssues = 0;
			internal int MethodResolveCount = 0;

			IEnumerable<CodeAction> GetActions(ParameterDeclaration parameter, IEnumerable<IType> possibleTypes)
			{
				var csResolver = ctx.Resolver.GetResolverStateBefore(parameter);
				var astBuilder = new TypeSystemAstBuilder(csResolver);
				foreach (var type in possibleTypes) {
					var localType = type;
					var message = String.Format(ctx.TranslateString("Demote parameter to '{0}'"), type.FullName);
					yield return new CodeAction(message, script => {
						script.Replace(parameter.Type, astBuilder.ConvertType(localType));
					}, parameter.NameToken);
				}
			}
		}

	    public static bool TypeChangeResolvesCorrectly(BaseRefactoringContext ctx, ParameterDeclaration parameter, AstNode rootNode, IType type)
	    {
	        var resolver = ctx.GetResolverStateBefore(rootNode);
	        resolver = resolver.AddVariable(new DefaultParameter(type, parameter.Name));
	        var astResolver = new CSharpAstResolver(resolver, rootNode, ctx.UnresolvedFile);
	        var validator = new TypeChangeValidationNavigator();
	        astResolver.ApplyNavigator(validator, ctx.CancellationToken);
	        return !validator.FoundErrors;
	    }

	    class TypeChangeValidationNavigator : IResolveVisitorNavigator
		{
			public bool FoundErrors { get; private set; }

			#region IResolveVisitorNavigator implementation
			public ResolveVisitorNavigationMode Scan(AstNode node)
			{
				if (FoundErrors)
					return ResolveVisitorNavigationMode.Skip;
				return ResolveVisitorNavigationMode.Resolve;
			}

			public void Resolved(AstNode node, ResolveResult result)
			{
//				bool errors = result.IsError;
				FoundErrors |= result.IsError;
			}

			public void ProcessConversion(Expression expression, ResolveResult result, Conversion conversion, IType targetType)
			{
				// no-op
			}
			#endregion
			
		}
	}
}
