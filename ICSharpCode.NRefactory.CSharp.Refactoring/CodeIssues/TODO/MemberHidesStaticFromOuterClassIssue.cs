//
// MemberHidesStaticFromOuterClass.cs
//
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
//
// Copyright (c) 2013 Xamarin Inc. (http://xamarin.com)
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
using System;
using ICSharpCode.NRefactory.Refactoring;

namespace ICSharpCode.NRefactory.CSharp.Refactoring
{
	/* Case:

    public static class Super
    {
        public static class Inner
        {
            public static string SomeValue { get; set; }
        }

        public static string SomeValue { get; set; }
    }

- no resolution
	*/
//	[IssueDescription (
//		"Member hides static member from outer class",
//		Description = "Member hides static member from outer class",
//		Category = IssueCategories.CodeQualityIssues,
//		Severity = Severity.Warning,
//		ResharperDisableKeyword = "MemberHidesStaticFromOuterClass")]
//	public class MemberHidesStaticFromOuterClassIssue : GatherVisitorCodeIssueProvider
//	{
//		protected override IGatherVisitor CreateVisitor(BaseRefactoringContext context)
//		{
//			return new GatherVisitor(context);
//		}
//
//		class GatherVisitor : GatherVisitorBase<MemberHidesStaticFromOuterClassIssue>
//		{
//			public GatherVisitor (BaseRefactoringContext ctx) : base (ctx)
//			{
//			}
//		}
//	}
}
