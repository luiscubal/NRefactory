﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under MIT X11 license (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.TypeSystem.Implementation;

namespace ICSharpCode.NRefactory.CSharp.Resolver
{
	/// <summary>
	/// Traverses the DOM and resolves expressions.
	/// </summary>
	/// <remarks>
	/// The ResolveVisitor does two jobs at the same time: it tracks the resolve context (properties on CSharpResolver)
	/// and it resolves the expressions visited.
	/// To allow using the context tracking without having to resolve every expression in the file (e.g. when you want to resolve
	/// only a single node deep within the DOM), you can use the <see cref="IResolveVisitorNavigator"/> interface.
	/// The navigator allows you to switch the between scanning mode and resolving mode.
	/// In scanning mode, the context is tracked (local variables registered etc.), but nodes are not resolved.
	/// While scanning, the navigator will get asked about every node that the resolve visitor is about to enter.
	/// This allows the navigator whether to keep scanning, whether switch to resolving mode, or whether to completely skip the
	/// subtree rooted at that node.
	/// 
	/// In resolving mode, the context is tracked and nodes will be resolved.
	/// The resolve visitor may decide that it needs to resolve other nodes as well in order to resolve the current node.
	/// In this case, those nodes will be resolved automatically, without asking the navigator interface.
	/// For child nodes that are not essential to resolving, the resolve visitor will switch back to scanning mode (and thus will
	/// ask the navigator for further instructions).
	/// 
	/// Moreover, there is the <c>ResolveAll</c> mode - it works similar to resolving mode, but will not switch back to scanning mode.
	/// The whole subtree will be resolved without notifying the navigator.
	/// </remarks>
	public sealed class ResolveVisitor : IDomVisitor<object, ResolveResult>
	{
		static readonly ResolveResult errorResult = new ErrorResolveResult(SharedTypes.UnknownType);
		CSharpResolver resolver;
		readonly ParsedFile parsedFile;
		readonly Dictionary<INode, ResolveResult> cache = new Dictionary<INode, ResolveResult>();
		
		readonly IResolveVisitorNavigator navigator;
		ResolveVisitorNavigationMode mode = ResolveVisitorNavigationMode.Scan;
		
		#region Constructor
		/// <summary>
		/// Creates a new ResolveVisitor instance.
		/// </summary>
		/// <param name="resolver">
		/// The CSharpResolver, describing the initial resolve context.
		/// If you visit a whole CompilationUnit with the resolve visitor, you can simply pass
		/// <c>new CSharpResolver(typeResolveContext)</c> without setting up the context.
		/// If you only visit a subtree, you need to pass a CSharpResolver initialized to the context for that subtree.
		/// </param>
		/// <param name="parsedFile">
		/// Result of the <see cref="TypeSystemConvertVisitor"/> for the file being passed. This is used for setting up the context on the resolver.
		/// You may pass <c>null</c> if you are only visiting a part of a method body and have already set up the context in the <paramref name="resolver"/>.
		/// </param>
		/// <param name="navigator">
		/// The navigator, which controls where the resolve visitor will switch between scanning mode and resolving mode.
		/// If you pass <c>null</c>, then <c>ResolveAll</c> mode will be used.
		/// </param>
		public ResolveVisitor(CSharpResolver resolver, ParsedFile parsedFile, IResolveVisitorNavigator navigator = null)
		{
			if (resolver == null)
				throw new ArgumentNullException("resolver");
			this.resolver = resolver;
			this.parsedFile = parsedFile;
			this.navigator = navigator;
			if (navigator == null)
				mode = ResolveVisitorNavigationMode.ResolveAll;
		}
		#endregion
		
		#region Scan / Resolve
		bool resolverEnabled {
			get { return mode != ResolveVisitorNavigationMode.Scan; }
		}
		
		public void Scan(INode node)
		{
			if (node == null)
				return;
			if (mode == ResolveVisitorNavigationMode.ResolveAll) {
				Resolve(node);
			} else {
				ResolveVisitorNavigationMode oldMode = mode;
				mode = navigator.Scan(node);
				switch (mode) {
					case ResolveVisitorNavigationMode.Skip:
						if (node is VariableDeclarationStatement) {
							// Enforce scanning of variable declarations.
							goto case ResolveVisitorNavigationMode.Scan;
						}
						break;
					case ResolveVisitorNavigationMode.Scan:
						node.AcceptVisitor(this, null);
						break;
					case ResolveVisitorNavigationMode.Resolve:
					case ResolveVisitorNavigationMode.ResolveAll:
						Resolve(node);
						break;
					default:
						throw new Exception("Invalid value for ResolveVisitorNavigationMode");
				}
				mode = oldMode;
			}
		}
		
		public ResolveResult Resolve(INode node)
		{
			if (node == null)
				return errorResult;
			bool wasScan = mode == ResolveVisitorNavigationMode.Scan;
			if (wasScan)
				mode = ResolveVisitorNavigationMode.Resolve;
			ResolveResult result;
			if (!cache.TryGetValue(node, out result)) {
				result = cache[node] = node.AcceptVisitor(this, null) ?? errorResult;
			}
			if (wasScan)
				mode = ResolveVisitorNavigationMode.Scan;
			return result;
		}
		
		void ScanChildren(INode node)
		{
			for (INode child = node.FirstChild; child != null; child = child.NextSibling) {
				Scan(child);
			}
		}
		#endregion
		
		#region GetResolveResult
		/// <summary>
		/// Gets the cached resolve result for the specified node.
		/// Returns <c>null</c> if no cached result was found (e.g. if the node was not visited; or if it was visited in scanning mode).
		/// </summary>
		public ResolveResult GetResolveResult(INode node)
		{
			ResolveResult result;
			if (cache.TryGetValue(node, out result))
				return result;
			else
				return null;
		}
		#endregion
		
		#region Track UsingScope
		ResolveResult IDomVisitor<object, ResolveResult>.VisitCompilationUnit(CompilationUnit unit, object data)
		{
			UsingScope previousUsingScope = resolver.UsingScope;
			try {
				if (parsedFile != null)
					resolver.UsingScope = parsedFile.RootUsingScope;
				ScanChildren(unit);
				return null;
			} finally {
				resolver.UsingScope = previousUsingScope;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitNamespaceDeclaration(NamespaceDeclaration namespaceDeclaration, object data)
		{
			UsingScope previousUsingScope = resolver.UsingScope;
			try {
				if (parsedFile != null) {
					resolver.UsingScope = parsedFile.GetUsingScope(namespaceDeclaration.StartLocation);
				}
				ScanChildren(namespaceDeclaration);
				return new NamespaceResolveResult(resolver.UsingScope.NamespaceName);
			} finally {
				resolver.UsingScope = previousUsingScope;
			}
		}
		#endregion
		
		#region Track CurrentTypeDefinition
		ResolveResult VisitTypeOrDelegate(INode typeDeclaration)
		{
			ITypeDefinition previousTypeDefinition = resolver.CurrentTypeDefinition;
			try {
				ITypeDefinition newTypeDefinition = null;
				if (resolver.CurrentTypeDefinition != null) {
					foreach (ITypeDefinition innerClass in resolver.CurrentTypeDefinition.InnerClasses) {
						if (innerClass.Region.IsInside(typeDeclaration.StartLocation)) {
							newTypeDefinition = innerClass;
							break;
						}
					}
				} else if (parsedFile != null) {
					newTypeDefinition = parsedFile.GetTopLevelTypeDefinition(typeDeclaration.StartLocation);
				}
				if (newTypeDefinition != null)
					resolver.CurrentTypeDefinition = newTypeDefinition;
				ScanChildren(typeDeclaration);
				return newTypeDefinition != null ? new TypeResolveResult(newTypeDefinition) : errorResult;
			} finally {
				resolver.CurrentTypeDefinition = previousTypeDefinition;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitTypeDeclaration(TypeDeclaration typeDeclaration, object data)
		{
			return VisitTypeOrDelegate(typeDeclaration);
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitDelegateDeclaration(DelegateDeclaration delegateDeclaration, object data)
		{
			return VisitTypeOrDelegate(delegateDeclaration);
		}
		#endregion
		
		#region Track CurrentMember
		ResolveResult IDomVisitor<object, ResolveResult>.VisitFieldDeclaration(FieldDeclaration fieldDeclaration, object data)
		{
			int initializerCount = fieldDeclaration.Variables.Count();
			ResolveResult result = null;
			for (INode node = fieldDeclaration.FirstChild; node != null; node = node.NextSibling) {
				if (node.Role == FieldDeclaration.Roles.Initializer) {
					if (resolver.CurrentTypeDefinition != null) {
						resolver.CurrentMember = resolver.CurrentTypeDefinition.Fields.FirstOrDefault(f => f.Region.IsInside(node.StartLocation));
					}
					
					if (resolverEnabled && initializerCount == 1) {
						result = Resolve(node);
					} else {
						Scan(node);
					}
					
					resolver.CurrentMember = null;
				} else {
					Scan(node);
				}
			}
			return result;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitVariableInitializer(VariableInitializer variableInitializer, object data)
		{
			ScanChildren(variableInitializer);
			if (resolverEnabled) {
				if (variableInitializer.Parent is FieldDeclaration) {
					if (resolver.CurrentMember != null)
						return new MemberResolveResult(resolver.CurrentMember, resolver.CurrentMember.ReturnType.Resolve(resolver.Context));
				} else {
					string identifier = variableInitializer.Name;
					foreach (IVariable v in resolver.LocalVariables) {
						if (v.Name == identifier) {
							object constantValue = v.IsConst ? v.ConstantValue.GetValue(resolver.Context) : null;
							return new VariableResolveResult(v, v.Type.Resolve(resolver.Context), constantValue);
						}
					}
				}
				return errorResult;
			} else {
				return null;
			}
		}
		
		ResolveResult VisitMethodMember(AbstractMemberBase member)
		{
			try {
				if (resolver.CurrentTypeDefinition != null) {
					resolver.CurrentMember = resolver.CurrentTypeDefinition.Methods.FirstOrDefault(m => m.Region.IsInside(member.StartLocation));
				}
				
				ScanChildren(member);
				
				if (resolverEnabled && resolver.CurrentMember != null)
					return new MemberResolveResult(resolver.CurrentMember, resolver.CurrentMember.ReturnType.Resolve(resolver.Context));
				else
					return errorResult;
			} finally {
				resolver.CurrentMember = null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitMethodDeclaration(MethodDeclaration methodDeclaration, object data)
		{
			return VisitMethodMember(methodDeclaration);
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitOperatorDeclaration(OperatorDeclaration operatorDeclaration, object data)
		{
			return VisitMethodMember(operatorDeclaration);
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitConstructorDeclaration(ConstructorDeclaration constructorDeclaration, object data)
		{
			return VisitMethodMember(constructorDeclaration);
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitDestructorDeclaration(DestructorDeclaration destructorDeclaration, object data)
		{
			return VisitMethodMember(destructorDeclaration);
		}
		
		// handle properties/indexers
		ResolveResult VisitPropertyMember(PropertyDeclaration propertyDeclaration)
		{
			try {
				if (resolver.CurrentTypeDefinition != null) {
					resolver.CurrentMember = resolver.CurrentTypeDefinition.Properties.FirstOrDefault(p => p.Region.IsInside(propertyDeclaration.StartLocation));
				}
				
				for (INode node = propertyDeclaration.FirstChild; node != null; node = node.NextSibling) {
					if (node.Role == PropertyDeclaration.PropertySetRole && resolver.CurrentMember != null) {
						resolver.PushBlock();
						resolver.AddVariable(resolver.CurrentMember.ReturnType, "value");
						Scan(node);
						resolver.PopBlock();
					} else {
						Scan(node);
					}
				}
				if (resolverEnabled && resolver.CurrentMember != null)
					return new MemberResolveResult(resolver.CurrentMember, resolver.CurrentMember.ReturnType.Resolve(resolver.Context));
				else
					return errorResult;
			} finally {
				resolver.CurrentMember = null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitPropertyDeclaration(PropertyDeclaration propertyDeclaration, object data)
		{
			return VisitPropertyMember(propertyDeclaration);
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitIndexerDeclaration(IndexerDeclaration indexerDeclaration, object data)
		{
			return VisitPropertyMember(indexerDeclaration);
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitEventDeclaration(EventDeclaration eventDeclaration, object data)
		{
			try {
				if (resolver.CurrentTypeDefinition != null) {
					resolver.CurrentMember = resolver.CurrentTypeDefinition.Events.FirstOrDefault(e => e.Region.IsInside(eventDeclaration.StartLocation));
				}
				
				if (resolver.CurrentMember != null) {
					resolver.PushBlock();
					resolver.AddVariable(resolver.CurrentMember.ReturnType, "value");
					ScanChildren(eventDeclaration);
					resolver.PopBlock();
				} else {
					ScanChildren(eventDeclaration);
				}
				
				if (resolverEnabled && resolver.CurrentMember != null)
					return new MemberResolveResult(resolver.CurrentMember, resolver.CurrentMember.ReturnType.Resolve(resolver.Context));
				else
					return errorResult;
			} finally {
				resolver.CurrentMember = null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitParameterDeclaration(ParameterDeclaration parameterDeclaration, object data)
		{
			ScanChildren(parameterDeclaration);
			if (resolverEnabled) {
				IParameterizedMember pm = resolver.CurrentMember as IParameterizedMember;
				if (pm != null) {
					foreach (IParameter p in pm.Parameters) {
						if (p.Name == parameterDeclaration.Name) {
							return new VariableResolveResult(p, p.Type.Resolve(resolver.Context));
						}
					}
				}
				return errorResult;
			} else {
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitEnumMemberDeclaration(EnumMemberDeclaration enumMemberDeclaration, object data)
		{
			try {
				if (resolver.CurrentTypeDefinition != null) {
					resolver.CurrentMember = resolver.CurrentTypeDefinition.Fields.FirstOrDefault(f => f.Region.IsInside(enumMemberDeclaration.StartLocation));
				}
				
				ScanChildren(enumMemberDeclaration);
				
				if (resolverEnabled && resolver.CurrentMember != null)
					return new MemberResolveResult(resolver.CurrentMember, resolver.CurrentMember.ReturnType.Resolve(resolver.Context));
				else
					return errorResult;
			} finally {
				resolver.CurrentMember = null;
			}
		}
		#endregion
		
		#region Track CheckForOverflow
		ResolveResult IDomVisitor<object, ResolveResult>.VisitCheckedExpression(CheckedExpression checkedExpression, object data)
		{
			bool oldCheckForOverflow = resolver.CheckForOverflow;
			try {
				resolver.CheckForOverflow = true;
				if (resolverEnabled) {
					return Resolve(checkedExpression.Expression);
				} else {
					ScanChildren(checkedExpression);
					return null;
				}
			} finally {
				resolver.CheckForOverflow = oldCheckForOverflow;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitUncheckedExpression(UncheckedExpression uncheckedExpression, object data)
		{
			bool oldCheckForOverflow = resolver.CheckForOverflow;
			try {
				resolver.CheckForOverflow = false;
				if (resolverEnabled) {
					return Resolve(uncheckedExpression.Expression);
				} else {
					ScanChildren(uncheckedExpression);
					return null;
				}
			} finally {
				resolver.CheckForOverflow = oldCheckForOverflow;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitCheckedStatement(CheckedStatement checkedStatement, object data)
		{
			bool oldCheckForOverflow = resolver.CheckForOverflow;
			try {
				resolver.CheckForOverflow = true;
				ScanChildren(checkedStatement);
				return null;
			} finally {
				resolver.CheckForOverflow = oldCheckForOverflow;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitUncheckedStatement(UncheckedStatement uncheckedStatement, object data)
		{
			bool oldCheckForOverflow = resolver.CheckForOverflow;
			try {
				resolver.CheckForOverflow = false;
				ScanChildren(uncheckedStatement);
				return null;
			} finally {
				resolver.CheckForOverflow = oldCheckForOverflow;
			}
		}
		#endregion
		
		#region Visit Expressions
		static bool IsTargetOfInvocation(INode node)
		{
			InvocationExpression ie = node.Parent as InvocationExpression;
			return ie != null && ie.Target == node;
		}
		
		IType ResolveType(INode node)
		{
			return SharedTypes.UnknownType;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitAnonymousMethodExpression(AnonymousMethodExpression anonymousMethodExpression, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitArgListExpression(ArgListExpression argListExpression, object data)
		{
			ScanChildren(argListExpression);
			return new ResolveResult(resolver.Context.GetClass(typeof(RuntimeArgumentHandle)) ?? SharedTypes.UnknownType);
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitArrayObjectCreateExpression(ArrayObjectCreateExpression arrayObjectCreateExpression, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitAsExpression(AsExpression asExpression, object data)
		{
			if (resolverEnabled) {
				Scan(asExpression.Expression);
				return new ResolveResult(ResolveType(asExpression.TypeReference));
			} else {
				ScanChildren(asExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitAssignmentExpression(AssignmentExpression assignmentExpression, object data)
		{
			if (resolverEnabled) {
				ResolveResult left = Resolve(assignmentExpression.Left);
				Scan(assignmentExpression.Right);
				return new ResolveResult(left.Type);
			} else {
				ScanChildren(assignmentExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitBaseReferenceExpression(BaseReferenceExpression baseReferenceExpression, object data)
		{
			if (resolverEnabled) {
				return resolver.ResolveBaseReference();
			} else {
				ScanChildren(baseReferenceExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitBinaryOperatorExpression(BinaryOperatorExpression binaryOperatorExpression, object data)
		{
			if (resolverEnabled) {
				ResolveResult left = Resolve(binaryOperatorExpression.Left);
				ResolveResult right = Resolve(binaryOperatorExpression.Right);
				return resolver.ResolveBinaryOperator(binaryOperatorExpression.BinaryOperatorType, left, right);
			} else {
				ScanChildren(binaryOperatorExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitCastExpression(CastExpression castExpression, object data)
		{
			if (resolverEnabled) {
				return resolver.ResolveCast(ResolveType(castExpression.CastTo), Resolve(castExpression.Expression));
			} else {
				ScanChildren(castExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitConditionalExpression(ConditionalExpression conditionalExpression, object data)
		{
			if (resolverEnabled) {
				Scan(conditionalExpression.Condition);
				return resolver.ResolveConditional(Resolve(conditionalExpression.TrueExpression),
				                                   Resolve(conditionalExpression.FalseExpression));
			} else {
				ScanChildren(conditionalExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitDefaultValueExpression(DefaultValueExpression defaultValueExpression, object data)
		{
			if (resolverEnabled) {
				return new ConstantResolveResult(ResolveType(defaultValueExpression.TypeReference), null);
			} else {
				ScanChildren(defaultValueExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitDirectionExpression(DirectionExpression directionExpression, object data)
		{
			if (resolverEnabled) {
				ResolveResult rr = Resolve(directionExpression.Expression);
				return new ByReferenceResolveResult(rr.Type, directionExpression.FieldDirection == FieldDirection.Out);
			} else {
				ScanChildren(directionExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitIdentifierExpression(IdentifierExpression identifierExpression, object data)
		{
			if (resolverEnabled) {
				// TODO: type arguments?
				return resolver.ResolveSimpleName(identifierExpression.Identifier, EmptyList<IType>.Instance,
				                                  IsTargetOfInvocation(identifierExpression));
			} else {
				ScanChildren(identifierExpression);
				return null;
			}
		}
		
		ResolveResult[] GetArguments(IEnumerable<INode> argumentExpressions, out string[] argumentNames)
		{
			argumentNames = null; // TODO: add support for named arguments
			ResolveResult[] arguments = new ResolveResult[argumentExpressions.Count()];
			int i = 0;
			foreach (INode argument in argumentExpressions) {
				arguments[i++] = Resolve(argument);
			}
			return arguments;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitIndexerExpression(IndexerExpression indexerExpression, object data)
		{
			if (resolverEnabled) {
				ResolveResult target = Resolve(indexerExpression.Target);
				string[] argumentNames;
				ResolveResult[] arguments = GetArguments(indexerExpression.Arguments, out argumentNames);
				return resolver.ResolveIndexer(target, arguments, argumentNames);
			} else {
				ScanChildren(indexerExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitInvocationExpression(InvocationExpression invocationExpression, object data)
		{
			if (resolverEnabled) {
				ResolveResult target = Resolve(invocationExpression.Target);
				string[] argumentNames;
				ResolveResult[] arguments = GetArguments(invocationExpression.Arguments, out argumentNames);
				return resolver.ResolveInvocation(target, arguments, argumentNames);
			} else {
				ScanChildren(invocationExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitIsExpression(IsExpression isExpression, object data)
		{
			ScanChildren(isExpression);
			return new ResolveResult(TypeCode.Boolean.ToTypeReference().Resolve(resolver.Context));
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitLambdaExpression(LambdaExpression lambdaExpression, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitMemberReferenceExpression(MemberReferenceExpression memberReferenceExpression, object data)
		{
			if (resolverEnabled) {
				ResolveResult target = Resolve(memberReferenceExpression.Target);
				List<INode> typeArgumentNodes = memberReferenceExpression.TypeArguments.ToList();
				// TODO: type arguments?
				return resolver.ResolveMemberAccess(target, memberReferenceExpression.MemberName,
				                                    EmptyList<IType>.Instance,
				                                    IsTargetOfInvocation(memberReferenceExpression));
			} else {
				ScanChildren(memberReferenceExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitNullReferenceExpression(NullReferenceExpression nullReferenceExpression, object data)
		{
			if (resolverEnabled) {
				return resolver.ResolvePrimitive(null);
			} else {
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitObjectCreateExpression(ObjectCreateExpression objectCreateExpression, object data)
		{
			if (resolverEnabled) {
				IType type = ResolveType(objectCreateExpression.Type);
				string[] argumentNames;
				ResolveResult[] arguments = GetArguments(objectCreateExpression.Arguments, out argumentNames);
				return resolver.ResolveObjectCreation(type, arguments, argumentNames);
			} else {
				ScanChildren(objectCreateExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitParenthesizedExpression(ParenthesizedExpression parenthesizedExpression, object data)
		{
			if (resolverEnabled) {
				return Resolve(parenthesizedExpression.Expression);
			} else {
				Scan(parenthesizedExpression.Expression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitPointerReferenceExpression(PointerReferenceExpression pointerReferenceExpression, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitPrimitiveExpression(PrimitiveExpression primitiveExpression, object data)
		{
			if (resolverEnabled) {
				return resolver.ResolvePrimitive(primitiveExpression.Value);
			} else {
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitSizeOfExpression(SizeOfExpression sizeOfExpression, object data)
		{
			if (resolverEnabled) {
				return resolver.ResolveSizeOf(ResolveType(sizeOfExpression.Type));
			} else {
				ScanChildren(sizeOfExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitStackAllocExpression(StackAllocExpression stackAllocExpression, object data)
		{
			if (resolverEnabled) {
				Scan(stackAllocExpression.CountExpression);
				return new ResolveResult(new PointerType(ResolveType(stackAllocExpression.Type)));
			} else {
				ScanChildren(stackAllocExpression);
				return null;
			}
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitThisReferenceExpression(ThisReferenceExpression thisReferenceExpression, object data)
		{
			return resolver.ResolveThisReference();
		}
		
		static readonly GetClassTypeReference systemType = new GetClassTypeReference("System.Type", 0);
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitTypeOfExpression(TypeOfExpression typeOfExpression, object data)
		{
			ScanChildren(typeOfExpression);
			if (resolverEnabled)
				return new ResolveResult(systemType.Resolve(resolver.Context));
			else
				return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitUnaryOperatorExpression(UnaryOperatorExpression unaryOperatorExpression, object data)
		{
			if (resolverEnabled) {
				ResolveResult expr = Resolve(unaryOperatorExpression.Expression);
				return resolver.ResolveUnaryOperator(unaryOperatorExpression.UnaryOperatorType, expr);
			} else {
				ScanChildren(unaryOperatorExpression);
				return null;
			}
		}
		#endregion
		
		#region Local Variable Scopes (Block Statements)
		ResolveResult IDomVisitor<object, ResolveResult>.VisitBlockStatement(BlockStatement blockStatement, object data)
		{
			resolver.PushBlock();
			ScanChildren(blockStatement);
			resolver.PopBlock();
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitUsingStatement(UsingStatement usingStatement, object data)
		{
			resolver.PushBlock();
			ScanChildren(usingStatement);
			resolver.PopBlock();
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitFixedStatement(FixedStatement fixedStatement, object data)
		{
			resolver.PushBlock();
			ScanChildren(fixedStatement);
			resolver.PopBlock();
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitForStatement(ForStatement forStatement, object data)
		{
			resolver.PushBlock();
			ScanChildren(forStatement);
			resolver.PopBlock();
			return null;
		}
		
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitForeachStatement(ForeachStatement foreachStatement, object data)
		{
			resolver.PushBlock();
			ITypeReference type = MakeTypeReference(foreachStatement.VariableType, foreachStatement.Expression, true);
			ScanChildren(foreachStatement);
			resolver.PopBlock();
			return null;
		}
		#endregion
		
		#region Simple Statements (only ScanChildren)
		ResolveResult IDomVisitor<object, ResolveResult>.VisitExpressionStatement(ExpressionStatement expressionStatement, object data)
		{
			ScanChildren(expressionStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitBreakStatement(BreakStatement breakStatement, object data)
		{
			ScanChildren(breakStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitContinueStatement(ContinueStatement continueStatement, object data)
		{
			ScanChildren(continueStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitEmptyStatement(EmptyStatement emptyStatement, object data)
		{
			ScanChildren(emptyStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitGotoStatement(GotoStatement gotoStatement, object data)
		{
			ScanChildren(gotoStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitIfElseStatement(IfElseStatement ifElseStatement, object data)
		{
			ScanChildren(ifElseStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitLabelStatement(LabelStatement labelStatement, object data)
		{
			ScanChildren(labelStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitLockStatement(LockStatement lockStatement, object data)
		{
			ScanChildren(lockStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitReturnStatement(ReturnStatement returnStatement, object data)
		{
			ScanChildren(returnStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitSwitchStatement(SwitchStatement switchStatement, object data)
		{
			ScanChildren(switchStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitSwitchSection(SwitchSection switchSection, object data)
		{
			ScanChildren(switchSection);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitCaseLabel(CaseLabel caseLabel, object data)
		{
			ScanChildren(caseLabel);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitThrowStatement(ThrowStatement throwStatement, object data)
		{
			ScanChildren(throwStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitUnsafeStatement(UnsafeStatement unsafeStatement, object data)
		{
			ScanChildren(unsafeStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitWhileStatement(WhileStatement whileStatement, object data)
		{
			ScanChildren(whileStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitYieldStatement(YieldStatement yieldStatement, object data)
		{
			ScanChildren(yieldStatement);
			return null;
		}
		#endregion
		
		#region Try / Catch
		ResolveResult IDomVisitor<object, ResolveResult>.VisitTryCatchStatement(TryCatchStatement tryCatchStatement, object data)
		{
			ScanChildren(tryCatchStatement);
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitCatchClause(CatchClause catchClause, object data)
		{
			resolver.PushBlock();
			if (catchClause.VariableName != null) {
				resolver.AddVariable(MakeTypeReference(catchClause.ReturnType, null, false), catchClause.VariableName);
			}
			ScanChildren(catchClause);
			resolver.PopBlock();
			return null;
		}
		#endregion
		
		#region VariableDeclarationStatement
		ResolveResult IDomVisitor<object, ResolveResult>.VisitVariableDeclarationStatement(VariableDeclarationStatement variableDeclarationStatement, object data)
		{
			bool isConst = (variableDeclarationStatement.Modifiers & Modifiers.Const) != 0;
			VariableInitializer firstInitializer = variableDeclarationStatement.Variables.FirstOrDefault();
			ITypeReference type = MakeTypeReference(variableDeclarationStatement.ReturnType,
			                                        firstInitializer != null ? firstInitializer.Initializer : null,
			                                        false);
			
			int initializerCount = variableDeclarationStatement.Variables.Count();
			ResolveResult result = null;
			for (INode node = variableDeclarationStatement.FirstChild; node != null; node = node.NextSibling) {
				if (node.Role == FieldDeclaration.Roles.Initializer) {
					VariableInitializer vi = (VariableInitializer)node;
					
					IConstantValue cv = null;
					if (isConst)
						throw new NotImplementedException();
					resolver.AddVariable(type, vi.Name, cv);
					
					if (resolverEnabled && initializerCount == 1) {
						result = Resolve(node);
					} else {
						Scan(node);
					}
				} else {
					Scan(node);
				}
			}
			return result;
		}
		#endregion
		
		#region Local Variable Type Inference
		/// <summary>
		/// Creates a type reference for the specified type node.
		/// If the type node is 'var', performs type inference on the initializer expression.
		/// </summary>
		ITypeReference MakeTypeReference(INode type, INode initializerExpression, bool isForEach)
		{
			if (initializerExpression != null && IsVar(type)) {
				return new VarTypeReference(this, resolver.Clone(), initializerExpression, isForEach);
			} else {
				return TypeSystemConvertVisitor.ConvertType(type);
			}
		}
		
		static bool IsVar(INode returnType)
		{
			return returnType is IdentifierExpression && ((IdentifierExpression)returnType).Identifier == "var";
		}
		
		sealed class VarTypeReference : ITypeReference
		{
			ResolveVisitor visitor;
			CSharpResolver storedContext;
			INode initializerExpression;
			bool isForEach;
			
			IType result;
			
			public VarTypeReference(ResolveVisitor visitor, CSharpResolver storedContext, INode initializerExpression, bool isForEach)
			{
				this.visitor = visitor;
				this.storedContext = storedContext;
				this.initializerExpression = initializerExpression;
				this.isForEach = isForEach;
			}
			
			public IType Resolve(ITypeResolveContext context)
			{
				if (visitor == null)
					return result ?? SharedTypes.UnknownType;
				
				var oldMode = visitor.mode;
				var oldResolver = visitor.resolver;
				try {
					visitor.mode = ResolveVisitorNavigationMode.Resolve;
					visitor.resolver = storedContext;
					
					result = visitor.Resolve(initializerExpression).Type;
					
					if (isForEach) {
						result = GetElementType(result);
					}
					
					return result;
				} finally {
					visitor.mode = oldMode;
					visitor.resolver = oldResolver;
					
					visitor = null;
					storedContext = null;
					initializerExpression = null;
				}
			}
			
			IType GetElementType(IType result)
			{
				foreach (IType baseType in result.GetAllBaseTypes(storedContext.Context)) {
					ITypeDefinition baseTypeDef = baseType.GetDefinition();
					if (baseTypeDef != null && baseTypeDef.Name == "IEnumerable") {
						if (baseTypeDef.Namespace == "System.Collections.Generic" && baseTypeDef.TypeParameterCount == 1) {
							ParameterizedType pt = baseType as ParameterizedType;
							if (pt != null) {
								return pt.TypeArguments[0];
							}
						} else if (baseTypeDef.Namespace == "System.Collections" && baseTypeDef.TypeParameterCount == 0) {
							return TypeCode.Object.ToTypeReference().Resolve(storedContext.Context);
						}
					}
				}
				return SharedTypes.UnknownType;
			}
			
			public override string ToString()
			{
				if (visitor == null)
					return "var=" + result;
				else
					return "var (not yet resolved)";
			}
		}
		#endregion
		
		#region Attributes
		ResolveResult IDomVisitor<object, ResolveResult>.VisitAttribute(Attribute attribute, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitAttributeSection(AttributeSection attributeSection, object data)
		{
			ScanChildren(attributeSection);
			return null;
		}
		#endregion
		
		#region Using Declaration
		ResolveResult IDomVisitor<object, ResolveResult>.VisitUsingDeclaration(UsingDeclaration usingDeclaration, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitUsingAliasDeclaration(UsingAliasDeclaration usingDeclaration, object data)
		{
			throw new NotImplementedException();
		}
		#endregion
		
		#region Type References
		ResolveResult IDomVisitor<object, ResolveResult>.VisitFullTypeName(FullTypeName fullTypeName, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitComposedType(ComposedType composedType, object data)
		{
			throw new NotImplementedException();
		}
		#endregion
		
		#region Query Expressions
		ResolveResult IDomVisitor<object, ResolveResult>.VisitQueryExpressionFromClause(QueryExpressionFromClause queryExpressionFromClause, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitQueryExpressionWhereClause(QueryExpressionWhereClause queryExpressionWhereClause, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitQueryExpressionJoinClause(QueryExpressionJoinClause queryExpressionJoinClause, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitQueryExpressionGroupClause(QueryExpressionGroupClause queryExpressionGroupClause, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitQueryExpressionLetClause(QueryExpressionLetClause queryExpressionLetClause, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitQueryExpressionOrderClause(QueryExpressionOrderClause queryExpressionOrderClause, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitQueryExpressionOrdering(QueryExpressionOrdering queryExpressionOrdering, object data)
		{
			throw new NotImplementedException();
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitQueryExpressionSelectClause(QueryExpressionSelectClause queryExpressionSelectClause, object data)
		{
			throw new NotImplementedException();
		}
		#endregion
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitIdentifier(Identifier identifier, object data)
		{
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitConstraint(Constraint constraint, object data)
		{
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitConstructorInitializer(ConstructorInitializer constructorInitializer, object data)
		{
			return null;
		}
		
		ResolveResult IDomVisitor<object, ResolveResult>.VisitAccessorDeclaration(Accessor accessorDeclaration, object data)
		{
			return null;
		}
	}
}
