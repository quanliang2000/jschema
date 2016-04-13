﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.
// Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;

namespace Microsoft.Json.Schema.ToDotNet
{
    /// <summary>
    /// Generates a set of .NET classes from a JSON schema.
    /// </summary>
    public class DataModelGenerator
    {
        private readonly DataModelGeneratorSettings _settings;
        private readonly IFileSystem _fileSystem;
        private JsonSchema _rootSchema;
        private Dictionary<string, string> _pathToFileContentsDictionary;
        private List<string> _generatedClassNames;
        private string _kindEnumName;
        private string _nodeInterfaceName;
        private List<AdditionalTypeRequiredInfo> _additionalTypesRequiredList;
        private Dictionary<string, PropertyInfoDictionary> _classInfoDictionary;

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

            _additionalTypesRequiredList = new List<AdditionalTypeRequiredInfo>();
            _generatedClassNames = new List<string>();

            _classInfoDictionary = new Dictionary<string, PropertyInfoDictionary>();
        }

        public string Generate(JsonSchema rootSchema)
        {
            _additionalTypesRequiredList.Clear();

            if (_settings.GenerateCloningCode)
            {
                _kindEnumName = _settings.SchemaName + "NodeKind";
                _nodeInterfaceName = "I" + _settings.SchemaName + "Node";
            }

            _rootSchema = JsonSchema.Collapse(rootSchema);

            if (_fileSystem.DirectoryExists(_settings.OutputDirectory) && !_settings.ForceOverwrite)
            {
                throw Error.CreateException(Resources.ErrorOutputDirectoryExists, _settings.OutputDirectory);
            }

            _fileSystem.CreateDirectory(_settings.OutputDirectory);

            if (_rootSchema.Type != JTokenType.Object)
            {
                throw Error.CreateException(Resources.ErrorNotAnObject, _rootSchema.Type.ToJsonSchemaName());
            }

            string rootFileText = CreateFile(_settings.RootClassName, _rootSchema, _settings.SealClasses);

            if (_rootSchema.Definitions != null)
            {
                foreach (KeyValuePair<string, JsonSchema> definition in _rootSchema.Definitions)
                {
                    CreateFile(definition.Key, definition.Value, _settings.SealClasses);
                }
            }

            foreach (AdditionalTypeRequiredInfo additionalTypeRequiredInfo in _additionalTypesRequiredList)
            {
                GenerateAdditionalType(additionalTypeRequiredInfo.Hint, additionalTypeRequiredInfo.Schema);
            }

            if (_settings.GenerateCloningCode)
            {
                _pathToFileContentsDictionary[_nodeInterfaceName] =
                    GenerateSyntaxInterface(_settings.SchemaName, _kindEnumName, _nodeInterfaceName);

                _pathToFileContentsDictionary[_kindEnumName] =
                    GenerateKindEnum(_kindEnumName);

                string rewritingVisitorClassName = _settings.SchemaName + "RewritingVisitor";
                _pathToFileContentsDictionary[rewritingVisitorClassName] =
                    new RewritingVisitorGenerator(
                        _classInfoDictionary,
                        _settings.CopyrightNotice,
                        _settings.NamespaceName,                        
                        rewritingVisitorClassName,
                        _settings.SchemaName,
                        _kindEnumName,
                        _nodeInterfaceName,
                        _generatedClassNames)
                        .GenerateRewritingVisitor();
            }

            foreach (KeyValuePair<string, string> entry in _pathToFileContentsDictionary)
            {
                _fileSystem.WriteAllText(Path.Combine(_settings.OutputDirectory, entry.Key + ".cs"), entry.Value);
            }

            // Returning the text of the file generated from the root schema allows this method
            // to be more easily unit tested.
            return rootFileText;
        }

        private string GenerateSyntaxInterface(string schemaName, string enumName, string syntaxInterfaceName)
        {
            PropertyDeclarationSyntax syntaxKindPropertyDeclaration =
                SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.ParseTypeName(enumName),
                    enumName)
                    .AddAccessorListAccessors(SyntaxHelper.MakeGetAccessor())
                    .WithLeadingTrivia(
                        SyntaxHelper.MakeDocComment(
                            string.Format(CultureInfo.CurrentCulture, Resources.SyntaxInterfaceKindDescription, syntaxInterfaceName)));

            MethodDeclarationSyntax deepCloneMethodDeclaration =
                SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.ParseTypeName(_nodeInterfaceName),
                    "DeepClone")
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .WithLeadingTrivia(
                        SyntaxHelper.MakeDocComment(Resources.SyntaxInterfaceDeepCloneDescription));

            InterfaceDeclarationSyntax interfaceDeclaration =
                SyntaxFactory.InterfaceDeclaration(_nodeInterfaceName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    .AddMembers(
                        syntaxKindPropertyDeclaration,
                        deepCloneMethodDeclaration);

            string summaryComment = string.Format(
                CultureInfo.CurrentCulture,
                Resources.SyntaxInterfaceDescription,
                schemaName);

            return interfaceDeclaration.Format(
                _settings.CopyrightNotice,
                null, // usings
                _settings.NamespaceName,
                summaryComment);
        }

        private string GenerateKindEnum(string enumName)
        {
            EnumDeclarationSyntax enumDeclaration =
                SyntaxFactory.EnumDeclaration(SyntaxFactory.Identifier(enumName))
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    .AddMembers(
                        SyntaxFactory.EnumMemberDeclaration("None")
                            .WithLeadingTrivia(
                                SyntaxHelper.MakeDocComment(Resources.KindEnumNoneDescription)))
                    .AddMembers(
                        _generatedClassNames.Select(gcn => GenerateKindEnumMember(gcn)).ToArray());

            string summaryComment = string.Format(
                CultureInfo.CurrentCulture,
                Resources.KindEnumDescription,
                _nodeInterfaceName);

            return enumDeclaration.Format(
                _settings.CopyrightNotice,
                null, // usings
                _settings.NamespaceName,
                summaryComment);
        }

        private EnumMemberDeclarationSyntax GenerateKindEnumMember(string className)
        {
            string description = string.Format(
                CultureInfo.CurrentCulture,
                Resources.KindEnumMemberDescription,
                _nodeInterfaceName,
                className);

            return SyntaxFactory.EnumMemberDeclaration(className)
                .WithLeadingTrivia(
                    SyntaxHelper.MakeDocComment(description));
        }

        internal string CreateFile(
            string className,
            JsonSchema schema,
            bool sealClasses)
        {
            className = className.ToPascalCase();

            var propertyInfoDictionary = new PropertyInfoDictionary(
                className,
                schema,
                _settings.HintDictionary,
                OnAdditionalTypeRequired);

            _classInfoDictionary.Add(className, propertyInfoDictionary);

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
                    propertyInfoDictionary,
                    schema,
                    _settings.HintDictionary,
                    baseInterfaceName,
                    _settings.GenerateOverrides,
                    _settings.GenerateCloningCode,
                    _settings.SealClasses,
                    _nodeInterfaceName,
                    _kindEnumName);

                if (_settings.GenerateCloningCode)
                {
                    // The cloning code includes an enumeration with one member for each
                    // generated class, so keep track of the class names.
                    _generatedClassNames.Add(className);
                }
            }
            else
            {
                typeGenerator = new EnumGenerator(schema, _settings.HintDictionary);
            }
        
            _pathToFileContentsDictionary[className] = typeGenerator.Generate(
                _settings.NamespaceName,
                className,
                _settings.CopyrightNotice,
                schema.Description);

            if (interfaceHint != null)
            {
                typeGenerator = new InterfaceGenerator(
                    propertyInfoDictionary,
                    schema,
                    _settings.HintDictionary);
                string description = interfaceHint.Description ?? schema.Description;

                _pathToFileContentsDictionary[baseInterfaceName] = typeGenerator.Generate(
                    _settings.NamespaceName,
                    baseInterfaceName,
                    _settings.CopyrightNotice,
                    description);
            }

            return _pathToFileContentsDictionary[className];
        }

        private void OnAdditionalTypeRequired(AdditionalTypeRequiredInfo e)
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
                throw Error.CreateException(
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
                throw Error.CreateException(
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

            var generator = new EnumGenerator(enumTypeSchema, _settings.HintDictionary);
            _pathToFileContentsDictionary[enumHint.TypeName] =
                generator.Generate(
                    _settings.NamespaceName,
                    enumHint.TypeName,
                    _settings.CopyrightNotice,
                    enumTypeSchema.Description);
        }
    }
}
