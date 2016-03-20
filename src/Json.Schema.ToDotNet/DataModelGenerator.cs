﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.
// Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.JSchema;

namespace Microsoft.Json.Schema.ToDotNet
{
    /// <summary>
    /// Generates a set of .NET classes from a JSON schema.
    /// </summary>
    public class DataModelGenerator
    {
        private const string SyntaxInterfaceTypeName = "ISyntax";

        // TODO: Pass this in on command line.
        private const string SchemaName = "Sarif";

        private readonly DataModelGeneratorSettings _settings;
        private readonly IFileSystem _fileSystem;
        private JsonSchema _rootSchema;
        private Dictionary<string, string> _pathToFileContentsDictionary;
        private List<string> _generatedClassNames;
        private string _kindEnumName;
        private List<AdditionalTypeRequiredEventArgs> _additionalTypesRequiredList;

        public DataModelGenerator(DataModelGeneratorSettings settings)
            : this(settings, new FileSystem())
        {
        }

        // This ctor allows unit tests to mock the file system.
        internal DataModelGenerator(DataModelGeneratorSettings settings, IFileSystem fileSystem)
        {
            _settings = settings;
            _settings.Validate();

            _fileSystem = fileSystem;
            _pathToFileContentsDictionary = new Dictionary<string, string>();

            _additionalTypesRequiredList = new List<AdditionalTypeRequiredEventArgs>();
            _generatedClassNames = new List<string>();
        }

        public string Generate(JsonSchema rootSchema)
        {
            _additionalTypesRequiredList.Clear();

            if (_settings.GenerateCloningCode)
            {
                _kindEnumName = SchemaName + "Kind";
            }

            _rootSchema = JsonSchema.Collapse(rootSchema);

            if (_fileSystem.DirectoryExists(_settings.OutputDirectory) && !_settings.ForceOverwrite)
            {
                throw JSchemaException.Create(Resources.ErrorOutputDirectoryExists, _settings.OutputDirectory);
            }

            _fileSystem.CreateDirectory(_settings.OutputDirectory);

            if (_rootSchema.Type != JsonType.Object)
            {
                throw JSchemaException.Create(Resources.ErrorNotAnObject, _rootSchema.Type);
            }

            string rootFileText = CreateFile(_settings.RootClassName, _rootSchema);

            if (_rootSchema.Definitions != null)
            {
                foreach (KeyValuePair<string, JsonSchema> definition in _rootSchema.Definitions)
                {
                    CreateFile(definition.Key, definition.Value);
                }
            }

            foreach (AdditionalTypeRequiredEventArgs e in _additionalTypesRequiredList)
            {
                GenerateAdditionalType(e.Hint, e.Schema);
            }

            if (_settings.GenerateCloningCode)
            {
                _pathToFileContentsDictionary[SyntaxInterfaceTypeName] =
                    GenerateSyntaxInterface(SchemaName, _kindEnumName);

                _pathToFileContentsDictionary[_kindEnumName] =
                    GenerateKindEnum(_kindEnumName);
            }

            foreach (KeyValuePair<string, string> entry in _pathToFileContentsDictionary)
            {
                _fileSystem.WriteAllText(Path.Combine(_settings.OutputDirectory, entry.Key + ".cs"), entry.Value);
            }

            // Returning the text of the file generated from the root schema allows this method
            // to be more easily unit tested.
            return rootFileText;
        }

        private string GenerateSyntaxInterface(string schemaName, string enumName)
        {
            SyntaxTokenList modifiers = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

            var accessorDeclarations = SyntaxFactory.List(
                new AccessorDeclarationSyntax[]
                {
                    SyntaxHelper.MakeGetAccessor()
                });

            PropertyDeclarationSyntax syntaxKindPropertyDeclaration =
                SyntaxFactory.PropertyDeclaration(
                    default(SyntaxList<AttributeListSyntax>),
                    default(SyntaxTokenList), // modifiers
                    SyntaxFactory.ParseTypeName(enumName),
                    default(ExplicitInterfaceSpecifierSyntax),
                    SyntaxFactory.Identifier("SyntaxKind"),
                    SyntaxFactory.AccessorList(accessorDeclarations))
                    .WithLeadingTrivia(
                        SyntaxHelper.MakeDocComment(Resources.SyntaxInterfaceKindDescription));

            MethodDeclarationSyntax deepCloneMethodDeclaration =
                SyntaxFactory.MethodDeclaration(
                    default(SyntaxList<AttributeListSyntax>),
                    default(SyntaxTokenList), // modifiers
                    SyntaxFactory.ParseTypeName(SyntaxInterfaceTypeName),
                    default(ExplicitInterfaceSpecifierSyntax),
                    SyntaxFactory.Identifier("DeepClone"),
                    default(TypeParameterListSyntax),
                    SyntaxFactory.ParameterList(),
                    default(SyntaxList<TypeParameterConstraintClauseSyntax>),
                    default(BlockSyntax), // body
                    SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .WithLeadingTrivia(
                        SyntaxHelper.MakeDocComment(Resources.SyntaxInterfaceDeepCloneDescription));

            InterfaceDeclarationSyntax interfaceDeclaration =
                SyntaxFactory.InterfaceDeclaration(SyntaxInterfaceTypeName)
                    .WithModifiers(modifiers)
                    .WithLeadingTrivia(SyntaxHelper.MakeDocComment(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.SyntaxInterfaceDescription,
                            schemaName)))
                    .WithMembers(
                        SyntaxFactory.List(
                            new MemberDeclarationSyntax[]
                            {
                                syntaxKindPropertyDeclaration,
                                deepCloneMethodDeclaration
                            }));

            SyntaxList<MemberDeclarationSyntax> namespaceMembers =
                SyntaxFactory.SingletonList<MemberDeclarationSyntax>(interfaceDeclaration);

            NamespaceDeclarationSyntax namespaceDecl =
                SyntaxFactory.NamespaceDeclaration(
                    SyntaxFactory.IdentifierName(_settings.NamespaceName))
                .WithMembers(namespaceMembers);

            SyntaxList<MemberDeclarationSyntax> compilationUnitMembers =
                SyntaxFactory.SingletonList<MemberDeclarationSyntax>(namespaceDecl);

            CompilationUnitSyntax compilationUnit = SyntaxFactory.CompilationUnit()
                .WithMembers(compilationUnitMembers);

            if (!string.IsNullOrWhiteSpace(_settings.CopyrightNotice))
            {
                compilationUnit = compilationUnit.WithLeadingTrivia(
                    SyntaxFactory.ParseLeadingTrivia(_settings.CopyrightNotice));
            }

            var workspace = new AdhocWorkspace();
            SyntaxNode formattedNode = Formatter.Format(compilationUnit, workspace);

            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
            {
                formattedNode.WriteTo(writer);
            }

            return sb.ToString();
        }

        private string GenerateKindEnum(string enumName)
        {
            SyntaxTokenList modifiers = SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

            EnumDeclarationSyntax enumDeclaration =
                SyntaxFactory.EnumDeclaration(SyntaxFactory.Identifier(enumName))
                    .WithModifiers(modifiers)
                    .WithMembers(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.EnumMemberDeclaration("None")
                                .WithLeadingTrivia(
                                    SyntaxHelper.MakeDocComment(Resources.KindEnumNoneDescription))))
                    .AddMembers(
                        _generatedClassNames.Select(gcn => GenerateKindEnumMember(gcn)).ToArray())
                    .WithLeadingTrivia(SyntaxHelper.MakeDocComment(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.KindEnumDescription,
                            SyntaxInterfaceTypeName)));

            SyntaxList<MemberDeclarationSyntax> namespaceMembers =
                SyntaxFactory.SingletonList<MemberDeclarationSyntax>(enumDeclaration);

            NamespaceDeclarationSyntax namespaceDecl =
                SyntaxFactory.NamespaceDeclaration(
                    SyntaxFactory.IdentifierName(_settings.NamespaceName))
                .WithMembers(namespaceMembers);

            SyntaxList<MemberDeclarationSyntax> compilationUnitMembers =
                SyntaxFactory.SingletonList<MemberDeclarationSyntax>(namespaceDecl);

            CompilationUnitSyntax compilationUnit = SyntaxFactory.CompilationUnit()
                .WithMembers(compilationUnitMembers);

            if (!string.IsNullOrWhiteSpace(_settings.CopyrightNotice))
            {
                compilationUnit = compilationUnit.WithLeadingTrivia(
                    SyntaxFactory.ParseLeadingTrivia(_settings.CopyrightNotice));
            }

            var workspace = new AdhocWorkspace();
            SyntaxNode formattedNode = Formatter.Format(compilationUnit, workspace);

            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
            {
                formattedNode.WriteTo(writer);
            }

            return sb.ToString();
        }

        private EnumMemberDeclarationSyntax GenerateKindEnumMember(string className)
        {
            string description = string.Format(
                CultureInfo.CurrentCulture,
                Resources.KindEnumMemberDescription,
                className);

            return SyntaxFactory.EnumMemberDeclaration(className)
                .WithLeadingTrivia(
                    SyntaxHelper.MakeDocComment(description));
        }

        internal string CreateFile(string className, JsonSchema schema)
        {
            className = className.ToPascalCase();

            CodeGenHint[] hints = null;
            EnumHint enumHint = null;
            InterfaceHint interfaceHint = null;
            if (_settings.HintDictionary != null && _settings.HintDictionary.TryGetValue(className.ToCamelCase(), out hints))
            {
                enumHint = hints.FirstOrDefault(h => h is EnumHint) as EnumHint;
                interfaceHint = hints.FirstOrDefault(h => h is InterfaceHint) as InterfaceHint;
            }

            string baseInterfaceName = null;
            if (interfaceHint != null)
            {
                baseInterfaceName = "I" + className;
            }

            TypeGenerator typeGenerator;
            if (enumHint == null)
            {
                typeGenerator = new ClassGenerator(
                    _rootSchema,
                    baseInterfaceName,
                    _settings.HintDictionary,
                    _settings.GenerateOverrides,
                    _settings.GenerateCloningCode,
                    SyntaxInterfaceTypeName,
                    _kindEnumName);

                // Keep track of any hints that the type generator might encounter in the
                // course of generating the type which require additional types (such as
                // enumerations) to be generated.
                typeGenerator.AdditionalTypeRequired += TypeGenerator_AdditionalTypeRequired;

                if (_settings.GenerateCloningCode)
                {
                    // The cloning code includes an enumeration with one member for each
                    // generated class, so keep track of the class names.
                    _generatedClassNames.Add(className);
                }
            }
            else
            {
                typeGenerator = new EnumGenerator(_settings.HintDictionary);
            }
        
            _pathToFileContentsDictionary[className] = typeGenerator.Generate(
                schema,
                _settings.NamespaceName,
                className,
                _settings.CopyrightNotice,
                schema.Description);

            if (interfaceHint != null)
            {
                typeGenerator = new InterfaceGenerator(_rootSchema, _settings.HintDictionary);
                string description = interfaceHint.Description ?? schema.Description;

                _pathToFileContentsDictionary[baseInterfaceName] = typeGenerator.Generate(
                    schema,
                    _settings.NamespaceName,
                    baseInterfaceName,
                    _settings.CopyrightNotice,
                    description);
            }

            return _pathToFileContentsDictionary[className];
        }

        private void TypeGenerator_AdditionalTypeRequired(object sender, AdditionalTypeRequiredEventArgs e)
        {
            _additionalTypesRequiredList.Add(e);
        }

        private void GenerateAdditionalType(CodeGenHint hint, JsonSchema schema)
        {
            // We do not handle the case where generating an additional type
            // causes still get another type to be generated. It wouldn't be hard
            // to add if needed.
            var enumHint = hint as EnumHint;
            if (enumHint != null)
            {
                GenerateAdditionalTypeFromEnumHint(enumHint, schema);
            }
            else
            {
                throw JSchemaException.Create(
                    Resources.ErrorCannotGenerateAdditionalTypeFromHintType,
                    nameof(CodeGenHint),
                    hint.GetType().Name);
            }
        }

        private void GenerateAdditionalTypeFromEnumHint(EnumHint enumHint, JsonSchema schema)
        {
            if (enumHint.Enum != null
                && schema.Enum != null
                && enumHint.Enum.Length > schema.Enum.Length)
            {
                throw JSchemaException.Create(
                    Resources.ErrorMismatchedEnumCount,
                    nameof(EnumHint),
                    enumHint.TypeName,
                    enumHint.Enum.Length,
                    schema.Enum.Length);
            }

            var enumValues = new List<string>();
            if (!string.IsNullOrWhiteSpace(enumHint.ZeroValue))
            {
                enumValues.Add(enumHint.ZeroValue);
            }

            if (enumHint.Enum != null)
            {
                enumValues.AddRange(enumHint.Enum);
            }
            else
            {
                enumValues.AddRange(schema.Enum.Select(e => e.ToString()));
            }

            var enumTypeSchema = new JsonSchema
            {
                Description = enumHint.Description ?? schema.Description,
                Enum = enumValues.ToArray()
            };

            var generator = new EnumGenerator(_settings.HintDictionary);
            _pathToFileContentsDictionary[enumHint.TypeName] =
                generator.Generate(
                    enumTypeSchema,
                    _settings.NamespaceName,
                    enumHint.TypeName,
                    _settings.CopyrightNotice,
                    enumTypeSchema.Description);
        }
    }
}