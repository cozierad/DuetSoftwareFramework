﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SourceGenerators.ObjectModel
{
    internal static class Helpers
    {
        public static string GetJsonPropertyName(this PropertyDeclarationSyntax propertySyntax)
        {
            string name = propertySyntax.Identifier.ValueText;
            if (name == "SBC")
            {
                return "sbc";
            }
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        public static bool HasSetter(this PropertyDeclarationSyntax propertySyntax)
        {
            return propertySyntax.AccessorList != null && propertySyntax.AccessorList.Accessors.Any(SyntaxKind.SetAccessorDeclaration);
        }

        public static bool IsSbcProperty(this PropertyDeclarationSyntax propertySyntax)
        {
            return propertySyntax.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString() == "SbcProperty"));
        }
    }
}