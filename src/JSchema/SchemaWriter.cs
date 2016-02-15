﻿// Copyright (c) Mount Baker Software.  All Rights Reserved.
// Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MountBaker.JSchema
{
    public static class SchemaWriter
    {
        public static void WriteSchema(
            TextWriter writer,
            JsonSchema schema,
            Formatting formatting = Formatting.Indented)
        {
            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Formatting = formatting
            };

            var serializer = JsonSerializer.Create(settings);
            serializer.ContractResolver = new JsonSchemaContractResolver();
            serializer.Converters.Add(
                new StringEnumConverter
                {
                    CamelCaseText = true
                });

            serializer.Serialize(writer, schema);
        }
    }
}