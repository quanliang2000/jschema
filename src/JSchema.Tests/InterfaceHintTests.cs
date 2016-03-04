﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.
// Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using FluentAssertions;
using Microsoft.JSchema.Generator;
using Xunit;

namespace Microsoft.JSchema.Tests
{
    public class InterfaceHintTests
    {
        private const string PrimaryOutputFilePath = TestFileSystem.OutputDirectory + "\\" + TestSettings.RootClassName + ".cs";

        private readonly TestFileSystem _testFileSystem;
        private readonly DataModelGeneratorSettings _settings;

        public InterfaceHintTests()
        {
            _testFileSystem = new TestFileSystem();
            _settings = TestSettings.MakeSettings();
        }

        public static readonly object[] TestCases = new object[]
        {
            // We give the
            new object[]
            {
@"{
  ""type"": ""object"",
  ""description"": ""My class with an interface."",
  ""properties"": {
    ""options"": {
      ""description"": ""The options."",
      ""type"": ""object"",
    },
  },
}",

@"{
  ""c"": [
    {
      ""$type"": ""Microsoft.JSchema.Generator.InterfaceHint, Microsoft.JSchema"",
      ""description"": ""My interface.""
    }
  ],
  ""C.Options"": [
    {
      ""$type"": ""Microsoft.JSchema.Generator.DictionaryHint, Microsoft.JSchema"",
    }
  ]
}",

@"using System;
using System.Collections.Generic;

namespace N
{
    /// <summary>
    /// My class with an interface.
    /// </summary>
    public partial class C : IC, IEquatable<C>
    {
        /// <summary>
        /// The options.
        /// </summary>
        public override Dictionary<string, string> Options { get; set; }

        public override bool Equals(object other)
        {
            return Equals(other as C);
        }

        public bool Equals(C other)
        {
            if (other == null)
            {
                return false;
            }

            if (!Object.ReferenceEquals(Options, other.Options))
            {
                if (Options == null || other.Options == null || Options.Count != other.Options.Count)
                {
                    return false;
                }

                foreach (var value_0 in Options)
                {
                    string value_1;
                    if (!other.Options.TryGetValue(value_0.Key, out value_1))
                    {
                        return false;
                    }

                    if (value_0.Value != value_1)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}",

@"using System.Collections.Generic;

namespace N
{
    /// <summary>
    /// My interface.
    /// </summary>
    public interface IC
    {
        /// <summary>
        /// The options.
        /// </summary>
        Dictionary<string, string> Options { get; set; }
    }
}"
            }
        };

        [Theory(DisplayName = "InterfaceHint generates interfaces in addition to classes")]
        [MemberData(nameof(TestCases))]
        public void GeneratesInterfaceFromClass(
            string schemaText,
            string hintsText,
            string classText,
            string interfaceText)
        {
            JsonSchema schema = SchemaReader.ReadSchema(schemaText);
            _settings.HintDictionary = HintDictionary.Deserialize(hintsText);
            var generator = new DataModelGenerator(_settings, _testFileSystem.FileSystem);

            generator.Generate(schema);

            string interfaceFilePath = TestFileSystem.MakeOutputFilePath("I" + _settings.RootClassName);

            var expectedOutputFiles = new List<string>
            {
                PrimaryOutputFilePath,
                interfaceFilePath
            };

            _testFileSystem.Files.Count.Should().Be(expectedOutputFiles.Count);
            _testFileSystem.Files.Should().OnlyContain(key => expectedOutputFiles.Contains(key));

            _testFileSystem[PrimaryOutputFilePath].Should().Be(classText);
            _testFileSystem[interfaceFilePath].Should().Be(interfaceText);
        }
    }
}
